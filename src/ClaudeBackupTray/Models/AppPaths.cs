namespace ClaudeBackupTray.Models;

/// <summary>
/// Fixed, resolved-at-startup paths the UI needs (the actual config file that was loaded,
/// and the log directory). Source/target paths are read live from options instead.
/// </summary>
public sealed record AppPaths(string SettingsFilePath, string LogDirectory);
