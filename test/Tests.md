# ExplorerHelper Test Script

Run these tests by asking Claude Code: "run the ExplorerHelper tests".

The target machine is assumed to be a fresh Windows 11 install. Only apps guaranteed
present on a clean install are used: Notepad, Microsoft Edge, Microsoft Store,
UWP:Calculator, Windows PowerShell, Windows PowerShell ISE.

All tests must be executed in order — later tests depend on state set up by earlier
tests. Record each test's result (PASS/FAIL) and a short note, then emit the final
report described at the bottom.

All Console output from the C# library is prefixed with a component tag
(`Taskbar:`, `QuickAccess:`, or `Explorer:`). Tests match the exact text the code
prints, including the prefix.

## Setup

1. Import the module (force reload to clear any prior state):
   `Import-Module "$PSScriptRoot\..\..\..\PC Setup\Tools\ExplorerHelper" -Force`
2. Verify the pending layout XML is not present. If
   `$env:LOCALAPPDATA\Microsoft\Windows\Shell\LayoutModification.xml` exists,
   delete it so that T05 starts from a clean state.
3. Create `D:\Temp` if it does not already exist (used by QuickAccess tests).
4. Take an original snapshot of the taskbar as the restore point:
   `Taskbar snapshot "D:\Temp\eh_tests_original.xml"`
   Record the reported **Items** count as `$OriginalTaskbarCount` and the first
   3 display names from `Taskbar list` as `$OriginalTop3` for later verification.
5. Take an original snapshot of Quick Access as the restore point:
   `QuickAccess snapshot "D:\Temp\eh_tests_qa_original.xml"`
   Record the reported **Items** count as `$OriginalQACount`.

## Tests

### T01 — Version
**Run:** `ExplorerHelper version`
**Expect:** Output contains `ExplorerHelper v` followed by a version number (e.g. `ExplorerHelper v2.0.0`).

### T02 — Taskbar List (populated)
**Run:** `Taskbar list`
**Expect:** Output starts with the line `Taskbar: Pinned items:` followed by one
or more numbered entries. Each entry is two lines: `  <n>. <DisplayName>` then
`     <lnkPath or AUMID>`.
**Verify:** At least one entry is shown and the numbering starts at 1.

### T03 — Taskbar Pin single (queue only, no apply)
**Prerequisite:** `LayoutModification.xml` does not exist (delete it if it does).
**Run:** `Taskbar pin "Notepad" -Apply $false`
**Expect:** Output contains `Taskbar: Queued pin for Notepad`.
**Verify:** `%LocalAppData%\Microsoft\Windows\Shell\LayoutModification.xml`
exists and contains a `<taskbar:DesktopApp` element whose
`DesktopApplicationLinkPath` ends in `Notepad.lnk`.
**Cleanup:** Delete `LayoutModification.xml` before proceeding so T04 starts
with a fresh queue.

### T04 — Taskbar Pin multiple via -Apps (queue only)
**Prerequisite:** `LayoutModification.xml` does not exist.
**Run:** `Taskbar pin -Apps "Notepad, UWP:Calculator" -Apply $false`
**Expect:** Output contains a line `Taskbar: Matched UWP: <AUMID>` for the
Calculator resolution, followed by `Taskbar: Queued pins for Notepad, Calculator`
(plural form because two apps were queued).
**Verify:** `LayoutModification.xml` contains both a `<taskbar:DesktopApp>`
entry for Notepad and a `<taskbar:UWA AppUserModelID="...">` entry whose AUMID
matches the one printed in the Matched UWP line (typically contains
`Microsoft.WindowsCalculator`).
**Note:** Do NOT delete the XML — T05 needs it to be present first, then T05
will clear it.

### T05 — Taskbar Apply (no pending, no order)
**Prerequisite:** Delete `LayoutModification.xml` if it exists (discarding the
queue from T04 without applying).
**Run:** `Taskbar apply`
**Expect:** Exactly `Taskbar: No pending pins or order changes to apply.`
**Verify:** No Explorer restart occurred (Explorer PID is unchanged from
before the command).

