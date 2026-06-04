<#
.SYNOPSIS
    Removes Claude Backup Tray's scheduled task and closes any running instance.

.PARAMETER TaskName
    Name of the scheduled task (default: ClaudeBackupTray).

.EXAMPLE
    .\Uninstall.ps1
#>
[CmdletBinding()]
param(
    [string] $TaskName = 'ClaudeBackupTray'
)

$ErrorActionPreference = 'Stop'

# Unregister the scheduled task if it exists.
$task = $null
try {
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction Stop
}
catch {
    $task = $null
}

if ($task) {
    try {
        Stop-ScheduledTask -TaskName $TaskName -ErrorAction Stop
    }
    catch {
        # the task may not have been running – not an error
    }

    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Host "Task '$TaskName' was removed." -ForegroundColor Green
}
else {
    Write-Host "No task named '$TaskName' was found."
}

# Cleanly close any running tray app.
$procs = Get-Process -Name 'ClaudeBackupTray' -ErrorAction Ignore
if ($procs) {
    Write-Host "Closing running ClaudeBackupTray ($($procs.Count) process(es))…"
    $procs | Stop-Process -Force
}

Write-Host "Uninstall complete."
