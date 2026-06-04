using System.Diagnostics;
using ClaudeBackupTray.Configuration;
using ClaudeBackupTray.Models;
using ClaudeBackupTray.Services;
using ClaudeBackupTray.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClaudeBackupTray.Tray;

/// <summary>
/// The tray UI. Constructed on the UI (STA) thread before <c>Application.Run</c>. Owns the
/// <see cref="NotifyIcon"/>, the right-click menu, and the colour/tooltip rendering driven by
/// <see cref="AppState"/>. State changes arrive on background threads and are marshalled to the
/// UI thread via a hidden control.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppState _state;
    private readonly BackupRunner _runner;
    private readonly IOptionsMonitor<BackupOptions> _options;
    private readonly INotificationService _notifications;
    private readonly AppPaths _paths;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<TrayApplicationContext> _log;

    private readonly Control _marshal;
    private readonly NotifyIcon _notifyIcon;
    private readonly Dictionary<BackupStatus, Icon> _icons;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _runNowItem;
    private bool _exiting;

    public TrayApplicationContext(
        AppState state,
        BackupRunner runner,
        IOptionsMonitor<BackupOptions> options,
        INotificationService notifications,
        AppPaths paths,
        IHostApplicationLifetime lifetime,
        ILogger<TrayApplicationContext> log)
    {
        _state = state;
        _runner = runner;
        _options = options;
        _notifications = notifications;
        _paths = paths;
        _lifetime = lifetime;
        _log = log;

        // Hidden control used purely to marshal background-thread updates onto the UI thread.
        _marshal = new Control();
        _ = _marshal.Handle; // force handle creation now (we are on the UI thread)

        _icons = TrayIconFactory.CreateAll();

        var menu = new ContextMenuStrip();
        _runNowItem = new ToolStripMenuItem("Run backup now", null, OnRunNow);
        menu.Items.Add(_runNowItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open backup script", null, (_, _) => OpenInEditor(ResolveScriptPath(), "backup script"));
        menu.Items.Add("Open settings", null, (_, _) => OpenInEditor(_paths.SettingsFilePath, "settings"));
        menu.Items.Add("Open backup folder", null, (_, _) => OpenFolder(ExpandedTargetRoot(), "backup folder"));
        menu.Items.Add("Open log", null, (_, _) => OpenLog());
        menu.Items.Add(new ToolStripSeparator());
        _statusItem = new ToolStripMenuItem("Status: loading…") { Enabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, OnExit);

        _notifyIcon = new NotifyIcon
        {
            Icon = _icons[BackupStatus.Idle],
            Text = "Claude Backup Tray",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notifyIcon.DoubleClick += (_, _) => OpenFolder(ExpandedTargetRoot(), "backup folder");

        _notifications.AttachTray(_notifyIcon, Post, OpenLog);

        _state.Changed += OnStateChanged;
        Render(_state.Snapshot());

        _log.LogInformation("Tray icon initialised.");
    }

    // ---- state rendering ------------------------------------------------------

    private void OnStateChanged(object? sender, EventArgs e) => Post(() => Render(_state.Snapshot()));

    private void Render(AppStateSnapshot s)
    {
        if (_exiting)
            return;

        if (_icons.TryGetValue(s.Status, out Icon? icon))
            _notifyIcon.Icon = icon;

        _notifyIcon.Text = Truncate(BuildTooltip(s), 127);
        _statusItem.Text = BuildStatusLine(s);
    }

    private static string BuildTooltip(AppStateSnapshot s)
    {
        string last = s.LastRunTime is { } lr ? lr.LocalDateTime.ToString("MM-dd HH:mm") : "—";
        string next = s.NextRunTime is { } nr ? nr.LocalDateTime.ToString("MM-dd HH:mm") : "—";
        return $"Claude Backup – {StatusWord(s.Status)}\n" +
               $"Last: {last}\n" +
               $".jsonl: {s.CopiedJsonl} new / {s.TotalJsonl} total\n" +
               $"Next: {next}";
    }

    private static string BuildStatusLine(AppStateSnapshot s)
    {
        // Short status line in the menu: just status + time (last run, otherwise next).
        DateTimeOffset? when = s.LastRunTime ?? s.NextRunTime;
        return when is { } w
            ? $"Status: {StatusWord(s.Status)} {w.LocalDateTime:yyyy-MM-dd HH:mm}"
            : $"Status: {StatusWord(s.Status)}";
    }

    private static string StatusWord(BackupStatus status) => status switch
    {
        BackupStatus.Idle => "Idle",
        BackupStatus.Running => "Running…",
        BackupStatus.Success => "Succeeded",
        BackupStatus.Failed => "FAILED",
        _ => status.ToString(),
    };

    // ---- menu actions ---------------------------------------------------------

    private void OnRunNow(object? sender, EventArgs e)
    {
        _log.LogInformation("Manual run requested from the tray menu.");
        _ = Task.Run(async () =>
        {
            try
            {
                await _runner.RunAsync("manual", _lifetime.ApplicationStopping).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error during manual run.");
            }
        });
    }

    private void OpenInEditor(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _notifications.Notify($"Cannot open {label}", $"File does not exist: {path}");
            return;
        }

        // Prefer the "edit" verb so we land in an editor rather than executing the .ps1.
        if (TryStart(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "edit" }))
            return;
        if (TryStart(new ProcessStartInfo(path) { UseShellExecute = true }))
            return;
        if (TryStart(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true }))
            return;

        _notifications.Notify($"Cannot open {label}", $"No editor could be started for: {path}");
    }

    private void OpenFolder(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            _notifications.Notify($"Cannot open {label}", "The path is empty.");
            return;
        }

        try
        {
            Directory.CreateDirectory(path); // backup root may not exist until the first run
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not create the folder {Path}.", path);
        }

        if (!TryStart(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }))
            _notifications.Notify($"Cannot open {label}", $"Explorer could not open: {path}");
    }

    private void OpenLog()
    {
        string dir = _paths.LogDirectory;
        try
        {
            Directory.CreateDirectory(dir);
            // Open the newest log file if there is one, otherwise the folder.
            string? newest = Directory.Exists(dir)
                ? new DirectoryInfo(dir).GetFiles("*.log")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault()?.FullName
                : null;

            if (newest is not null && TryStart(new ProcessStartInfo(newest) { UseShellExecute = true }))
                return;
            if (TryStart(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true }))
                return;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not open the log in {Dir}.", dir);
        }
    }

    private bool TryStart(ProcessStartInfo psi)
    {
        try
        {
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Process.Start failed for {File} {Args}.", psi.FileName, psi.Arguments);
            return false;
        }
    }

    private void OnExit(object? sender, EventArgs e)
    {
        if (_exiting)
            return;
        _exiting = true;

        _log.LogInformation("Exit selected – shutting down the tray app.");
        _notifyIcon.Visible = false;
        ExitThread(); // ends Application.Run; Program then stops the host
    }

    // ---- helpers --------------------------------------------------------------

    private string ResolveScriptPath()
    {
        string configured = Environment.ExpandEnvironmentVariables(_options.CurrentValue.ScriptPath?.Trim() ?? "");
        if (string.IsNullOrEmpty(configured))
            return "";
        return Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured));
    }

    private string ExpandedTargetRoot() =>
        Environment.ExpandEnvironmentVariables(_options.CurrentValue.TargetRoot?.Trim() ?? "");

    private void Post(Action action)
    {
        if (_exiting)
            return;
        try
        {
            if (_marshal.IsHandleCreated && !_marshal.IsDisposed)
                _marshal.BeginInvoke(action);
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _state.Changed -= OnStateChanged;
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            foreach (Icon icon in _icons.Values)
                TrayIconFactory.Destroy(icon);
            _marshal.Dispose();
        }

        base.Dispose(disposing);
    }
}
