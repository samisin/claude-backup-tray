namespace ClaudeBackupTray.State;

/// <summary>Drives the tray icon colour.</summary>
public enum BackupStatus
{
    /// <summary>Grey – never run yet, waiting for the first scheduled run.</summary>
    Idle,

    /// <summary>Orange – a backup is running right now.</summary>
    Running,

    /// <summary>Green – the most recent run succeeded.</summary>
    Success,

    /// <summary>Red – the most recent run failed, or a health check tripped.</summary>
    Failed,
}

/// <summary>Immutable point-in-time view of <see cref="AppState"/> for the UI to render.</summary>
public sealed record AppStateSnapshot(
    BackupStatus Status,
    DateTimeOffset? LastRunTime,
    DateTimeOffset? LastSuccessTime,
    DateTimeOffset? NextRunTime,
    string ResultText,
    int CopiedJsonl,
    int TotalJsonl,
    string? Error,
    string ScheduleText);

/// <summary>
/// Thread-safe singleton shared by the scheduler, the backup runner, the health monitor
/// and the tray UI. Every mutation raises <see cref="Changed"/> so the tray can re-render.
/// </summary>
public sealed class AppState
{
    private readonly Lock _gate = new();

    private BackupStatus _status = BackupStatus.Idle;
    private DateTimeOffset? _lastRunTime;
    private DateTimeOffset? _lastSuccessTime;
    private DateTimeOffset? _nextRunTime;
    private string _resultText = "No run yet.";
    private int _copiedJsonl;
    private int _totalJsonl;
    private string? _error;
    private string _scheduleText = "";

    /// <summary>Raised (off any thread) after any state change. Handlers must marshal to the UI thread themselves.</summary>
    public event EventHandler? Changed;

    public AppStateSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new AppStateSnapshot(
                _status, _lastRunTime, _lastSuccessTime, _nextRunTime,
                _resultText, _copiedJsonl, _totalJsonl, _error, _scheduleText);
        }
    }

    public void SetRunning()
    {
        lock (_gate)
        {
            _status = BackupStatus.Running;
            _resultText = "Backup running…";
            _error = null;
        }
        Raise();
    }

    public void SetSuccess(int copiedJsonl, int totalJsonl, string resultText, DateTimeOffset when)
    {
        lock (_gate)
        {
            _status = BackupStatus.Success;
            _lastRunTime = when;
            _lastSuccessTime = when;
            _copiedJsonl = copiedJsonl;
            _totalJsonl = totalJsonl;
            _resultText = resultText;
            _error = null;
        }
        Raise();
    }

    /// <summary>Marks the most recent run/health check as failed. Does NOT update the last-success time.</summary>
    public void SetFailed(string error, string resultText, DateTimeOffset? when = null)
    {
        lock (_gate)
        {
            _status = BackupStatus.Failed;
            if (when is not null)
                _lastRunTime = when;
            _resultText = resultText;
            _error = error;
        }
        Raise();
    }

    public void SetNextRun(DateTimeOffset? next, string scheduleText)
    {
        lock (_gate)
        {
            _nextRunTime = next;
            _scheduleText = scheduleText;
        }
        Raise();
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
