using ClaudeBackupTray.Configuration;
using ClaudeBackupTray.State;
using Cronos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClaudeBackupTray.Services;

/// <summary>
/// Computes the next run time from the cron expression (Cronos, evaluated in LOCAL time) and
/// triggers <see cref="BackupRunner"/> when it elapses. Picks up schedule edits live via
/// <see cref="IOptionsMonitor{TOptions}"/>; an invalid cron expression raises an alarm and the
/// last valid schedule keeps running.
/// </summary>
public sealed class SchedulerService : BackgroundService
{
    private static readonly TimeSpan MaxSingleWait = TimeSpan.FromHours(12);

    private readonly IOptionsMonitor<BackupOptions> _options;
    private readonly BackupRunner _runner;
    private readonly AppState _state;
    private readonly INotificationService _notifications;
    private readonly ILogger<SchedulerService> _log;
    private readonly TimeZoneInfo _timeZone = TimeZoneInfo.Local;
    private readonly IDisposable? _onChange;

    private CronExpression? _lastValid;
    private string? _lastConfigError;
    private CancellationTokenSource _reload = new();

    public SchedulerService(
        IOptionsMonitor<BackupOptions> options,
        BackupRunner runner,
        AppState state,
        INotificationService notifications,
        ILogger<SchedulerService> log)
    {
        _options = options;
        _runner = runner;
        _state = state;
        _notifications = notifications;
        _log = log;
        _onChange = _options.OnChange(_ => TriggerReload());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Scheduler started. Time zone for cron: {Tz} ({Offset}).",
            _timeZone.Id, _timeZone.BaseUtcOffset);

        if (_options.CurrentValue.RunOnStartup)
        {
            _log.LogInformation("Running a backup immediately at startup (RunOnStartup=true).");
            await SafeRun("startup", stoppingToken).ConfigureAwait(false);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            CronExpression? expression = GetExpression();
            if (expression is null)
            {
                // No valid schedule yet. Wait briefly (or until config changes) and retry.
                await DelayOrReload(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
                continue;
            }

            DateTimeOffset? next = expression.GetNextOccurrence(DateTimeOffset.Now, _timeZone);
            string scheduleText = _options.CurrentValue.Schedule;
            _state.SetNextRun(next, scheduleText);

            if (next is null)
            {
                _log.LogWarning("The cron expression '{Cron}' yields no future run.", scheduleText);
                await DelayOrReload(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false);
                continue;
            }

            _log.LogInformation("Next run: {Next:yyyy-MM-dd HH:mm:ss} (local time).", next.Value.LocalDateTime);

            TimeSpan wait = next.Value - DateTimeOffset.Now;
            if (wait < TimeSpan.Zero)
                wait = TimeSpan.Zero;

            // Cap a single wait so live reloads and clock changes are re-evaluated regularly.
            bool capped = wait > MaxSingleWait;
            bool reloaded = await DelayOrReload(capped ? MaxSingleWait : wait, stoppingToken).ConfigureAwait(false);

            if (stoppingToken.IsCancellationRequested)
                break;
            if (reloaded || capped)
                continue; // schedule changed or wait was capped -> recompute next occurrence

            await SafeRun("scheduled", stoppingToken).ConfigureAwait(false);
        }

        _log.LogInformation("Scheduler stopped.");
    }

    private CronExpression? GetExpression()
    {
        string raw = _options.CurrentValue.Schedule?.Trim() ?? "";

        if (string.IsNullOrEmpty(raw))
        {
            ReportConfigError("The schedule (cron) is missing in appsettings.json. Using the last valid schedule.");
            return _lastValid;
        }

        try
        {
            int fields = raw.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
            CronFormat format = fields >= 6 ? CronFormat.IncludeSeconds : CronFormat.Standard;
            CronExpression expression = CronExpression.Parse(raw, format);

            _lastValid = expression;
            ClearConfigError();
            return expression;
        }
        catch (CronFormatException ex)
        {
            _log.LogError(ex, "Invalid cron expression: '{Cron}'.", raw);
            ReportConfigError($"Invalid cron expression: '{raw}'. {(_lastValid is null ? "No valid schedule to fall back to." : "Using the last valid schedule.")}");
            _state.SetFailed($"Invalid cron expression: '{raw}'.", "Invalid schedule");
            return _lastValid;
        }
    }

    /// <summary>Waits for the delay, returning true if interrupted by a config reload, false if it elapsed (or we are stopping).</summary>
    private async Task<bool> DelayOrReload(TimeSpan delay, CancellationToken stoppingToken)
    {
        CancellationTokenSource reloadCts = Volatile.Read(ref _reload);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, reloadCts.Token);
        try
        {
            await Task.Delay(delay, linked.Token).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            return !stoppingToken.IsCancellationRequested; // cancelled by reload, not by shutdown
        }
    }

    private void TriggerReload()
    {
        _log.LogInformation("Configuration changed – recomputing the schedule.");
        CancellationTokenSource previous = Interlocked.Exchange(ref _reload, new CancellationTokenSource());
        try
        {
            previous.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // ignore
        }
    }

    private async Task SafeRun(string trigger, CancellationToken stoppingToken)
    {
        try
        {
            await _runner.RunAsync(trigger, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            // RunAsync already guards itself; this is belt-and-suspenders so the loop survives.
            _log.LogError(ex, "Unexpected error during the scheduled run – the scheduler continues.");
        }
    }

    private void ReportConfigError(string message)
    {
        if (_lastConfigError == message)
            return; // already alarmed for this exact problem

        _lastConfigError = message;
        _log.LogError("Configuration error: {Message}", message);
        _notifications.Notify("Invalid configuration", message);
    }

    private void ClearConfigError() => _lastConfigError = null;

    public override void Dispose()
    {
        _onChange?.Dispose();
        base.Dispose();
    }
}
