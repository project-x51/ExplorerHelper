# ExplorerHelper — Implementation notes

This document describes how ExplorerHelper works internally. It's written for developers modifying the C# code. For the user-facing command surface, see the top-level [`ReadMe.md`](../ReadMe.md).

## Layout

```
src/
  ExplorerHelper.csproj        .NET Framework 4.8, x64, OutputType=Library
  Commands/
    TaskbarCommands.cs         Taskbar list/pin/unpin/apply/snapshot
    QuickAccessCommands.cs     Quick Access list/pin/unpin/apply/snapshot
    ExplorerCommands.cs        Explorer restart + LayoutModification.xml cleanup
  Interop/
    PinnedList3.cs             IPinnedList3 COM wrapper (taskbar pinning)
    NativeMethods.cs           P/Invoke: SHGetNameFromIDList, CoTaskMemAlloc, etc.
    IsExternalInit.cs          Shim so C# 9 records compile on net48
  Shared/
    Utilities.cs               Common helpers

module/
  ExplorerHelper.psm1          PowerShell wrapper — `Add-Type -Path` loads the DLL
  ExplorerHelper.psd1          Module manifest (version is read at runtime)
```

## Build

```
dotnet build -c Release
```

`ExplorerHelper.csproj` has a `DeployModule` target that runs `AfterTargets="Build"`. It copies three files into `D:\Dropbox\IT\scripts\PC Setup\Tools\ExplorerHelper\` (resolved as `$(MSBuildProjectDirectory)\..\..\..\PC Setup\Tools\ExplorerHelper`):

- `ExplorerHelper.dll` — from `$(OutputPath)`
- `ExplorerHelper.psm1` — from `../module/`
- `ExplorerHelper.psd1` — from `../module/`

That folder is on the `PSModulePath` used by setup scripts, so a plain `dotnet build` is the entire deploy — no manual copy, no install step.

Targets:
- `TargetFramework` = `net48`
- `PlatformTarget` = `x64` (required because Explorer is 64-bit and the COM objects are in-proc)
- `LangVersion` = `latest`, `Nullable` enabled, `AllowUnsafeBlocks` enabled (for PIDL memcpy)

## Taskbar pinning — the two-path model

Windows exposes two unrelated ways to affect taskbar pins:

1. **`IPinnedList3` COM interface.** Undocumented, used by Firefox and Edge. Lets you pin and unpin items synchronously, but the target must already be a `.lnk` shortcut that Windows considers a "pinnable" Start Menu entry. New pins added this way don't always survive an Explorer restart cleanly.
2. **`LayoutModification.xml`.** A Start / taskbar policy file that Explorer reads at shell startup. Writing a file at `%LOCALAPPDATA%\Microsoft\Windows\Shell\LayoutModification.xml` and restarting Explorer causes the listed items to become pinned. Works for both desktop `.lnk` targets and UWP `AUMID`s, survives restart, and supports `PinListPlacement="Replace"` to wipe the existing pin list.

ExplorerHelper uses COM for queries and unpinning (fast, no restart required) and the layout XML path for adding pins (reliable, survives restart). `apply` combines the two: it reads the current state via COM, merges the pending layout XML, writes a new layout XML, and restarts Explorer.

### `IPinnedList3` wrapper (`Interop/PinnedList3.cs`)

The interface has no C# type library, so the wrapper creates the COM object by CLSID and then walks the vtable manually:

- CLSID_TASKBAND_PIN = `{90AA3A4E-1CBA-4233-B8BB-535773D48449}`
- IID_IPINNED_LIST3 = `{0DD79AE2-D156-45D4-9EEB-3B549769E940}`
- `EnumObjects` is at vtable slot 3 (first slot after the three IUnknown methods)
- `Modify` is at vtable slot 16 (IUnknown + 13 interface methods before it)
- `Modify(unpin, pin, caller)` — passing `IntPtr.Zero` for either operand skips that half. Caller is `int.MaxValue` (the value Firefox uses).

Enumeration returns `IEnumIDList`; `Next` is at vtable slot 3 of that interface. For each PIDL, the wrapper calls `SHGetNameFromIDList` three times:

- `SIGDN_FILESYSPATH` → the `.lnk` path (null for UWP items)
- `SIGDN_DESKTOPABSOLUTEPARSING` → the AUMID (for UWP items) or a parsing path
- `SIGDN_NORMALDISPLAY` → the friendly display name for the tooltip

Items whose filesystem path ends in `.lnk` are classified as desktop apps; everything else is treated as UWP and keyed by AUMID.

`GetOrderedItemsWithPidls()` is the version used by `UnpinAll`. It makes a heap copy of each PIDL with `ILGetSize` + `CoTaskMemAlloc` + `Buffer.MemoryCopy` so the PIDLs remain valid after the enumerator is released; the caller is then responsible for passing them back to `UnpinAll(list)`, which calls `Modify` for each and frees the copy.

### Pending-pin queue file

`TaskbarCommands.Pin` doesn't touch the taskbar directly when called with `-Apply $false`. Instead it appends to the `LayoutModification.xml` file at:

```
%LOCALAPPDATA%\Microsoft\Windows\Shell\LayoutModification.xml
```

Structure:

```xml
<LayoutModificationTemplate
    xmlns="http://schemas.microsoft.com/Start/2014/LayoutModification"
    xmlns:defaultlayout="http://schemas.microsoft.com/Start/2014/FullDefaultLayout"
    xmlns:start="http://schemas.microsoft.com/Start/2014/StartLayout"
    xmlns:taskbar="http://schemas.microsoft.com/Start/2014/TaskbarLayout"
    Version="1">
  <CustomTaskbarLayoutCollection>
    <defaultlayout:TaskbarLayout>
      <taskbar:TaskbarPinList>
        <taskbar:DesktopApp DesktopApplicationLinkPath="..." />
        <taskbar:UWA AppUserModelID="..." />
      </taskbar:TaskbarPinList>
    </defaultlayout:TaskbarLayout>
  </CustomTaskbarLayoutCollection>
