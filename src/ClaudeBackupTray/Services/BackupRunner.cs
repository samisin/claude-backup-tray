using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ClaudeBackupTray.Configuration;
using ClaudeBackupTray.Models;
using ClaudeBackupTray.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClaudeBackupTray.Services;

/// <summary>
/// Owns invocation of the external PowerShell backup script: validates config, launches the
/// process with a timeout, parses the machine-readable result line, and translates the outcome
/// into <see cref="AppState"/> changes plus notifications. It never throws – a failed run
/// becomes a red icon + notification, and the next scheduled run still happens.
/// </summary>
public sealed partial class BackupRunner(
    IOptionsMonitor<BackupOptions> options,
    AppState state,
    INotificationService notifications,
    PowerShellResolver powerShell,
    ILogger<BackupRunner> log)
{
    // Only one backup at a time: a manual "Run now" while a scheduled run is in flight is skipped.
    private readonly SemaphoreSlim _gate = new(1, 1);

    [GeneratedRegex(@"CLAUDEBACKUP_RESULT\b.*", RegexOptions.CultureInvariant)]
    private static partial Regex ResultLineRegex();

    public async Task<BackupRunResult> RunAsync(string trigger, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            log.LogInformation("Backup already running – skipping {Trigger} run.", trigger);
            return BackupRunResult.Skipped();
        }

        try
        {
            return await RunCoreAsync(trigger, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Last line of defence: a runner bug must not crash the app or stop the scheduler.
            log.LogError(ex, "Unexpected error in the backup run.");
            state.SetFailed(ex.Message, "Unexpected error", DateTimeOffset.Now);
            notifications.Notify("Backup failed", $"Unexpected error: {ex.Message}");
            return new BackupRunResult(RunOutcome.Failed, 0, 0, 0, 0, -1, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<BackupRunResult> RunCoreAsync(string trigger, CancellationToken cancellationToken)
    {
        BackupOptions opt = options.CurrentValue;
        state.SetRunning();
        log.LogInformation("Backup starting ({Trigger}).", trigger);

        // --- validate configuration ------------------------------------------------
        string scriptPath = ResolveScriptPath(opt.ScriptPath);
        if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
            return Fail($"Backup script not found: '{scriptPath}'.", "Script missing");

        string targetRoot = Environment.ExpandEnvironmentVariables(opt.TargetRoot?.Trim() ?? "");
        if (string.IsNullOrWhiteSpace(targetRoot))
            return Fail("TargetRoot is missing in appsettings.json.", "Target missing");

        string? psExe = powerShell.Resolve(opt.PowerShellExe);
        if (psExe is null)
            return Fail("No PowerShell found (pwsh.exe/powershell.exe).", "PowerShell missing");

        string[] sources = (opt.Sources ?? [])
            .Select(s => Environment.ExpandEnvironmentVariables(s?.Trim() ?? ""))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();
        if (sources.Length == 0)
            return Fail("No source folders (Sources) configured.", "Sources missing");

        foreach (string s in sources)
        {
            if (!Directory.Exists(s))
                log.LogWarning("Source folder missing (the script will skip it): {Source}", s);
        }

        try
        {
            Directory.CreateDirectory(targetRoot);
        }
        catch (Exception ex)
        {
            return Fail($"Cannot create/open the target folder '{targetRoot}': {ex.Message}", "Target unavailable");
        }

        // --- launch the script -----------------------------------------------------
        var psi = new ProcessStartInfo
        {
            FileName = psExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? AppContext.BaseDirectory,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-NoLogo");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add("-TargetRoot");
        psi.ArgumentList.Add(targetRoot);
        psi.ArgumentList.Add("-MinJsonlCount");
        psi.ArgumentList.Add(opt.MinJsonlCount.ToString());
        // Sources are passed positionally as the trailing arguments (no "-Sources" token): the
        // script's ValueFromRemainingArguments parameter collects them. Absolute paths never
        // start with '-', so they won't be mistaken for switches.
        foreach (string s in sources)
            psi.ArgumentList.Add(s);

        log.LogInformation("Running: {Exe} -File {Script} -TargetRoot {Target} ({SourceCount} sources)",
            psExe, scriptPath, targetRoot, sources.Length);

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return Fail($"Could not start PowerShell: {ex.Message}", "Start failed");
        }

        // Read both streams concurrently to avoid pipe-buffer deadlocks.
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        int timeoutSeconds = opt.ScriptTimeoutSeconds > 0 ? opt.ScriptTimeoutSeconds : 300;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            TryKill(process);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw; // app is shutting down
        }

        string stdout = await SafeRead(stdoutTask).ConfigureAwait(false);
        string stderr = await SafeRead(stderrTask).ConfigureAwait(false);
        stopwatch.Stop();

        if (timedOut)
            return Fail($"The script exceeded the {timeoutSeconds}s timeout and was aborted.", "Timeout",
                stdout, stderr);

        int exitCode = process.ExitCode;
        (int copiedJsonl, int totalJsonl, int copiedFiles, long copiedBytes) = ParseResult(stdout);

        log.LogInformation(
            "Script finished: exit={ExitCode}, copied={CopiedFiles} files ({CopiedJsonl} .jsonl), " +
            "{TotalJsonl} .jsonl total in snapshot, {Mb:F2} MB, took {Seconds:F1}s.",
            exitCode, copiedFiles, copiedJsonl, totalJsonl, copiedBytes / 1024.0 / 1024.0, stopwatch.Elapsed.TotalSeconds);

        if (!string.IsNullOrWhiteSpace(stderr))
            log.LogWarning("Script stderr:{NewLine}{Stderr}", Environment.NewLine, stderr.Trim());

        if (exitCode != 0)
            return Fail($"The backup script returned exit code {exitCode}. {FirstLine(stderr)}", $"Exit code {exitCode}",
                stdout, stderr, copiedJsonl, totalJsonl, copiedFiles, copiedBytes, exitCode);

        // Exit 0 but suspiciously empty backup -> health alarm (red + notify), not counted as success.
        if (opt.MinJsonlCount > 0 && totalJsonl < opt.MinJsonlCount)
        {
            string msg = $"Only {totalJsonl} .jsonl in the backup (threshold {opt.MinJsonlCount}). Suspected empty backup.";
            log.LogWarning(msg);
            state.SetFailed(msg, "Too few .jsonl", DateTimeOffset.Now);
            notifications.Notify("Suspected empty backup", msg);
            return new BackupRunResult(RunOutcome.Failed, copiedJsonl, totalJsonl, copiedFiles, copiedBytes, exitCode, msg);
        }

        string okText = $"OK – {copiedFiles} new files ({copiedJsonl} .jsonl), {totalJsonl} .jsonl total.";
        state.SetSuccess(copiedJsonl, totalJsonl, okText, DateTimeOffset.Now);
        log.LogInformation("Backup succeeded. {Text}", okText);
        return new BackupRunResult(RunOutcome.Success, copiedJsonl, totalJsonl, copiedFiles, copiedBytes, exitCode, okText);
    }

    private string ResolveScriptPath(string configured)
    {
        configured = Environment.ExpandEnvironmentVariables(configured?.Trim() ?? "");
        if (string.IsNullOrEmpty(configured))
            return "";
        return Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured));
    }

    private BackupRunResult Fail(string error, string shortText,
        string? stdout = null, string? stderr = null,
        int copiedJsonl = 0, int totalJsonl = 0, int copiedFiles = 0, long copiedBytes = 0, int exitCode = -1)
    {
        log.LogError("Backup failed: {Error}", error);
        if (!string.IsNullOrWhiteSpace(stdout))
            log.LogDebug("Script stdout:{NewLine}{Stdout}", Environment.NewLine, stdout.Trim());
        if (!string.IsNullOrWhiteSpace(stderr))
            log.LogWarning("Script stderr:{NewLine}{Stderr}", Environment.NewLine, stderr.Trim());

        state.SetFailed(error, shortText, DateTimeOffset.Now);
        notifications.Notify("Backup failed", error);
        return new BackupRunResult(RunOutcome.Failed, copiedJsonl, totalJsonl, copiedFiles, copiedBytes, exitCode, error);
    }

    private (int copiedJsonl, int totalJsonl, int copiedFiles, long copiedBytes) ParseResult(string stdout)
    {
        string? line = null;
        foreach (Match m in ResultLineRegex().Matches(stdout))
            line = m.Value; // keep the last match

        if (line is null)
        {
            log.LogWarning("No CLAUDEBACKUP_RESULT line found in the script output.");
            return (0, 0, 0, 0);
        }

        int GetInt(string key) =>
            int.TryParse(Regex.Match(line, $@"\b{key}=(\d+)").Groups[1].Value, out int v) ? v : 0;
        long GetLong(string key) =>
            long.TryParse(Regex.Match(line, $@"\b{key}=(\d+)").Groups[1].Value, out long v) ? v : 0;

        return (GetInt("CopiedJsonl"), GetInt("TotalJsonl"), GetInt("CopiedFiles"), GetLong("TotalBytes"));
    }

    private void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not terminate the script process.");
        }
    }

    private static async Task<string> SafeRead(Task<string> readTask)
    {
        try
        {
            return await readTask.ConfigureAwait(false);
        }
        catch
        {
            return "";
        }
    }

    private static string FirstLine(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "" : text.Trim().Split('\n', '\r').FirstOrDefault(l => l.Length > 0)?.Trim() ?? "";
}
