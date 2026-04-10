# ExplorerHelper

A Windows 11 shell automation library (.NET Framework 4.8, x64) that handles shell tasks that are awkward or impossible from pure PowerShell — taskbar pinning, Quick Access pinning, and Explorer restarts. Built as a C# class library (`ExplorerHelper.dll`) and consumed through a thin PowerShell wrapper module (`ExplorerHelper.psm1`) from PC setup scripts.

The PowerShell module exports four functions: `Taskbar`, `QuickAccess`, `Explorer`, and `ExplorerHelper`. Each dispatches to a static method on the matching C# class.

For internals (how the layout XML queue works, how apply merges pending pins, the COM interfaces used for taskbar pinning, the build / post-build deploy, etc.) see [`src/Implementation.md`](src/Implementation.md).

## Taskbar

```
Taskbar list                                       List pinned apps (in taskbar order)
Taskbar snapshot [path]                            Snapshot pins to XML + backup .lnk files
Taskbar pin <app>                                  Pin app(s) to taskbar
Taskbar pin -Apps <csv>                            Pin multiple apps (comma-separated)
Taskbar pin <app> -Apply $false                    Queue only, don't apply
Taskbar unpin <name>                               Unpin app from taskbar by name
Taskbar unpinall                                   Unpin ALL items from the taskbar
Taskbar apply                                      Apply pending pins (merge with existing)
Taskbar apply -Order <csv>                         Apply pending pins and reorder
Taskbar apply <snapshot.xml>                       Restore taskbar from a snapshot file
Taskbar apply $false                               Skip apply (no-op, for setup scripts)
```

### snapshot

Saves the current taskbar state to XML and copies all `.lnk` files to a `links/` subfolder. Does not modify the taskbar. The `path` argument can be:
- Omitted → `%TEMP%\ExplorerHelper_Snapshot_<datetime>\snapshot.xml`
- A folder → `<folder>\snapshot_<datetime>.xml`
- An `.xml` file → exact path (overwritten if exists)

The snapshot file is a plain XML document — items are in line order, so rearranging them by cut/paste reorders the taskbar on restore, and editing `displayName` renames the tooltip. See [`src/Implementation.md`](src/Implementation.md) for the schema.

### pin

Resolves desktop apps to Start Menu `.lnk` shortcuts and queues them for the next `apply`. Entries prefixed with `UWP:` are resolved as UWP/Store apps by matching the supplied pattern against `Get-AppxPackage` names via PowerShell's `-match` operator.

```
-Apps <csv>          Comma/newline-separated list of apps (here-string friendly)
-Apply $true         Apply immediately (default)
-Apply $false        Queue only — call 'Taskbar apply' later
```

A positional `<app>` argument is equivalent to `-Apps <app>` for single-app pins.

**Desktop app matching:** case-insensitive exact match on the Start Menu `.lnk` filename (without the extension). `Taskbar pin "Word 2016"` finds `Word 2016.lnk`; `Taskbar pin "Word"` errors with `Could not find a Start Menu shortcut for 'Word'.` If an exact match is not found, a `'<name>_N'` suffix match is accepted as a fallback (Windows appends these when the layout cache is stale). A full `.lnk` path is also accepted and used directly. Substring matching was removed — it silently picked the wrong app when the query was a prefix of another (e.g. `"Notepad"` matching `Notepad++.lnk`).

**UWP pattern matching:** `-match` is a substring-regex, so `UWP:Calculator` finds `Microsoft.WindowsCalculator`. The pattern IS a regex, though, so metacharacters like `.`, `+`, `(`, `[` are interpreted — if you need to match a literal metacharacter, escape it (e.g. `UWP:Paint\.NET`).

### unpin

Accepts a single name or a comma-separated list. Matching rules:

- **No wildcard** — case-insensitive exact match on the pinned shortcut's display name (filename without `.lnk`). This is stricter than it used to be: `Taskbar unpin "Windows PowerShell"` will not touch `Windows PowerShell ISE`. Prints `'<name>' is not pinned.` if nothing matches.
- **Glob wildcard** (`*` or `?` anywhere in the name) — unpins every pinned shortcut whose name matches the glob. `Taskbar unpin "Windows Power*"` removes both PowerShell and PowerShell ISE.

### apply

When called **without a path**: merges pending pins with the current taskbar (new items appended at end, duplicates skipped), optionally reorders, and rebuilds the taskbar. If nothing changed, the rebuild is skipped. Prints a consolidated result showing each item's state (`Added`, `Moved`, `Pinned`).

When called **with a snapshot path**: restores the taskbar from that file. Note that `-Order` is ignored in this mode.

When called **with `$false`** as the target (PowerShell wrapper only): prints `Apply skipped.` and does nothing. This is a convenience for setup scripts that want a single line controlled by a boolean.

```
-Order <csv>         Reorder: listed apps first, unlisted apps keep their current order.
                     Matching is: exact → '<name>_N' suffix. Names that don't
                     match any pinned item are silently skipped.
```

See [`src/Implementation.md`](src/Implementation.md) for the internals of pending-pin merging, the matching cascade, and how the rebuild is performed.

## QuickAccess

```
QuickAccess list                                   List Quick Access pinned items
QuickAccess snapshot [path]                        Snapshot Quick Access to XML
QuickAccess pin <path>                             Pin folder(s) to Quick Access
QuickAccess pin -Paths <csv>                       Pin multiple folders (comma-separated)
QuickAccess unpin <path>                           Unpin folder from Quick Access
QuickAccess apply <snapshot.xml>                   Apply (reconcile) Quick Access from a snapshot
```

Pin and apply accept comma/newline-separated lists and trim whitespace for use with PowerShell here-strings. Both are idempotent — pin checks the current state before pinning, and apply unpins anything not in the snapshot and pins anything missing. Explorer is only restarted if something actually changed.

```
-RestartExplorer $true|$false    Restart Explorer after changes (default: $true)
```

### snapshot

Default path (when omitted) is `%TEMP%\ExplorerHelper_QA_Snapshot_<datetime>\quickaccess.xml`. Folder and `.xml` file arguments work the same as for `Taskbar snapshot`. The file is a simple list of `<Item path="…"/>` entries.

## Explorer

```
Explorer restart                                   Restart Windows Explorer
Explorer restart $false                            No-op (for conditional script lines)
```

Restarts Windows Explorer and cleans up any leftover layout XML from a pending pin operation. Pass `$false` to skip entirely (useful when a boolean flag controls whether a setup step should restart).

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

To rebuild and deploy the DLL, see [`src/Implementation.md`](src/Implementation.md).

## Tests

`test/Tests.md` is a test plan designed to be executed by Claude Code — it describes scenarios in natural language for an agent to run and verify against the live shell.
