# ExplorerHelper

A Windows 11 shell automation library (.NET Framework 4.8, x64) that handles shell tasks that are awkward or impossible from pure PowerShell — taskbar pinning, Quick Access pinning, and Explorer restarts. Built as a C# class library (`ExplorerHelper.dll`) and consumed through a thin PowerShell wrapper module (`ExplorerHelper.psm1`) from PC setup scripts.

The PowerShell module exports four functions: `Taskbar`, `QuickAccess`, `Explorer`, and `ExplorerHelper`. Each dispatches to a static method on the matching C# class.

## Taskbar

```
Taskbar list                                       List pinned apps (in taskbar order)
Taskbar pin <app>                                  Pin app(s) to taskbar
Taskbar pin -Apps <csv>                            Pin multiple apps (comma-separated)
Taskbar pin <app> -Apply $false                    Queue only, don't apply
Taskbar unpin <name>                               Unpin app from taskbar by name
Taskbar unpinall                                   Unpin ALL items from the taskbar
Taskbar snapshot [path]                            Snapshot pins to XML + backup .lnk files
Taskbar apply                                      Apply pending pins (merge with existing)
Taskbar apply -Order <csv>                         Apply pending pins and reorder
Taskbar apply <snapshot.xml>                       Restore taskbar from a snapshot file
Taskbar apply $false                               Skip apply (no-op, for setup scripts)
```

### pin

Resolves desktop apps to Start Menu `.lnk` shortcuts and queues them into `LayoutModification.xml`. Entries prefixed with `UWP:` are resolved as UWP/Store apps by matching the supplied regex against `Get-AppxPackage` names.

```
-Apps <csv>          Comma/newline-separated list of apps (here-string friendly)
-Apply $true         Apply immediately (default)
-Apply $false        Queue only — call 'Taskbar apply' later
```

A positional `<app>` argument is equivalent to `-Apps <app>` for single-app pins.

### apply

When called **without a path**: reads pending pins from `LayoutModification.xml`, snapshots the current taskbar, merges new pins (appended at end, duplicates skipped), optionally reorders, then rebuilds via unpinall + `PinListPlacement="Replace"` + Explorer restart. If nothing changed, the expensive rebuild is skipped. Prints a consolidated result showing each item's state (`Added`, `Moved`, `Pinned`).

When called **with a snapshot path**: restores the taskbar from that file (unpinall first, then replace). Note that `-Order` is ignored in this mode.

When called **with `$false`** as the target (PowerShell wrapper only): prints `Apply skipped.` and does nothing. This is a convenience for setup scripts that want a single line controlled by a boolean.

```
-Order <csv>         Reorder: listed apps first, unlisted apps keep their current order.
                     Matching is: exact → '<name>_N' suffix → substring.
```

### unpin

Accepts a single name or a comma-separated list. For each name, finds a matching `.lnk` in the pinned taskbar folder (substring match), resolves it to a PIDL, and unpins via `IPinnedList3::Modify`.

### snapshot

Saves the current taskbar state to XML and copies all `.lnk` files to a `links/` subfolder. Does not modify the taskbar. The `path` argument can be:
- Omitted → `%TEMP%\ExplorerHelper_Snapshot_<datetime>\snapshot.xml`
- A folder → `<folder>\snapshot_<datetime>.xml`
- An `.xml` file → exact path (overwritten if exists)

### Snapshot XML format

```xml
<?xml version="1.0" encoding="utf-8"?>
<TaskbarSnapshot timestamp="2026-04-08T15:17:32" count="33">
  <Item displayName="Outlook 2016" type="Desktop"
        lnkPath="C:\...\TaskBar\Outlook 2016.lnk"
        path="links/Outlook 2016.lnk"/>
  <Item displayName="Claude" type="UWP"
        appUserModelId="Claude_pzs8sxrjxfjjc!Claude"/>
</TaskbarSnapshot>
```

Order is determined by line order — rearrange items by cut/paste. Edit `displayName` to rename shortcuts in the taskbar tooltip (not widely used yet).

## QuickAccess

```
QuickAccess list                                   List Quick Access pinned items
QuickAccess pin <path>                             Pin folder(s) to Quick Access
QuickAccess pin -Paths <csv>                       Pin multiple folders (comma-separated)
QuickAccess unpin <path>                           Unpin folder from Quick Access
QuickAccess snapshot [path]                        Snapshot Quick Access to XML
QuickAccess apply <snapshot.xml>                   Apply (reconcile) Quick Access from a snapshot
```

