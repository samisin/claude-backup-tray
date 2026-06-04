using ClaudeBackupTray.Configuration;
using ClaudeBackupTray.Models;
using ClaudeBackupTray.Services;
using ClaudeBackupTray.State;
using ClaudeBackupTray.Tray;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace ClaudeBackupTray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Single instance per logged-on user session (the app is per-user by design).
        using var mutex = new Mutex(initiallyOwned: true, name: @"Local\ClaudeBackupTray.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Claude Backup Tray is already running.", "Claude Backup Tray",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        // Load config from a fixed absolute path next to the exe, regardless of the working
        // directory the Task Scheduler launches us with. reloadOnChange powers IOptionsMonitor.
        string baseDir = AppContext.BaseDirectory;
        string settingsPath = Path.Combine(baseDir, "appsettings.json");

        IConfigurationRoot bootstrapConfig = new ConfigurationBuilder()
            .SetBasePath(baseDir)
            .AddJsonFile(settingsPath, optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "CLAUDEBACKUP_")
            .Build();

        LoggingOptions logOptions = bootstrapConfig.GetSection(LoggingOptions.SectionName).Get<LoggingOptions>() ?? new LoggingOptions();
        string logDir = string.IsNullOrWhiteSpace(logOptions.LogPath)
            ? Path.Combine(baseDir, "_logs")
            : Environment.ExpandEnvironmentVariables(logOptions.LogPath);
        Directory.CreateDirectory(logDir);

        var levelSwitch = new LoggingLevelSwitch(ParseLevel(logOptions.Level));
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitch)
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: Path.Combine(logDir, "claude-backup-.log"),
                rollingInterval: RollingInterval.Day,
                shared: true,
                retainedFileCountLimit: 31,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            Log.Information("=== Claude Backup Tray starting (v{Version}) ===",
                typeof(Program).Assembly.GetName().Version?.ToString() ?? "?");

            ApplicationConfiguration.Initialize();

            HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

            // Own the configuration sources so paths resolve from the exe directory.
            builder.Configuration.Sources.Clear();
            builder.Configuration
                .SetBasePath(baseDir)
                .AddJsonFile(settingsPath, optional: false, reloadOnChange: true)
                .AddEnvironmentVariables(prefix: "CLAUDEBACKUP_");

            // Serilog is the only logging provider (file sink). No console: this is a WinExe.
            builder.Logging.ClearProviders();
            builder.Logging.AddSerilog(Log.Logger, dispose: false);

            // A crash in a background service must not tear down the host (robustness requirement).
            builder.Services.Configure<HostOptions>(o =>
                o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

            builder.Services.Configure<BackupOptions>(builder.Configuration.GetSection(BackupOptions.SectionName));
            builder.Services.Configure<LoggingOptions>(builder.Configuration.GetSection(LoggingOptions.SectionName));

            builder.Services.AddSingleton(new AppPaths(settingsPath, logDir));
            builder.Services.AddSingleton<AppState>();
            builder.Services.AddSingleton<INotificationService, NotificationService>();
            builder.Services.AddSingleton<PowerShellResolver>();
            builder.Services.AddSingleton<BackupRunner>();
            builder.Services.AddSingleton<TrayApplicationContext>();

            builder.Services.AddHostedService<SchedulerService>();
            builder.Services.AddHostedService<HealthMonitorService>();

            using IHost host = builder.Build();

            // Live log-level changes (only Schedule is required to be live, but this is cheap).
            host.Services.GetRequiredService<IOptionsMonitor<LoggingOptions>>()
                .OnChange(o => levelSwitch.MinimumLevel = ParseLevel(o.Level));

            host.Start(); // starts the scheduler + health monitor

            var tray = host.Services.GetRequiredService<TrayApplicationContext>();

            // Blocks on the UI thread until "Exit" ends the message loop.
            Application.Run(tray);

            Log.Information("Tray loop ended – stopping host.");
            host.StopAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Unexpected error in Main – the app is exiting.");
            try
            {
                MessageBox.Show($"Claude Backup Tray could not start:\n{ex.Message}", "Claude Backup Tray",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch
            {
                // no UI available; the log already has the details
            }

            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
            GC.KeepAlive(mutex);
        }
    }

    private static LogEventLevel ParseLevel(string? level) =>
        Enum.TryParse(level, ignoreCase: true, out LogEventLevel parsed) ? parsed : LogEventLevel.Information;
}
