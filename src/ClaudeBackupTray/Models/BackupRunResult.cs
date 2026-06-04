namespace ClaudeBackupTray.Models;

public enum RunOutcome
{
    Success,
    Failed,
    Skipped,
}

/// <summary>Outcome of a single backup invocation.</summary>
public sealed record BackupRunResult(
    RunOutcome Outcome,
    int CopiedJsonl,
    int TotalJsonl,
    int CopiedFiles,
    long CopiedBytes,
    int ExitCode,
    string Message)
{
    public static BackupRunResult Skipped() =>
        new(RunOutcome.Skipped, 0, 0, 0, 0, 0, "Skipped – a run is already in progress.");
}
