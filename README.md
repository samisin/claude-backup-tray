<div align="center">

# 🛡️ Claude Backup Tray

**A tiny Windows system-tray app that quietly keeps versioned, off-disk backups of your Claude Code sessions.**

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6?logo=windows&logoColor=white)](#requirements)
[![PowerShell](https://img.shields.io/badge/PowerShell-5.1%20%7C%207%2B-5391FE?logo=powershell&logoColor=white)](#)
[![UI](https://img.shields.io/badge/UI-WinForms%20tray-512BD4)](#)
[![Solution](https://img.shields.io/badge/open%20in-Visual%20Studio%20(.slnx)-5C2D91?logo=visualstudio&logoColor=white)](#quick-start)
[![License: Unlicense](https://img.shields.io/badge/license-Unlicense-blue.svg)](LICENSE)

</div>

> **Why?** Claude Code deletes local transcripts after `cleanupPeriodDays` (default **30 days**) at
> startup — sometimes even when the setting has been raised. This app is the safety net: an
> independent, **versioned** copy, taken on a schedule and at startup, with a
> colour-coded tray icon and a Windows notification the moment something goes wrong.

---

## Table of contents

- [Features](#features)
- [How it works](#how-it-works)
- [Requirements](#requirements)
- [Quick start](#quick-start)
- [Configuration](#configuration)
- [The backup model](#the-backup-model)
- [Tray icon &amp; menu](#tray-icon--menu)
- [Notifications &amp; logging](#notifications--logging)
- [Script contract](#script-contract)
- [Project structure](#project-structure)
- [Architecture &amp; design decisions](#architecture--design-decisions)
- [Verified behaviour](#verified-behaviour)
- [Roadmap](#roadmap)
- [License](#license)

---

## Features

- 🗓️ **Scheduled backups** via a cron expression (5- or 6-field, [Cronos](https://github.com/HangfireIO/Cronos)), evaluated in local time.
- ⚡ **Runs immediately at startup** (configurable) so you never wait for the first interval.
- ➕ **Incremental** — only new/changed files are copied (compares size + `LastWriteTimeUtc`).
- 🗂️ **Versioned** — one snapshot folder per day; older days are never overwritten.
- 🧹 **Never deletes** — files Claude cleans up in the source stay in the backup.
- 🔓 **Non-blocking reads** — source files are read with `FileShare.ReadWrite`, so a live session isn't blocked.
- 🎨 **Colour-coded tray icon** + hover tooltip with last/next run and `.jsonl` counts.
- 🔔 **Notifications on failure only** (toast with balloon-tip fallback) — successful runs stay quiet.
- ♻️ **Live config reload** — change the schedule in `appsettings.json` with no restart.
- 🚀 **Autostart at logon** via Task Scheduler, running as you (not `SYSTEM`).
- 🧱 **Robust** — a failed run never crashes the app; nothing fails silently.
- 🔌 **Pluggable** — the actual copying lives in an external PowerShell script you can swap out.

---

## How it works

```
┌──────────────────────────┐        cron / startup / "Run now"
│  Claude Backup Tray (UI)  │ ───────────────────────────────────┐
│  Generic Host + WinForms  │                                     ▼
└──────────────────────────┘                          ┌────────────────────┐
        ▲   reads counts & exit code                   │   backup.ps1       │
        │                                              │  (PowerShell)      │
        └──────────────────────────────────────────── │  incremental copy  │
                                                       └─────────┬──────────┘
   Sources                                                       │
   • %USERPROFILE%\.claude\projects            copies into       ▼
   • %APPDATA%\Claude\claude-code-sessions   ───────────►  TargetRoot\yyyy-MM-dd\<source>\…
   • %APPDATA%\Claude\local-agent-mode-sessions          (date-stamped, never overwritten)
```

The app **schedules, runs, and evaluates**; the script **owns the copying** — so you can change the
backup logic without recompiling.

---

## Requirements

- **Windows 10 / 11**
- **.NET 10 Desktop Runtime** (for framework-dependent runs) — or publish self-contained
- **PowerShell 7** (`pwsh.exe`) **or** Windows PowerShell (`powershell.exe`); the app falls back to `powershell.exe` automatically

Open `ClaudeBackupTray.slnx` in **Visual Studio 2022 (17.10+)**, or build from the CLI.

---

## Quick start

```powershell
# 1. Publish (self-contained — no runtime needed on the target machine)
dotnet publish src\ClaudeBackupTray -c Release -r win-x64 --self-contained `
  -o "$env:LOCALAPPDATA\Programs\ClaudeBackupTray"

# 2. Register autostart at logon (and launch it now)
.\install\Install.ps1 -ExePath "$env:LOCALAPPDATA\Programs\ClaudeBackupTray" -StartNow
```

> `-ExePath` accepts **either the folder or the full path** to `ClaudeBackupTray.exe`.
> Don't see an icon? It's likely in the **hidden-icons flyout** behind the `^` arrow near the clock.

Edit `appsettings.json` next to the exe to point `TargetRoot` at a different disk/network share, then
save — the schedule reloads live. To remove everything:

```powershell
.\install\Uninstall.ps1
```

---

## Configuration

`appsettings.json` (next to the exe):

```jsonc
{
  "Backup": {
    "ScriptPath": "scripts\\backup.ps1",           // relative paths resolve against the app folder
    "PowerShellExe": "pwsh.exe",                   // falls back to powershell.exe
    "TargetRoot": "D:\\claude-backup",             // the target folder for the backups
    "Sources": [
      "%USERPROFILE%\\.claude\\projects",
      "%APPDATA%\\Claude\\claude-code-sessions",
      "%APPDATA%\\Claude\\local-agent-mode-sessions"
    ],
    "Schedule": "*/30 * * * *",                    // cron; 5 or 6 fields (6 = with seconds)
    "RunOnStartup": true,                          // also back up immediately on launch
    "MinJsonlCount": 50,                           // alarm if the snapshot has fewer .jsonl
    "StaleAfterHours": 6,                          // alarm if no success within N hours
    "ScriptTimeoutSeconds": 300
  },
  "Logging": {
    "LogPath": "D:\\claude-backup\\_logs",
    "Level": "Information"                          // Verbose | Debug | Information | Warning | Error | Fatal
  }
}
```

| Key | Meaning |
|-----|---------|
| `ScriptPath` | Path to the backup script. Relative paths resolve against the app folder. |
| `PowerShellExe` | `pwsh.exe`, `powershell.exe`, or a full path. Falls back to Windows PowerShell. |
| `TargetRoot` | Backup folder. |
| `Sources` | Source folders to copy. `%ENVIRONMENT_VARIABLES%` are expanded. |
| `Schedule` | Cron (Cronos). 5 fields = minutes, 6 fields = with seconds. Local time. |
| `RunOnStartup` | Back up immediately at app start, in addition to the schedule. Default `true`. |
| `MinJsonlCount` | Alarm if the snapshot contains fewer `.jsonl` than this. `0` disables. |
| `StaleAfterHours` | Alarm if no successful backup has happened in this many hours. |
| `ScriptTimeoutSeconds` | Abort + fail the run after this many seconds. |
| `Logging:LogPath` | Folder for daily rolling log files. |
| `Logging:Level` | Minimum log level. |

> 💡 Any value can be overridden with an environment variable using the `CLAUDEBACKUP_` prefix and
> `__` as separator, e.g. `CLAUDEBACKUP_Backup__Schedule="*/5 * * * * *"`. Handy for testing.

---

## The backup model

- **Versioned** — `TargetRoot\yyyy-MM-dd\<source-name>\…`, one folder per day. Older days are never
  touched, so a corrupt newer file can never overwrite yesterday's good copy.
- **Incremental** — within a day, only new/changed files are copied (size + `LastWriteTimeUtc`).
  A growing `.jsonl` changes both, so appended sessions are always re-copied; unchanged files are skipped.
- **Never deletes** — anything Claude removes from the source remains in the backup.
- **Shared reads** — `FileShare.ReadWrite`, so an in-progress session writing to a `.jsonl` isn't blocked.

Because `.jsonl` is append-only, a newer file is normally a superset of the older one — and the
per-day split still preserves earlier versions.

---

## Tray icon &amp; menu

<table>
  <tr><th>Icon</th><th>State</th><th>Meaning</th></tr>
  <tr><td align="center"><img src="src/ClaudeBackupTray/Resources/claude-backup-gray.png"   width="22"></td><td><b>Idle</b></td><td>Waiting — hasn't run yet.</td></tr>
  <tr><td align="center"><img src="src/ClaudeBackupTray/Resources/claude-backup-orange.png" width="22"></td><td><b>Running</b></td><td>A backup is in progress.</td></tr>
  <tr><td align="center"><img src="src/ClaudeBackupTray/Resources/claude-backup-green.png"  width="22"></td><td><b>Succeeded</b></td><td>The last run succeeded.</td></tr>
  <tr><td align="center"><img src="src/ClaudeBackupTray/Resources/claude-backup-red.png"    width="22"></td><td><b>Failed</b></td><td>The last run failed, or a health check tripped.</td></tr>
</table>

**Tooltip** (hover): last run time, result, `.jsonl` count (new / total), and the next scheduled run.

**Right-click menu:** `Run backup now` · `Open backup script` · `Open settings` · `Open backup folder` ·
`Open log` · *Status* · `Exit`. Double-clicking the icon opens the backup folder.

---

## Notifications &amp; logging

A **Windows notification** is shown **only on errors** (never on success):

- the script returns a non-zero exit code,
- the `.jsonl` count drops below `MinJsonlCount`,
- no successful run within `StaleAfterHours`,
- invalid configuration (bad cron or missing path).

Notifications use a toast (CommunityToolkit) with a **balloon-tip fallback**; clicking opens the log.

**Logs** are structured, daily-rolling files in `Logging:LogPath`
(`claude-backup-YYYYMMDD.log`, 31 kept). Nothing fails silently — everything is alarmed or logged.

---

## Script contract

<details>
<summary>How the app and the PowerShell script are coupled (so you can swap the script)</summary>

- **Invocation:**
  ```
  pwsh -File <script> -TargetRoot <target> -MinJsonlCount <n> <source1> <source2> …
  ```
  Source folders are passed as **positional arguments last** (already environment-variable-expanded).
- **Exit codes:** `0` = full success · `3` = some files could not be copied · `1` = structural error.
  Anything `!= 0` → red icon + notification.
- **Last stdout line** (machine-readable, parsed by the app):
  ```
  CLAUDEBACKUP_RESULT CopiedJsonl=<n> TotalJsonl=<n> CopiedFiles=<n> TotalBytes=<n>
  ```
  The app compares **`TotalJsonl`** (total `.jsonl` in the snapshot) against `MinJsonlCount`.

The bundled `scripts/backup.ps1` is a complete, working default — replace it with your own as long as
it honours this contract.

</details>

---

## Project structure

<details>
<summary>Layout</summary>

```
ClaudeBackupTray.slnx                 # open this in Visual Studio
README.md
install/
  Install.ps1                         # register autostart (Task Scheduler logon trigger)
  Uninstall.ps1                       # remove task + close the app
src/ClaudeBackupTray/
  Program.cs                          # mutex, Serilog, Generic Host, WinForms loop
  appsettings.json
  app.manifest                        # asInvoker, PerMonitorV2, Win10/11
  Configuration/                      # BackupOptions, LoggingOptions
  Models/                             # AppPaths, BackupRunResult
  State/AppState.cs                   # thread-safe shared state → icon/tooltip
  Services/
    SchedulerService.cs               # Cronos scheduling + live reload
    BackupRunner.cs                   # validate, launch script, parse result
    HealthMonitorService.cs           # stale-backup alarm
    NotificationService.cs            # toast + balloon fallback
    PowerShellResolver.cs             # pwsh.exe → powershell.exe fallback
  Tray/                               # TrayApplicationContext, TrayIconFactory
  Resources/                          # status icons (.ico embedded in the exe)
  scripts/backup.ps1                  # default incremental backup script
```

</details>

---

## Architecture &amp; design decisions

<details>
<summary>Why a tray app + Task Scheduler (and not a Windows Service)</summary>

A tray icon needs an **interactive user session**. A classic Windows Service runs in Session 0 as
`SYSTEM`, where `%APPDATA%` / `%USERPROFILE%` resolve to the wrong place — and it can't show UI. Since
the backup sources live under the **user profile**, the app must run in the user's context.

So the design is fixed:

- The app is a **user-session app with a tray icon**.
- "Service-like" behaviour comes from **Task Scheduler with a logon trigger** that runs the app as the
  logged-on user, automatically at logon, **unelevated** (`RunLevel Limited`, `LogonType Interactive`),
  restarting on crash (3× / 1 min).

Built on the **Generic Host** for DI, configuration, `IOptionsMonitor` live-reload, and background
services; **Serilog** for rolling file logs; **WinForms `NotifyIcon`** for the tray. Single-instance is
enforced with a named mutex.

</details>

---

## Verified behaviour

| # | Behaviour | Status |
|---|-----------|--------|
| 1 | Starts, idle icon, next run in tooltip | ✅ |
| 2 | "Run now": orange → green, date-stamped copy with the right `.jsonl` count | ✅ |
| 3 | Second run is incremental (only changed files) | ✅ (378 → 4 → 0 files) |
| 4 | Wrong script path → red icon + notification, app keeps running | ✅ |
| 5 | `MinJsonlCount` threshold raises an alarm | ✅ |
| 6 | `Schedule` change takes effect without restart | ✅ |
| 7 | "Open backup script" / "Open settings" open the right files | ✅ |
| 8 | Autostart at logon, runs as the user | ✅ |
| 9 | A successful run produces no notification | ✅ |
| 10 | Runs a backup immediately at startup | ✅ |

---

## Roadmap

Out of scope for v1 (candidates for later):

- ☁️ Cloud / off-site replication (can be added in the script)
- 🖥️ A GUI for editing settings (file editing is enough for now)
- ♻️ A restore / recovery flow (a separate tool)

---

## License

Released into the public domain under [The Unlicense](LICENSE) — use, modify, and distribute it
however you like, no conditions or attribution required.

> ℹ️ Transcripts are plain text. If the contents are sensitive, point `TargetRoot` at an encrypted disk.
