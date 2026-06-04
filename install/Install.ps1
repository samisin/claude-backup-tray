<#
.SYNOPSIS
    Registers Claude Backup Tray with Task Scheduler so the app starts automatically at logon.

.DESCRIPTION
    Creates a scheduled task that:
      * Triggers at logon for the current user.
      * Runs as the logged-on user (NOT SYSTEM), unelevated (RunLevel Limited).
      * Runs only when the user is logged on (LogonType Interactive) – required for the tray icon
        and for %USERPROFILE%/%APPDATA% to resolve correctly.
      * Restarts on failure: 3 times with a 1-minute interval.
      * Has no execution time limit (the app is long-lived).

    Note: registering a task may require running PowerShell as administrator. The task itself still
    runs unelevated as you.

.PARAMETER ExePath
    Path to ClaudeBackupTray.exe, or the folder it lives in. Auto-detected under the repo if omitted.

.PARAMETER TaskName
    Name of the scheduled task (default: ClaudeBackupTray).

.PARAMETER StartNow
    Start the task immediately after registration.

.EXAMPLE
    .\Install.ps1 -StartNow
#>
[CmdletBinding()]
param(
    [string] $ExePath,
    [string] $TaskName = 'ClaudeBackupTray',
    [switch] $StartNow
)

$ErrorActionPreference = 'Stop'

function Resolve-ExePath {
    param([string] $Provided)

    if (-not [string]::IsNullOrWhiteSpace($Provided)) {
        if (-not (Test-Path -LiteralPath $Provided)) {
            throw "The given ExePath does not exist: $Provided"
        }

        $item = Get-Item -LiteralPath $Provided
        # Accept both a folder (complete it with the exe) and a direct exe path.
        if ($item.PSIsContainer) {
            $candidate = Join-Path $item.FullName 'ClaudeBackupTray.exe'
            if (-not (Test-Path -LiteralPath $candidate)) {
                throw "No ClaudeBackupTray.exe found in the folder: $($item.FullName)"
            }
            return (Resolve-Path -LiteralPath $candidate).Path
        }

        if (-not $item.Name.EndsWith('.exe', [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "ExePath must point to ClaudeBackupTray.exe (or the folder it lives in). Got: $($item.FullName)"
        }
        return $item.FullName
    }

    # Search under the repo root (one level up from install\) for a built/published exe.
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $candidates = Get-ChildItem -Path $repoRoot -Recurse -Filter 'ClaudeBackupTray.exe' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending

    if (-not $candidates -or $candidates.Count -eq 0) {
        throw "No ClaudeBackupTray.exe found. Build/publish first (e.g. 'dotnet publish src\ClaudeBackupTray -c Release') or pass -ExePath."
    }

    return $candidates[0].FullName
}

$exe = Resolve-ExePath -Provided $ExePath
$workDir = Split-Path -Parent $exe
$userId = "$env:USERDOMAIN\$env:USERNAME"

Write-Host "Registering task '$TaskName'"
Write-Host "  Exe:        $exe"
Write-Host "  WorkingDir: $workDir"
Write-Host "  User:       $userId"

$action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $workDir

$trigger = New-ScheduledTaskTrigger -AtLogOn -User $userId

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew

# LogonType Interactive => "Run only when the user is logged on". RunLevel Limited => unelevated.
$principal = New-ScheduledTaskPrincipal -UserId $userId -LogonType Interactive -RunLevel Limited

try {
    Register-ScheduledTask `
        -TaskName $TaskName `
        -Action $action `
        -Trigger $trigger `
        -Settings $settings `
        -Principal $principal `
        -Description 'Claude Backup Tray – backs up Claude Code session folders. Starts at logon, runs as the logged-on user.' `
        -Force | Out-Null
}
catch [UnauthorizedAccessException] {
    throw "Access denied during registration. Run this PowerShell as administrator and try again. (The task still runs unelevated as you.)"
}

Write-Host "Done. Task '$TaskName' is registered." -ForegroundColor Green

if ($StartNow) {
    Write-Host "Starting the task now…"
    Start-ScheduledTask -TaskName $TaskName
}
else {
    Write-Host "Tip: start it directly with 'Start-ScheduledTask -TaskName $TaskName', or log out and back in."
}
