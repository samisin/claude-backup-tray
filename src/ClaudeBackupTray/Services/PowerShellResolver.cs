using Microsoft.Extensions.Logging;

namespace ClaudeBackupTray.Services;

/// <summary>
/// Resolves the PowerShell executable to launch. Tries, in order:
/// the configured value (as an absolute path, then on PATH), then known PowerShell 7
/// install locations, and finally Windows PowerShell (powershell.exe), which always exists.
/// </summary>
public sealed class PowerShellResolver(ILogger<PowerShellResolver> log)
{
    private static readonly string[] WellKnownPwsh =
    [
        @"%ProgramFiles%\PowerShell\7\pwsh.exe",
        @"%ProgramFiles(x86)%\PowerShell\7\pwsh.exe",
        @"%LOCALAPPDATA%\Microsoft\WindowsApps\pwsh.exe",
    ];

    /// <summary>Returns the full path to a usable PowerShell host, or null if none was found.</summary>
    public string? Resolve(string configured)
    {
        configured = Environment.ExpandEnvironmentVariables(configured?.Trim() ?? "");

        if (!string.IsNullOrEmpty(configured))
        {
            if (Path.IsPathRooted(configured) && File.Exists(configured))
                return configured;

            string? onPath = FindOnPath(configured);
            if (onPath is not null)
                return onPath;

            log.LogWarning("Configured PowerShell '{Exe}' not found – trying fallback.", configured);
        }

        foreach (string candidate in WellKnownPwsh)
        {
            string expanded = Environment.ExpandEnvironmentVariables(candidate);
            if (File.Exists(expanded))
                return expanded;
        }

        string? windowsPs = FindOnPath("powershell.exe")
            ?? Environment.ExpandEnvironmentVariables(@"%WINDIR%\System32\WindowsPowerShell\v1.0\powershell.exe");
        if (File.Exists(windowsPs))
        {
            log.LogInformation("Using Windows PowerShell as fallback: {Path}", windowsPs);
            return windowsPs;
        }

        return null;
    }

    private static string? FindOnPath(string exe)
    {
        if (!exe.Contains('.'))
            exe += ".exe";

        string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(dir, exe);
            }
            catch (ArgumentException)
            {
                continue; // malformed PATH entry
            }

            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