### T06 — Taskbar Apply with pending pin (Notepad)
**Prerequisite:** Notepad is NOT currently pinned. If `Taskbar list` shows
Notepad, run `Taskbar unpin "Notepad"` first. No `LayoutModification.xml` on
disk.
**Run:**
```powershell
Taskbar pin "Notepad" -Apply $false
Taskbar apply
```
**Expect:** The second command triggers an Explorer restart and finishes by
printing a consolidated summary that begins with `Taskbar:` on its own line,
then one `  <n>. <state> - <name>` line per pinned item. The Notepad entry
has state `Added`. All previously-pinned items have state `Pinned`.
**Verify:** `Taskbar list` now includes `Notepad`. Item count is
`$OriginalTaskbarCount + 1`. `LayoutModification.xml` has been deleted (see
T22).

### T07 — Taskbar Unpin single
**Prerequisite:** Notepad is pinned (from T06).
**Run:** `Taskbar unpin "Notepad"`
**Expect:** Output contains `Taskbar: Unpinned Notepad`.
**Verify:** `Taskbar list` no longer shows Notepad. Item count is back to
`$OriginalTaskbarCount`.

### T08 — Taskbar Unpin not-pinned
**Run:** `Taskbar unpin "Notepad"` (immediately after T07 — Notepad is gone)
**Expect:** Output contains `Taskbar: 'Notepad' is not pinned.`
**Verify:** No error, exit code 0. Item count unchanged.

### T08b — Taskbar Unpin exact-match (no substring fallback)
**Prerequisite:** Pin both `Windows PowerShell` and `Windows PowerShell ISE`:
```powershell
Taskbar pin -Apps "Windows PowerShell, Windows PowerShell ISE" -Apply $false
Taskbar apply
```
**Run:** `Taskbar unpin "Windows PowerShell"`
**Expect:** Output contains `Taskbar: Unpinned Windows PowerShell` exactly once.
**Verify:** `Taskbar list` still shows `Windows PowerShell ISE` (the substring
match from the pre-fix behaviour would have wrongly unpinned it). Item count
decreased by 1.

### T08c — Taskbar Unpin glob wildcard
**Prerequisite:** `Windows PowerShell ISE` is still pinned (from T08b). Pin
`Windows PowerShell` again so both are present:
```powershell
Taskbar pin "Windows PowerShell" -Apply $false
Taskbar apply
```
**Run:** `Taskbar unpin "Windows Power*"`
**Expect:** Output contains `Taskbar: Unpinned Windows PowerShell` AND
`Taskbar: Unpinned Windows PowerShell ISE` (both match the glob).
**Verify:** `Taskbar list` shows neither. Item count decreased by 2 from the
start of this test.

### T09 — Taskbar Pin nonexistent desktop app
**Run:** `Taskbar pin "ZzzNonexistentAppXyz" -Apply $false`
**Expect:** Stderr contains
`Taskbar: ERROR: Could not find a Start Menu shortcut for 'ZzzNonexistentAppXyz'.`
followed by `Taskbar: ERROR: No apps could be resolved.`
**Verify:** No `LayoutModification.xml` written (or it is unchanged if one
already exists). Exit code non-zero.

### T10 — Taskbar Pin nonexistent UWP
**Run:** `Taskbar pin "UWP:ZzzNoSuchPackage" -Apply $false`
**Expect:** Stderr contains
`Taskbar: ERROR: No installed UWP app matches 'ZzzNoSuchPackage'.`
followed by `Taskbar: ERROR: No apps could be resolved.`

### T11 — Taskbar Apply with -Order (reorder existing)
**Prerequisite:** Both `Microsoft Edge` and `Microsoft Store` are pinned (they
are by default on a fresh Windows 11). No `LayoutModification.xml`.
**Run:** `Taskbar apply -Order "Microsoft Edge, Microsoft Store"`
**Expect:** Consolidated output shows an Explorer restart and a summary
beginning with `Taskbar:`. Position 1 is `Microsoft Edge`, position 2 is
`Microsoft Store`. At least one of them is reported with state `Moved` if they
were not already in that order, or `Pinned` for items whose predecessor did
not change.
**Verify:** `Taskbar list` confirms positions 1 and 2 are
`Microsoft Edge` then `Microsoft Store`.

### T12 — Taskbar Apply -Order idempotent
**Run:** `Taskbar apply -Order "Microsoft Edge, Microsoft Store"` (same order)
**Expect:** Exactly `Taskbar: No changes needed.`
**Verify:** No Explorer restart (Explorer PID unchanged from after T11).

