<#
.SYNOPSIS
    Default backup script for Claude Backup Tray.

.DESCRIPTION
    Copies Claude Code's session folders into a date-stamped snapshot directory under -TargetRoot.

    Contract with the tray app:
      * Called with -TargetRoot, -MinJsonlCount and then the source folders as positional
        arguments (last; collected by $Sources via ValueFromRemainingArguments).
      * Returns exit code 0 on full success, != 0 on error (3 = some files could not be copied,
        1 = structural error).
      * Writes ONE machine-readable final line to stdout that the app parses:
            CLAUDEBACKUP_RESULT CopiedJsonl=<n> TotalJsonl=<n> CopiedFiles=<n> TotalBytes=<n>

    Properties:
      * Incremental: copies only files that are new or changed (compares size + LastWriteTimeUtc).
      * Versioned: one folder per day (yyyy-MM-dd). Within a day, runs are incremental; a new day
        creates a new folder and never touches older copies. A corrupt newer file can therefore
        never overwrite yesterday's good copy.
      * Never deletes anything in the target – files that Claude cleans up in the source remain
        in the backup.
      * Reads source files with FileShare.ReadWrite, so an in-progress Claude session writing to a
        .jsonl is not blocked.
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $TargetRoot,

    [Parameter(Mandatory = $false)]
    [int] $MinJsonlCount = 0,

    # Must come last in the call: receives all remaining arguments (one path per argument),
    # because a named [string[]] parameter does not bind multiple space-separated values.
    [Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)]
    [string[]] $Sources
)

$ErrorActionPreference = 'Stop'      # no silent failures
$ProgressPreference    = 'SilentlyContinue'  # progress bar only, not error handling

function Write-Log {
    param([string] $Message, [string] $Level = 'INFO')
    $ts = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    Write-Host "[$ts] [$Level] $Message"
}

# Copies a file with shared reading (FileShare.ReadWrite) so an open .jsonl does not block the backup.
function Copy-FileShared {
    param(
        [Parameter(Mandatory = $true)] [string] $Source,
        [Parameter(Mandatory = $true)] [string] $Destination
    )
    $in = $null
    $out = $null
    try {
        $in  = [System.IO.FileStream]::new($Source,      [System.IO.FileMode]::Open,   [System.IO.FileAccess]::Read,  [System.IO.FileShare]::ReadWrite)
        $out = [System.IO.FileStream]::new($Destination, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        $in.CopyTo($out, 1MB)
    }
    finally {
        if ($null -ne $out) { $out.Dispose() }
        if ($null -ne $in)  { $in.Dispose() }
    }
}

$copiedFiles = 0
$copiedJsonl = 0
$copiedBytes = [long] 0
$totalJsonl  = 0
$fileErrors  = [System.Collections.Generic.List[string]]::new()

try {
    if ([string]::IsNullOrWhiteSpace($TargetRoot)) {
        throw 'TargetRoot is empty.'
    }

    $stamp    = (Get-Date).ToString('yyyy-MM-dd')
    $snapshot = Join-Path -Path $TargetRoot -ChildPath $stamp
    New-Item -ItemType Directory -Path $snapshot -Force | Out-Null
    Write-Log "Snapshot folder: $snapshot"

    $usedNames = @{}

    foreach ($src in $Sources) {
        if ([string]::IsNullOrWhiteSpace($src)) { continue }
        if (-not (Test-Path -LiteralPath $src)) {
            Write-Log "Source missing – skipping: $src" 'WARN'
            continue
        }

        # Derive a unique destination folder name from the source's last segment.
        $leaf = Split-Path -Path $src -Leaf
        if ([string]::IsNullOrWhiteSpace($leaf)) { $leaf = 'root' }
        $name = $leaf
        $i = 1
        while ($usedNames.ContainsKey($name)) { $name = "$leaf-$i"; $i++ }
        $usedNames[$name] = $true

        $destBase = Join-Path -Path $snapshot -ChildPath $name
        New-Item -ItemType Directory -Path $destBase -Force | Out-Null

        $srcFull = (Resolve-Path -LiteralPath $src).Path.TrimEnd('\', '/')
        Write-Log "Copying '$srcFull' -> '$destBase'"

        $files = Get-ChildItem -LiteralPath $srcFull -Recurse -File -Force
        foreach ($f in $files) {
            $rel  = $f.FullName.Substring($srcFull.Length).TrimStart('\', '/')
            $dest = Join-Path -Path $destBase -ChildPath $rel

            # Incremental comparison: skip unchanged files.
            if (Test-Path -LiteralPath $dest) {
                $existing = Get-Item -LiteralPath $dest -Force
                if ($existing.Length -eq $f.Length -and $existing.LastWriteTimeUtc -eq $f.LastWriteTimeUtc) {
                    continue
                }
            }

            $destDir = Split-Path -Path $dest -Parent
            if (-not (Test-Path -LiteralPath $destDir)) {
                New-Item -ItemType Directory -Path $destDir -Force | Out-Null
            }

            try {
                Copy-FileShared -Source $f.FullName -Destination $dest
                # Preserve the timestamp so the next run sees the file as unchanged.
                $destItem = Get-Item -LiteralPath $dest -Force
                $destItem.LastWriteTimeUtc = $f.LastWriteTimeUtc

                $copiedFiles++
                $copiedBytes += $f.Length
                if ($f.Extension -ieq '.jsonl') { $copiedJsonl++ }
            }
            catch {
                $fileErrors.Add("$($f.FullName): $($_.Exception.Message)")
                Write-Log "Could not copy $($f.FullName): $($_.Exception.Message)" 'ERROR'
            }
        }
    }

    # Total number of .jsonl in the whole snapshot (this is the value the app compares to the threshold).
    try {
        $totalJsonl = @(Get-ChildItem -LiteralPath $snapshot -Recurse -File -Filter '*.jsonl' -Force).Count
    }
    catch {
        Write-Log "Could not count .jsonl in the snapshot: $($_.Exception.Message)" 'WARN'
    }

    $mb = [math]::Round($copiedBytes / 1MB, 2)
    Write-Log "Done. Copied $copiedFiles new/changed files ($copiedJsonl .jsonl), $mb MB. Total $totalJsonl .jsonl in the snapshot."

    if ($MinJsonlCount -gt 0 -and $totalJsonl -lt $MinJsonlCount) {
        Write-Log "The .jsonl count ($totalJsonl) is below the threshold ($MinJsonlCount)." 'WARN'
    }

    # Machine-readable result line (the app reads the LAST line starting with CLAUDEBACKUP_RESULT).
    Write-Output "CLAUDEBACKUP_RESULT CopiedJsonl=$copiedJsonl TotalJsonl=$totalJsonl CopiedFiles=$copiedFiles TotalBytes=$copiedBytes"

    if ($fileErrors.Count -gt 0) {
        Write-Log "$($fileErrors.Count) file(s) could not be copied." 'ERROR'
        exit 3
    }

    exit 0
}
catch {
    Write-Log "Backup failed: $($_.Exception.Message)" 'ERROR'
    if ($_.ScriptStackTrace) { Write-Host $_.ScriptStackTrace }
    # Still emit a result line so the app can show how far we got.
    Write-Output "CLAUDEBACKUP_RESULT CopiedJsonl=$copiedJsonl TotalJsonl=$totalJsonl CopiedFiles=$copiedFiles TotalBytes=$copiedBytes"
    exit 1
}
