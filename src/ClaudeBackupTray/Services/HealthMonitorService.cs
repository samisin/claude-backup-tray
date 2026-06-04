using ClaudeBackupTray.Configuration;
using ClaudeBackupTray.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClaudeBackupTray.Services;

/// <summary>
/// Periodically checks that a successful backup has happened recently. If the last success is
/// older than <see cref="BackupOptions.StaleAfterHours"/>, it raises the icon to red and fires a
/// single notification (re-armed once a fresh backup succeeds). The app's own start time is the
/// baseline before the first run, so a freshly launched app isn't immediately flagged stale.
/// </summary>
public sealed class HealthMonitorService(
    IOptionsMonitor<BackupOptions> options,
    AppState state,
    INotificationService notifications,
    ILogger<HealthMonitorService> log) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTimeOffset baseline = DateTimeOffset.Now;
        bool staleAlerted = false;

        using var timer = new PeriodicTimer(CheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                int staleAfterHours = Math.Max(1, options.CurrentValue.StaleAfterHours);
                AppStateSnapshot snapshot = state.Snapshot();
                DateTimeOffset lastGood = snapshot.LastSuccessTime ?? baseline;
                TimeSpan age = DateTimeOffset.Now - lastGood;

                if (age > TimeSpan.FromHours(staleAfterHours))
                {
                    if (!staleAlerted)
                    {
                        staleAlerted = true;
                        string message = snapshot.LastSuccessTime is null
                            ? $"No successful backup since the app started {age.TotalHours:F1} h ago (limit {staleAfterHours} h)."
                            : $"No successful backup for {age.TotalHours:F1} h (limit {staleAfterHours} h).";
                        log.LogWarning("Stale alarm: {Message}", message);
                        state.SetFailed(message, "Stale backup");
                        notifications.Notify("Backup is stale", message);
                    }
                }
                else if (staleAlerted)
                {
                    staleAlerted = false; // a fresh success cleared the staleness; re-arm the alarm
                    log.LogInformation("Stale alarm cleared – a fresh backup succeeded.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }

        log.LogInformation("Health monitor stopped.");
    }
}
