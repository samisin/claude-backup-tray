namespace ClaudeBackupTray.Configuration;

/// <summary>
/// Strongly-typed view of the "Backup" section in appsettings.json.
/// Bound via <c>IOptionsMonitor</c> so edits are picked up live (no restart).
/// </summary>
public sealed class BackupOptions
{
    public const string SectionName = "Backup";

    /// <summary>Path to the PowerShell backup script. May be relative to the app directory.</summary>
    public string ScriptPath { get; set; } = "scripts\\backup.ps1";

    /// <summary>PowerShell executable to use. Falls back to powershell.exe if pwsh.exe is missing.</summary>
    public string PowerShellExe { get; set; } = "pwsh.exe";

    /// <summary>Root directory the backup copies into. Should live on a different disk than the sources.</summary>
    public string TargetRoot { get; set; } = "";

    /// <summary>Source folders to back up. Environment variables (e.g. %USERPROFILE%) are expanded.</summary>
    public string[] Sources { get; set; } = [];

    /// <summary>Cron expression (Cronos syntax), evaluated in local time. 5 fields = minute precision, 6 = with seconds.</summary>
    public string Schedule { get; set; } = "*/30 * * * *";

    /// <summary>Run a backup immediately when the app starts, in addition to the schedule. Default true.</summary>
    public bool RunOnStartup { get; set; } = true;

    /// <summary>Alarm if the snapshot ends up with fewer than this many .jsonl files. 0 disables the check.</summary>
    public int MinJsonlCount { get; set; }

    /// <summary>Alarm if no successful run has happened within this many hours.</summary>
    public int StaleAfterHours { get; set; } = 6;

    /// <summary>Kill the script and treat the run as failed after this many seconds.</summary>
    public int ScriptTimeoutSeconds { get; set; } = 300;
}