Pin and apply accept comma/newline-separated lists and trim whitespace for use with PowerShell here-strings. Both are idempotent — pin checks the current state before invoking the `pintohome` verb (whose underlying behaviour is a toggle), and apply unpins anything not in the snapshot and pins anything missing. Explorer is only restarted if something actually changed.

```
-RestartExplorer $true|$false    Restart Explorer after changes (default: $true)
```

### snapshot

Default path (when omitted) is `%TEMP%\ExplorerHelper_QA_Snapshot_<datetime>\quickaccess.xml`. Folder and `.xml` file arguments work the same as for `Taskbar snapshot`.

```xml
<?xml version="1.0" encoding="utf-8"?>
<QuickAccessSnapshot timestamp="2026-04-08T15:17:32" count="5">
  <Item path="D:\Dropbox"/>
  <Item path="D:\Temp"/>
</QuickAccessSnapshot>
```

## Explorer

```
Explorer restart                                   Restart Windows Explorer
Explorer restart $false                            No-op (for conditional script lines)
```

Kills all `explorer.exe` processes, waits for exit, relaunches the shell, and cleans up any leftover `LayoutModification.xml` (with a 5-retry loop in case Explorer still holds the file). Pass `$false` to skip entirely.

## ExplorerHelper

```
ExplorerHelper version                             Print the module version
```

Reads the version from the loaded module manifest (`ExplorerHelper.psd1`).

## Exit codes

Every C# command method returns an int. The PowerShell wrapper pipes these to `Out-Null`, so scripts generally don't inspect them — the methods write all status to stdout/stderr — but the underlying contract is:

| Code | Meaning |
|------|---------|
| 0    | Success |
| 1    | (reserved for invalid arguments; currently unused — the PS wrapper throws for missing args) |
| 2    | Operation failed |

## Setup script usage

The module is autoloaded from `PC Setup\Tools\ExplorerHelper\` via the PowerShell module path. Setup scripts just call the functions directly.

### Step scripts — pin without applying

```powershell
$winget = "$PSScriptRoot\..\Generic\Install-UsingWinGet.ps1"

& $winget -AppName "Notepad++"  -Id "Notepad++.Notepad++"  -Install:$NotepadPlusPlus  -PinToTaskbar:$Pin_NotepadPlusPlus
& $winget -AppName "HxD"        -Id "MHNexus.HxD"          -Install:$HxD              -PinToTaskbar:$Pin_HxD

# Apply once at the end — pass $false to skip
Taskbar apply $ApplyTaskbarChanges
```

### Orchestrator — apply with ordering

```powershell
.\Step.1-Windows.ps1 -ApplyTaskbarChanges $false
.\Step.2-Utils.ps1   -ApplyTaskbarChanges $false
.\Step.3-Office.ps1  -ApplyTaskbarChanges $false

# Final apply with desired order — unmentioned items keep their current order
Taskbar apply -Order "Outlook, Chrome, File Explorer, VS Code, SOLIDWORKS"
```

### Backup and restore

```powershell
# Snapshot current state
Taskbar snapshot "D:\Temp\taskbar_backup"

# Restore from snapshot
Taskbar unpinall
Taskbar apply "D:\Temp\taskbar_backup\snapshot.xml"
```

### Quick Access

```powershell
QuickAccess pin -Paths @"
D:\Dropbox
D:\Temp
D:\Projects
"@

QuickAccess snapshot "D:\Temp\qa_backup.xml"
QuickAccess apply    "D:\Temp\qa_backup.xml"
```

## Build

- **Source:** `D:\Dropbox\IT\scripts\tools\ExplorerHelper\src\`
- **Project:** `src\ExplorerHelper.csproj` (`OutputType=Library`, `TargetFramework=net48`, `PlatformTarget=x64`)
- **Output:** `D:\Dropbox\IT\scripts\PC Setup\Tools\ExplorerHelper\ExplorerHelper.dll`

```
dotnet build -c Release
```

A `CopyToToolsFolder` post-build target automatically copies `ExplorerHelper.dll` next to the `.psm1` / `.psd1` module files, so a rebuild is all that's needed to deploy.

## Tests

`test/Tests.md` is a test plan designed to be executed by Claude Code — it describes scenarios in natural language for an agent to run and verify against the live shell.
