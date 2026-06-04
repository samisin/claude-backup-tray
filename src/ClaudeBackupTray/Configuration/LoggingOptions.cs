namespace ClaudeBackupTray.Configuration;

/// <summary>
/// Strongly-typed view of the "Logging" section in appsettings.json.
/// Used to configure the Serilog rolling-file sink.
/// </summary>
public sealed class LoggingOptions
{
    public const string SectionName = "Logging";

    /// <summary>Directory for the daily rolling log files. Environment variables are expanded.</summary>
    public string LogPath { get; set; } = "";

    /// <summary>Minimum log level (Verbose, Debug, Information, Warning, Error, Fatal).</summary>
    public string Level { get; set; } = "Information";
}