### T12b — Taskbar Apply from snapshot with -Order
**Prerequisite:** A taskbar snapshot XML exists at
`"$env:TEMP\eh_tests_original.xml"` (from Setup). The taskbar currently has
`Microsoft Edge` in position 1 and `Microsoft Store` in position 2.
**Run:** `Taskbar apply "$env:TEMP\eh_tests_original.xml" -Order "Microsoft Store, Microsoft Edge"`
**Expect:** Output contains `Taskbar: Restored from snapshot.` Explorer restarts.
**Verify:** `Taskbar list` shows `Microsoft Store` in position 1 and
`Microsoft Edge` in position 2 (the reverse of T11 — the snapshot itself had
Edge first, but `-Order` reordered its items before the restore).

### T12c — Taskbar Apply from LayoutModificationTemplate with -Order (rejected)
**Prerequisite:** Any `LayoutModificationTemplate`-format XML file (e.g. one of
the `applied_layout_*.xml` files a prior apply created alongside the snapshot).
**Run:** `Taskbar apply "<that file>" -Order "Microsoft Edge"`
**Expect:** Stderr contains
`Taskbar: ERROR: -Order cannot be combined with a LayoutModificationTemplate file (only TaskbarSnapshot files can be reordered).`
**Verify:** Non-zero return. No Explorer restart.

### T13 — Taskbar Pin idempotent (already-pinned app)
**Prerequisite:** `Microsoft Edge` is pinned (verified in T11).
**Run:** `Taskbar pin "Microsoft Edge"` (uses default `-Apply $true`)
**Expect:** Output contains `Taskbar: Queued pin for Microsoft Edge` (the pin
is queued), then `Taskbar: No changes needed.` (because `ApplyPending` detects
the app is already pinned and neither `aNewPinsAdded` nor `aOrderChanged` is set).
**Verify:** Item count is unchanged. No Explorer restart. `LayoutModification.xml`
has been deleted.

### T14 — Exact vs substring match: Windows PowerShell / ISE
**Prerequisite:** Neither `Windows PowerShell` nor `Windows PowerShell ISE` is
currently pinned. Unpin either first if needed.
**Run:**
```powershell
Taskbar pin -Apps "Windows PowerShell, Windows PowerShell ISE" -Apply $false
Taskbar apply
```
**Expect:** The pin step prints
`Taskbar: Queued pins for Windows PowerShell, Windows PowerShell ISE`. The
apply step's summary lists two distinct `Added` entries — one for
`Windows PowerShell` and one for `Windows PowerShell ISE` (not two copies of
the same shortcut).
**Verify:** `Taskbar list` shows `Windows PowerShell` and
`Windows PowerShell ISE` as two separate items with different lnk paths. The
ResolveLnkPath exact-match rule must win here; a bug would cause both pins to
resolve to the same shortcut.
**Cleanup:** `Taskbar unpin "Windows PowerShell, Windows PowerShell ISE"` so
the count returns to `$OriginalTaskbarCount`.

### T15 — Taskbar Unpin comma-separated list
**Prerequisite:** Pin Notepad and Windows PowerShell so the list has two
removable targets: run
```powershell
Taskbar pin -Apps "Notepad, Windows PowerShell"
```
and wait for apply to finish.
**Run:** `Taskbar unpin "Notepad, Windows PowerShell"`
**Expect:** Output contains both `Taskbar: Unpinned Notepad` and
`Taskbar: Unpinned Windows PowerShell` lines.
**Verify:** Neither app is in `Taskbar list`. Item count is back to
`$OriginalTaskbarCount`.

### T16 — Taskbar Snapshot (default path)
**Run:** `Taskbar snapshot`
**Expect:** Output lines:
- `Taskbar: Snapshot saved to <temp path ending in snapshot.xml>`
- `  Items: <N>`
- `  Links: <M>` (where M ≤ N; UWP items contribute 0 links)
**Verify:** The reported path exists, the file contains `<TaskbarSnapshot`
with `count="<N>"`, and has N `<Item` elements. A `links/` sibling folder
exists next to the XML and contains M `.lnk` files.