</LayoutModificationTemplate>
```

Multiple calls to `Pin` accumulate entries in that file. The file is also the channel between separate PowerShell processes (e.g. successive step scripts in a setup run) — each process can queue pins, and a final `Taskbar apply` in the orchestrator flushes the whole batch at once.

### `apply` — merging pending pins with existing

Happens in `TaskbarCommands.Apply`. The flow when called without a path argument:

1. Read pending entries from `LayoutModification.xml` (via `LoadPendingPins`). Each is a `(DisplayName, LnkPath, AppUserModelId)` tuple.
2. Snapshot the current taskbar via `PinnedList3.GetOrderedItemsWithPidls()`. These items seed the merged list and are marked `"Unchanged"`.
3. For each pending pin, check whether it's already present:
   - Exact AUMID match (case-insensitive) for UWP items
   - Exact display-name match
   - `<name>_N` suffix match (Explorer sometimes appends `_1`, `_2`, … when the same display name is pinned again — matching ignores the suffix so `"Snagit 2025"` matches `"Snagit 2025_1"`)
   - Explicitly **not** a substring match — `"Visual Studio"` must not be treated as already-present when `"Visual Studio Code"` is pinned
4. For new items, copy the source `.lnk` into a temp `links/` directory (so the layout XML can point at a stable path) and append to the merged list as `"Added"`.
5. If `-Order` was supplied, reorder the merged list (see below) and mark shuffled items `"Moved"`.
6. If nothing was added and nothing moved, return without restarting Explorer.
7. Otherwise write a fresh `LayoutModification.xml` with `PinListPlacement="Replace"`, call `UnpinAll` to clear the current taskbar, and restart Explorer. Explorer reads the layout XML on relaunch and rebuilds the pin list from scratch.

The "unpinall then replace" dance is necessary because `LayoutModification.xml` with `PinListPlacement="Replace"` is only honoured when the existing pin list is empty — otherwise Explorer merges rather than replacing.

After the restart, `ExplorerCommands.RestartAndWaitForPins()` polls via `IPinnedList3.EnumObjects` for up to 30 seconds, then waits an extra second before deleting the layout XML. Deleting it the instant pins appear was observed to make the taskbar take noticeably longer to come back; the settle delay is a workaround for that.

### Matching cascade (for `-Order` and `unpin`)

When matching a user-supplied name against existing pin display names, the code tries:

1. **Exact** case-insensitive equality
2. **`<name>_N` suffix** — same prefix followed by `_` and any trailing text (handles Explorer's disambiguating suffixes)
3. **Substring** — case-insensitive `IndexOf` contains match (fallback, covers things like `-Order "Chrome"` matching `"Google Chrome"`)

The cascade is deliberately layered so an exact name never gets shadowed by an unrelated substring hit. `unpin` uses only the substring stage (it operates on `.lnk` filenames in the pinned-taskbar folder), while `apply -Order` runs the full cascade in sequence.

### Glob vs regex in `pin`

Inside `TaskbarCommands.Pin`, the resolution of an app name to a Start Menu shortcut goes:

- If the name contains `*` or `?`, convert it to a regex by `Regex.Escape`-ing everything and then re-expanding `\*` → `.*` and `\?` → `.` — every matching shortcut is queued.
- Otherwise, prefer an exact (case-insensitive) filename match; fall back to substring.
- Entries prefixed with `UWP:` are passed straight to the UWP resolver (`FindUwpByRegex`), which does `Regex.IsMatch` against `Get-AppxPackage` family and package names. The UWP path always uses real regex, never glob.

## Snapshot XML format

Produced by `Taskbar snapshot` and consumed by `Taskbar apply <snapshot.xml>`:

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

- The `links/` subfolder beside the XML holds a copy of every `.lnk` that was pinned at snapshot time, so the backup is self-contained and still works if the source Start Menu entry later moves or disappears.
- Order is determined by line order — the snapshot-restore path feeds items into the same layout-XML rebuild used by `apply`, so rearranging lines in the XML reorders the taskbar on restore.
- `displayName` affects the tooltip text; it's otherwise cosmetic.
- UWP items have no `lnkPath` / `path`; they're restored via `<taskbar:UWA AppUserModelID="…"/>` in the generated layout XML.

`ConvertSnapshotToLayout` (in `TaskbarCommands.cs`) rewrites the snapshot into a `LayoutModification.xml` with `PinListPlacement="Replace"` and then goes through the same unpinall + write + restart path as `apply`.

For Quick Access, the snapshot is much simpler:

```xml
<?xml version="1.0" encoding="utf-8"?>
<QuickAccessSnapshot timestamp="2026-04-08T15:17:32" count="5">
  <Item path="D:\Dropbox"/>
  <Item path="D:\Temp"/>
