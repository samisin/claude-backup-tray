using CommunityToolkit.WinUI.Notifications;
using Microsoft.Extensions.Logging;

namespace ClaudeBackupTray.Services;

public interface INotificationService
{
    /// <summary>Shows a Windows notification with an "Open log" action. Never shown on success.</summary>
    void Notify(string title, string message);

    /// <summary>Wires up the tray icon (for balloon-tip fallback), a UI-thread marshaller, and the open-log action.</summary>
    void AttachTray(NotifyIcon icon, Action<Action> uiInvoke, Action openLog);
}

/// <summary>
/// Prefers a Windows toast (CommunityToolkit) and falls back to a NotifyIcon balloon tip if toast
/// isn't available (e.g. unregistered AppUserModelID on an unpackaged app). Both surface an action
/// that opens the log. Every notification is also written to the log so nothing is lost.
/// </summary>
public sealed class NotificationService(ILogger<NotificationService> log) : INotificationService
{
    private const string OpenLogArgument = "action=openlog";

    private NotifyIcon? _icon;
    private Action<Action>? _uiInvoke;
    private Action? _openLog;
    private bool _toastUsable = true;
    private bool _activationHooked;

    public void AttachTray(NotifyIcon icon, Action<Action> uiInvoke, Action openLog)
    {
        _icon = icon;
        _uiInvoke = uiInvoke;
        _openLog = openLog;

        // Balloon-tip click -> open the log.
        icon.BalloonTipClicked += (_, _) => SafeOpenLog();

        // Toast activation (button click) -> open the log. Best-effort for unpackaged apps.
        if (!_activationHooked)
        {
            try
            {
                ToastNotificationManagerCompat.OnActivated += args =>
                {
                    if (args.Argument.Contains(OpenLogArgument, StringComparison.OrdinalIgnoreCase))
                        SafeOpenLog();
                };
                _activationHooked = true;
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Could not hook toast activation (harmless).");
            }
        }
    }

    public void Notify(string title, string message)
    {
        // Always record it – no silent failures.
        log.LogInformation("NOTIFY: {Title} – {Message}", title, message);

        if (TryToast(title, message))
            return;

        ShowBalloon(title, message);
    }

    private bool TryToast(string title, string message)
    {
        if (!_toastUsable)
            return false;

        try
        {
            new ToastContentBuilder()
                .AddArgument("action", "openlog")
                .AddText(title)
                .AddText(message)
                .AddButton(new ToastButton()
                    .SetContent("Open log")
                    .AddArgument("action", "openlog"))
                .Show();
            return true;
        }
        catch (Exception ex)
        {
            // Toast registration is unavailable; stop trying and use balloon tips from now on.
            _toastUsable = false;
            log.LogDebug(ex, "Toast failed – using balloon-tip fallback from now on.");
            return false;
        }
    }

    private void ShowBalloon(string title, string message)
    {
        NotifyIcon? icon = _icon;
        Action<Action>? invoke = _uiInvoke;
        if (icon is null || invoke is null)
        {
            log.LogWarning("No tray icon attached – cannot show balloon tip for: {Title}", title);
            return;
        }

        invoke(() =>
        {
            try
            {
                icon.BalloonTipTitle = title;
                icon.BalloonTipText = message;
                icon.BalloonTipIcon = ToolTipIcon.Error;
                icon.ShowBalloonTip(10_000);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Could not show balloon tip.");
            }
        });
    }

    private void SafeOpenLog()
    {
        try
        {
            _openLog?.Invoke();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not open the log from the notification.");
        }
    }
}