### T17 — Taskbar Snapshot (explicit file path)
**Run:** `Taskbar snapshot "D:\Temp\eh_tests_explicit.xml"`
**Expect:** Output includes
`Taskbar: Snapshot saved to D:\Temp\eh_tests_explicit.xml`.
**Verify:** `D:\Temp\eh_tests_explicit.xml` exists and is valid XML with a
`<TaskbarSnapshot` root. A `D:\Temp\links\` folder exists containing `.lnk`
files copied from the current taskbar.

### T18 — Taskbar Snapshot (explicit directory path)
**Prerequisite:** `D:\Temp\eh_tests_snap_dir` exists (create it if not).
**Run:** `Taskbar snapshot "D:\Temp\eh_tests_snap_dir"`
**Expect:** Output includes `Taskbar: Snapshot saved to
D:\Temp\eh_tests_snap_dir\snapshot_<yyyyMMdd_HHmmss>.xml`.
**Verify:** A new XML file of that form exists in the directory.

### T19 — Taskbar UnpinAll
**Prerequisite:** T17 has been run, so `D:\Temp\eh_tests_explicit.xml` exists
as a restore point.
**Run:** `Taskbar unpinall`
**Expect:** Output includes `Taskbar: Unpinning <N> items...` then
`Taskbar: Unpinned <N> items.` where N matches `$OriginalTaskbarCount`.
**Verify:** `Taskbar list` prints `Taskbar: No items pinned.`

### T20 — Taskbar Apply from snapshot file
**Prerequisite:** Taskbar is empty (from T19). Snapshot file from T17 exists.
**Run:** `Taskbar apply "D:\Temp\eh_tests_explicit.xml"`
**Expect:** Output contains `Taskbar: Restored from snapshot.`
**Verify:** `Taskbar list` shows items again. Count equals
`$OriginalTaskbarCount`. The first three display names match `$OriginalTop3`
(order preserved from the snapshot).

### T21 — Taskbar Apply missing snapshot file
**Run:** `Taskbar apply "D:\Temp\eh_tests_does_not_exist.xml"`
**Expect:** Stderr contains
`Taskbar: ERROR: File not found: D:\Temp\eh_tests_does_not_exist.xml`.
Exit code non-zero.

### T22 — LayoutModification.xml is cleaned up after apply
**Run:** Re-run T06 (pin Notepad, then apply) or use any other apply that
triggers a restart. After the command finishes, check for the layout file.
**Verify:** `%LocalAppData%\Microsoft\Windows\Shell\LayoutModification.xml`
does NOT exist once the apply returns.
**Cleanup:** `Taskbar unpin "Notepad"` to restore state.

### T23 — QuickAccess List
**Run:** `QuickAccess list`
**Expect:** Either `QuickAccess: No items pinned.` (empty) or
`QuickAccess: Pinned items:` followed by one path per line (indented two
spaces). On a fresh Windows 11 there are usually default items like
`C:\Users\<user>\Desktop` and `C:\Users\<user>\Downloads`.

### T24 — QuickAccess Pin new folder
**Prerequisite:** `D:\Temp` exists and is NOT currently pinned. If it is
pinned, run `QuickAccess unpin "D:\Temp"` first.
**Run:** `QuickAccess pin "D:\Temp" -RestartExplorer $false`
**Expect:** Output contains `QuickAccess: Pinning D:\Temp` followed by
`QuickAccess: Pins applied. Restart Explorer to refresh.` (no restart because
`-RestartExplorer $false`).
**Verify:** `QuickAccess list` now contains `D:\Temp`.

### T25 — QuickAccess Pin idempotent
**Prerequisite:** `D:\Temp` is pinned (from T24).
**Run:** `QuickAccess pin "D:\Temp" -RestartExplorer $false`
**Expect:** Output contains `QuickAccess: Already pinned D:\Temp` followed by
`QuickAccess: No changes needed.`
**Verify:** QuickAccess item count is unchanged.

### T26 — QuickAccess Unpin
**Prerequisite:** `D:\Temp` is pinned.
**Run:** `QuickAccess unpin "D:\Temp"`
**Expect:** Output contains `QuickAccess: Unpinned D:\Temp`.
**Verify:** `QuickAccess list` no longer contains `D:\Temp`.

### T27 — QuickAccess Unpin not-pinned
**Run:** `QuickAccess unpin "D:\Temp"` (immediately after T26)
**Expect:** Output contains `QuickAccess: 'D:\Temp' is not pinned.`

### T28 — QuickAccess Snapshot
**Run:** `QuickAccess snapshot "D:\Temp\eh_tests_qa.xml"`
**Expect:** Output contains `QuickAccess: Snapshot saved to
D:\Temp\eh_tests_qa.xml` followed by `  Items: <N>`.
**Verify:** `D:\Temp\eh_tests_qa.xml` exists and has a `<QuickAccessSnapshot`
root with `<Item path="..."/>` elements.

### T29 — QuickAccess Apply (restore from snapshot)
**Prerequisite:** `D:\Temp\eh_tests_qa.xml` from T28 exists and does NOT
contain `D:\Temp` (T26 unpinned it before T28 was taken).
**Action:** First make the QA state differ from the snapshot by pinning
`D:\Temp` again: `QuickAccess pin "D:\Temp" -RestartExplorer $false`.
**Run:** `QuickAccess apply "D:\Temp\eh_tests_qa.xml" -RestartExplorer $false`
**Expect:** Output shows `QuickAccess: Unpinning D:\Temp` (because it's in the
current state but not in the snapshot), then `QuickAccess: Changes applied.
Restart Explorer to refresh.`
**Verify:** `QuickAccess list` matches the pre-T29 state (D:\Temp removed
again). Item count equals `$OriginalQACount`.

### T30 — QuickAccess Apply missing file
**Run:** `QuickAccess apply "D:\Temp\eh_tests_qa_missing.xml"`
**Expect:** Stderr contains
`QuickAccess: ERROR: File not found: D:\Temp\eh_tests_qa_missing.xml`.

### T31 — Explorer Restart
**Run:**
```powershell
$before = (Get-Process explorer -ErrorAction SilentlyContinue | Select -First 1).Id
Explorer restart
Start-Sleep -Seconds 2
$after = (Get-Process explorer -ErrorAction SilentlyContinue | Select -First 1).Id
```
**Expect:** Command prints `Explorer: Restarted.`
**Verify:** `$after -ne $before` (Explorer was killed and relaunched) and
`Get-Process explorer` returns at least one process.

### T32 — Explorer Restart skipped
**Run:**
```powershell
$before = (Get-Process explorer -ErrorAction SilentlyContinue | Select -First 1).Id
Explorer restart $false
$after = (Get-Process explorer -ErrorAction SilentlyContinue | Select -First 1).Id
```
**Expect:** Command prints `Explorer: Restart skipped.`
**Verify:** `$after -eq $before` (Explorer was not restarted).

### T33 — Taskbar apply "false" sentinel (PSM1 wrapper)
**Run:** `Taskbar apply false`
**Expect:** Output contains `Taskbar: Apply skipped.` (this is handled by the
PowerShell wrapper, not the C# library).
**Verify:** No Explorer restart.

## Teardown

1. Restore the taskbar from the original snapshot taken in Setup:
   `Taskbar apply "D:\Temp\eh_tests_original.xml"`
2. Run `Taskbar list` and verify the item count equals `$OriginalTaskbarCount`
   and the first three display names match `$OriginalTop3`.
3. Restore Quick Access from the original snapshot:
   `QuickAccess apply "D:\Temp\eh_tests_qa_original.xml"`
4. Run `QuickAccess list` and verify item count equals `$OriginalQACount`.
5. Delete any pending layout XML left behind:
   `Remove-Item "$env:LOCALAPPDATA\Microsoft\Windows\Shell\LayoutModification.xml" -ErrorAction SilentlyContinue`
6. Clean up test files in `D:\Temp`:
   - `eh_tests_original.xml` and its sibling `links\` folder
   - `eh_tests_qa_original.xml`
   - `eh_tests_explicit.xml` and any `applied_layout_*.xml` files
   - `eh_tests_snap_dir\` folder
   - `eh_tests_qa.xml`
   - `D:\Temp\links\` (created by T17)

## Report Format

```
# ExplorerHelper Test Report — <date>

| #   | Test                                | Result | Notes |
|-----|-------------------------------------|--------|-------|
| T01 | Version                             | PASS   | v2.0.0 |
| T02 | Taskbar List (populated)            | PASS   | 35 items |
| T03 | Taskbar Pin single (queue only)     | PASS   |        |
| ... | ...                                 | ...    | ...    |

Summary: X passed, Y failed out of 33 tests.
```

For each failure, include in the Notes column the exact output observed vs.
the expected output so the discrepancy can be debugged.