</QuickAccessSnapshot>
```

Quick Access doesn't have a layout-XML equivalent; `QuickAccess apply` diffs the snapshot against the live list and invokes the `pintohome` / `unpinfromhome` verbs to reconcile. The verbs are toggles, so the diff is essential — running `pintohome` against an already-pinned folder would unpin it.

## Explorer restart (`ExplorerCommands.cs`)

`Restart()` is the simple public entry point:

1. Kill all `explorer.exe` processes (`Process.GetProcessesByName("explorer")` → `Kill` → `WaitForExit(5000)`)
2. Sleep 2 seconds — lets the shell fully tear down; killing and relaunching too quickly can leave the taskbar in a half-initialised state
3. `Process.Start("explorer.exe", UseShellExecute=true)`
4. `CleanupLayoutXml()` — deletes `LayoutModification.xml` if present, retrying up to 5 times with a 1-second sleep between attempts (Explorer sometimes still has the file locked when the new shell is coming up)

`RestartAndWaitForPins()` is the version used by `Taskbar apply`. It's the same sequence but adds a polling loop after the relaunch: it creates a fresh `PinnedList3` and calls `GetOrderedItems` once per second for up to 30 seconds, breaking as soon as at least one item appears. Then it sleeps 1 more second before deleting the layout XML — as noted above, deleting the file the instant pins appear was observed to delay the taskbar's full recovery. If the COM init itself fails (very rare), it falls back to a fixed 5-second sleep.

If `CleanupLayoutXml` can't delete the file after all retries, it writes a warning to stderr telling the user to delete it manually — leaving a stale file around would cause the pinned apps to be reapplied on the next reboot.

## Exit code contract

All public command methods return `int`:

- `0` — success
- `1` — reserved for invalid arguments (unused; the PS wrapper throws on missing required args before reaching the C# layer)
- `2` — operation failed (any caught exception in a command's top-level `try` block)

The PS wrapper pipes the return value to `Out-Null`, and all user-visible progress goes through `Console.WriteLine` / `Console.Error.WriteLine` instead. Keep it that way — scripts rely on seeing the textual output, not on inspecting `$LASTEXITCODE`.

## Records on net48

net48 doesn't ship the `System.Runtime.CompilerServices.IsExternalInit` type that C# 9 `record` types need for init-only setters. `Interop/IsExternalInit.cs` contains a trivial stub of that type so `PinnedItem` / `PinnedItemWithPidl` compile. Don't delete it.
