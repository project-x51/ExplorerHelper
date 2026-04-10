#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

using ExplorerHelper.Interop;
using ExplorerHelper.Shared;

namespace ExplorerHelper.Commands;


/// <summary>
/// Handles listing, pinning, unpinning, snapshotting and restoring
/// applications on the Windows 11 taskbar. Pin queues entries to a
/// pending LayoutModification.xml; apply merges pending pins with the
/// current taskbar state and rebuilds the layout.
/// </summary>
public static class TaskbarCommands
{
    // Standard Start Menu shortcut locations to search for .lnk files
    private static readonly string[] SHORTCUT_SEARCH_PATHS =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
    ];

    // Where Windows stores shortcuts for currently pinned taskbar items
    private static readonly string TASKBAR_PINNED_PATH = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

    // Path where Windows reads taskbar layout provisioning XML
    private static readonly string LAYOUT_MOD_PATH = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        @"Microsoft\Windows\Shell\LayoutModification.xml");

    // Layout cache that must be cleared for provisioning to take effect
    private static readonly string LAYOUT_CACHE_PATH = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        @"Microsoft\Windows\Shell\DefaultLayouts");



    // ───────────────────────────────────────────────────────────
    //  taskbar list
    // ───────────────────────────────────────────────────────────

    /// <summary>
    /// Lists all applications currently pinned to the taskbar in their
    /// display order using the IPinnedList3 COM enumeration.
    /// </summary>
    public static int List()
    {

        try
        {
            using var aPinnedList = new PinnedList3();
            var aItems = aPinnedList.GetOrderedItems();

            if (aItems.Count == 0) {
                Console.WriteLine("Taskbar: No items pinned.");
                return 0;
            }

            Console.WriteLine("Taskbar: Pinned items:");
            for (int i = 0; i < aItems.Count; i++) {
                var aItem = aItems[i];
                string aSource = aItem.LnkPath ?? aItem.AppUserModelId ?? "unknown";
                Console.WriteLine($"  {i + 1}. {aItem.DisplayName}");
                Console.WriteLine($"     {aSource}");
            }

            return 0;
        }
        catch (Exception x)
        {
            Console.Error.WriteLine($"Taskbar: ERROR: {x.Message}");
            return 2;
        }

    }


    // ───────────────────────────────────────────────────────────
    //  taskbar snapshot [path]
    // ───────────────────────────────────────────────────────────

    /// <summary>
    /// Snapshots the current taskbar state to an XML file and copies all
    /// .lnk files to a backup folder. Does not modify the taskbar.
    /// </summary>
    public static int Snapshot(string? pathArg = null)
    {

        try
        {
            string aSnapshotPath = Utilities.ResolveSnapshotPath(pathArg, "ExplorerHelper_Snapshot", "snapshot");
            string aSnapshotDir = Path.GetDirectoryName(aSnapshotPath)!;

            Directory.CreateDirectory(aSnapshotDir);
            string aLinksDir = Path.Combine(aSnapshotDir, "links");
            Directory.CreateDirectory(aLinksDir);

            using var aPinnedList = new PinnedList3();
            var aItems = aPinnedList.GetOrderedItemsWithPidls();

            var aTempPaths = CopyLnkFiles(aItems, aLinksDir);

            WriteSnapshotXml(aSnapshotPath, aItems, aTempPaths);

            foreach (var aItem in aItems) {
                if (aItem.Pidl != IntPtr.Zero)
                    NativeMethods.CoTaskMemFree(aItem.Pidl);
            }

            Console.WriteLine($"Taskbar: Snapshot saved to {aSnapshotPath}");
            Console.WriteLine($"  Items: {aItems.Count}");
            Console.WriteLine($"  Links: {aTempPaths.Count}");
            return 0;
        }
        catch (Exception x)
        {
            Console.Error.WriteLine($"Taskbar: ERROR: {x.Message}");
            return 2;
        }

    }


    // ───────────────────────────────────────────────────────────
    //  taskbar unpinall
    // ───────────────────────────────────────────────────────────

    /// <summary>
    /// Unpins ALL items from the taskbar using raw PIDLs from enumeration.
    /// </summary>
    public static int UnpinAllCommand()
    {

        try
        {
            using var aPinnedList = new PinnedList3();
            var aItems = aPinnedList.GetOrderedItemsWithPidls();

            Console.WriteLine($"Taskbar: Unpinning {aItems.Count} items...");
            aPinnedList.UnpinAll(aItems);

            Console.WriteLine($"Taskbar: Unpinned {aItems.Count} items.");
            return 0;
        }
        catch (Exception x)
        {
            Console.Error.WriteLine($"Taskbar: ERROR: {x.Message}");
            return 2;
        }

    }


    // ───────────────────────────────────────────────────────────
    //  taskbar apply [path] [--order names]
    // ───────────────────────────────────────────────────────────

    /// <summary>
    /// Applies taskbar changes. When called with a snapshot path, restores
    /// from that file. When called without a path, merges any pending pins
    /// from LayoutModification.xml with the current taskbar state, optionally
    /// reorders, then rebuilds via unpinall + Replace.
    /// </summary>
    /// <param name="xmlPath">Optional snapshot XML path to restore from.</param>
    /// <param name="order">Optional comma-separated order list.</param>
    public static int Apply(string? xmlPath = null, string? order = null)
    {

        // Restore from snapshot file
        if (!string.IsNullOrEmpty(xmlPath))
            return ApplyFromFile(xmlPath, order);

        // Merge pending pins + optional reorder
        return ApplyPending(order);

    }


    /// <summary>
    /// Restores the taskbar from a snapshot or LayoutModification XML file.
    /// If <paramref name="order"/> is provided and the file is a snapshot,
    /// the snapshot items are reordered before being applied.
    /// </summary>
    private static int ApplyFromFile(string xmlPath, string? order = null)
    {

        try
        {
            if (!File.Exists(xmlPath)) {
                Console.Error.WriteLine($"Taskbar: ERROR: File not found: {xmlPath}");
                return 2;
            }

            string aXmlContent = File.ReadAllText(xmlPath);
            string aLayoutXml;

            if (aXmlContent.Contains("<TaskbarSnapshot")) {
                aLayoutXml = ConvertSnapshotToLayout(xmlPath, order);
            }
            else if (aXmlContent.Contains("<LayoutModificationTemplate")) {
                if (!string.IsNullOrEmpty(order)) {
                    Console.Error.WriteLine("Taskbar: ERROR: -Order cannot be combined with a LayoutModificationTemplate file (only TaskbarSnapshot files can be reordered).");
                    return 2;
                }
                aLayoutXml = aXmlContent;
            }
            else {
                Console.Error.WriteLine("Taskbar: ERROR: Unrecognised XML format.");
                return 2;
            }

            string aDir = Path.GetDirectoryName(xmlPath) ?? ".";
            string aCopyPath = Path.Combine(aDir,
                $"applied_layout_{DateTime.Now:yyyyMMdd_HHmmss}.xml");
            File.WriteAllText(aCopyPath, aLayoutXml, Encoding.UTF8);

            // Clear the current taskbar before applying. Windows Explorer
            // does not reliably re-order pins that are already pinned when
            // it re-reads LayoutModification.xml on restart — the old items
            // must be unpinned first so Explorer pins fresh in the new order.
            using (var aPinnedList = new PinnedList3()) {
                var aCurrent = aPinnedList.GetOrderedItemsWithPidls();
                if (aCurrent.Count > 0)
                    aPinnedList.UnpinAll(aCurrent);
            }

            bool aCleanOk = ApplyLayoutXml(aLayoutXml);
            Console.WriteLine("Taskbar: Restored from snapshot.");
            return aCleanOk ? 0 : 2;
        }
        catch (Exception x)
        {
            Console.Error.WriteLine($"Taskbar: ERROR: {x.Message}");
            return 2;
        }

    }


    /// <summary>
    /// Merges pending pins from LayoutModification.xml with the current
    /// taskbar state, optionally reorders, then rebuilds via unpinall + Replace.
    /// </summary>
    private static int ApplyPending(string? order)
    {

        try
        {
            // Read pending pins from the LayoutModification.xml
            var aPendingPins = ReadPendingPins();

            // Parse order list
            string[] aOrderNames = order != null
                ? order.Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                       .Select(n => n.Trim()).Where(n => n.Length > 0).ToArray()
                : Array.Empty<string>();

            if (aPendingPins.Count == 0 && aOrderNames.Length == 0) {
                Console.WriteLine("Taskbar: No pending pins or order changes to apply.");
                return 0;
            }

            // Create temp folder for backup
            string aTempDir = Path.Combine(
                Path.GetTempPath(),
                $"ExplorerHelper_Apply_{DateTime.Now:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(aTempDir);
            string aLinksDir = Path.Combine(aTempDir, "links");
            Directory.CreateDirectory(aLinksDir);

            // Snapshot current state with PIDLs
            using var aPinnedList = new PinnedList3();
            var aCurrentItems = aPinnedList.GetOrderedItemsWithPidls();
            try
            {
            var aTempPaths = CopyLnkFiles(aCurrentItems, aLinksDir);

            // Build merged list: current + pending (skip duplicates)
            // Track state of each item: "Added", "Pinned" (existing), "Moved", "Unchanged"
            var aMerged = new List<PinnedList3.PinnedItem>();
            var aItemStates = new Dictionary<string, string>(); // DisplayName -> state

            foreach (var aItem in aCurrentItems) {
                aMerged.Add(new PinnedList3.PinnedItem(aItem.DisplayName, aItem.LnkPath, aItem.AppUserModelId));
                aItemStates[aItem.DisplayName] = "Unchanged";
            }

            // Build a list of original positions for move detection
            var aOriginalOrder = aMerged.Select(i => i.DisplayName).ToList();

            bool aNewPinsAdded = false;
            foreach (var aPending in aPendingPins) {
                // Check for duplicates: prefer exact match on display name,
                // fall back to checking if the pending name matches the start
                // of an existing name (e.g. "Snagit 2025" matches "Snagit 2025_1")
                // but NOT partial substring (e.g. "Visual Studio" must NOT match "Visual Studio Code")
                bool aAlreadyPinned = aMerged.Any(i => {
                    if (aPending.AppUserModelId != null && i.AppUserModelId != null &&
                        i.AppUserModelId.Equals(aPending.AppUserModelId, StringComparison.OrdinalIgnoreCase))
                        return true;
                    if (aPending.DisplayName == null || i.DisplayName == null)
                        return false;
                    // Exact match
                    if (i.DisplayName.Equals(aPending.DisplayName, StringComparison.OrdinalIgnoreCase))
                        return true;
                    // Match with _N suffix (from prior apply cycles)
                    if (i.DisplayName.StartsWith(aPending.DisplayName, StringComparison.OrdinalIgnoreCase) &&
                        i.DisplayName.Length > aPending.DisplayName.Length &&
                        i.DisplayName[aPending.DisplayName.Length] == '_')
                        return true;
                    return false;
                });

                if (aAlreadyPinned)
                    continue;

                // Copy new .lnk to temp links dir using the shared helper
                // so filename collisions get a suffix rather than silently
                // mapping the new pin to an unrelated previously-copied file.
                if (aPending.LnkPath != null)
                    CopyLnkFile(aPending.LnkPath, aLinksDir, aTempPaths);

                aMerged.Add(aPending);
                aItemStates[aPending.DisplayName] = "Added";
                aNewPinsAdded = true;
            }

            // Apply ordering if specified
            bool aOrderChanged = false;
            if (aOrderNames.Length > 0) {
                var aOrdered = new List<PinnedList3.PinnedItem>();
                var aRemaining = new List<PinnedList3.PinnedItem>(aMerged);

                foreach (string aName in aOrderNames) {
                    // Prefer exact match, then match with _N suffix,
                    // then fall back to partial match (contains)
                    int aIdx = aRemaining.FindIndex(i =>
                        i.DisplayName.Equals(aName, StringComparison.OrdinalIgnoreCase));
                    if (aIdx < 0)
                        aIdx = aRemaining.FindIndex(i =>
                            i.DisplayName.StartsWith(aName, StringComparison.OrdinalIgnoreCase) &&
                            i.DisplayName.Length > aName.Length && i.DisplayName[aName.Length] == '_');
                    if (aIdx < 0)
                        aIdx = aRemaining.FindIndex(i =>
                            i.DisplayName.IndexOf(aName, StringComparison.OrdinalIgnoreCase) >= 0);

                    if (aIdx >= 0) {
                        aOrdered.Add(aRemaining[aIdx]);
                        aRemaining.RemoveAt(aIdx);
                    }
                }

                aOrdered.AddRange(aRemaining);

                // Detect order changes by comparing relative ordering.
                // An item is "Moved" only if its predecessor changed.
                for (int i = 0; i < aOrdered.Count; i++) {
                    string aNewName = aOrdered[i].DisplayName;
                    int aOrigIdx = aOriginalOrder.IndexOf(aNewName);

                    if (aOrigIdx < 0)
                        continue; // new item, already marked "Added"

                    // Check if predecessor is the same in both lists
                    string? aNewPred = i > 0 ? aOrdered[i - 1].DisplayName : null;
                    string? aOrigPred = aOrigIdx > 0 ? aOriginalOrder[aOrigIdx - 1] : null;

                    if (aNewPred != aOrigPred) {
                        aOrderChanged = true;
                        if (aItemStates.ContainsKey(aNewName) &&
                            aItemStates[aNewName] == "Unchanged")
                            aItemStates[aNewName] = "Moved";
                    }
                }

                aMerged = aOrdered;
            }

            // Mark remaining unchanged items as "Pinned"
            foreach (var aKey in aItemStates.Keys.ToList()) {
                if (aItemStates[aKey] == "Unchanged")
                    aItemStates[aKey] = "Pinned";
            }

            // Clean up pending XML regardless of whether we apply
            try { File.Delete(LAYOUT_MOD_PATH); } catch { /* best effort */ }

            // Skip the expensive rebuild if nothing changed
            if (!aNewPinsAdded && !aOrderChanged) {
                Console.WriteLine("Taskbar: No changes needed.");
                return 0;
            }

            // Write merged snapshot and hand off to ApplyFromFile. ApplyFromFile
            // owns the unpin step now — doing it here too would be wasted work
            // and would invalidate aCurrentItems' PIDLs before the finally
            // runs.
            string aSnapshotPath = Path.Combine(aTempDir, "merged.xml");
            WriteSnapshotXml(aSnapshotPath, aMerged, aTempPaths);

            int aApplyResult = ApplyFromFile(aSnapshotPath);

            // Show consolidated result
            Console.WriteLine($"Taskbar:");
            for (int i = 0; i < aMerged.Count; i++) {
                string aName = aMerged[i].DisplayName;
                string aState = aItemStates.ContainsKey(aName) ? aItemStates[aName] : "Pinned";
                Console.WriteLine($"  {i + 1}. {aState} - {aName}");
            }

            return aApplyResult;
            }
            finally
            {
                // Free every PIDL regardless of control flow (success, no-op,
                // or exception). The list records themselves are left with
                // zeroed IntPtrs but are no longer used after this point.
                foreach (var aItem in aCurrentItems) {
                    if (aItem.Pidl != IntPtr.Zero)
                        NativeMethods.CoTaskMemFree(aItem.Pidl);
                }
            }
        }
        catch (Exception x)
        {
            Console.Error.WriteLine($"Taskbar: ERROR: {x.Message}");
            return 2;
        }

    }


    /// <summary>
    /// Reads pending pin entries from the LayoutModification.xml, extracting
    /// DesktopApplicationLinkPath and AppUserModelID values. UWP entries
    /// have their DisplayName resolved to the friendly app name via
    /// Get-StartApps (single shell-out, amortized across all UWP items).
    /// </summary>
    private static List<PinnedList3.PinnedItem> ReadPendingPins()
    {

        var aResults = new List<PinnedList3.PinnedItem>();

        if (!File.Exists(LAYOUT_MOD_PATH))
            return aResults;

        try
        {
            var aDoc = new XmlDocument();
            aDoc.Load(LAYOUT_MOD_PATH);

            var aNs = new XmlNamespaceManager(aDoc.NameTable);
            aNs.AddNamespace("t", "http://schemas.microsoft.com/Start/2014/TaskbarLayout");

            // Desktop apps
            foreach (XmlNode aNode in aDoc.SelectNodes("//t:DesktopApp", aNs)!) {
                string? aPath = ((XmlElement)aNode).GetAttribute("DesktopApplicationLinkPath");
                if (!string.IsNullOrEmpty(aPath)) {
                    string aName = Path.GetFileNameWithoutExtension(aPath);
                    aResults.Add(new PinnedList3.PinnedItem(aName, aPath, null));
                }
            }

            // UWP apps — collect first, then resolve friendly names in one shot
            var aUwpAumids = new List<string>();
            foreach (XmlNode aNode in aDoc.SelectNodes("//t:UWA", aNs)!) {
                string? aAumid = ((XmlElement)aNode).GetAttribute("AppUserModelID");
                if (!string.IsNullOrEmpty(aAumid))
                    aUwpAumids.Add(aAumid);
            }

            if (aUwpAumids.Count > 0) {
                var aNameMap = ResolveUwpFriendlyNames(aUwpAumids);
                foreach (string aAumid in aUwpAumids) {
                    string aFriendly = aNameMap.TryGetValue(aAumid, out var aName) ? aName : aAumid;
                    aResults.Add(new PinnedList3.PinnedItem(aFriendly, null, aAumid));
                }
            }
        }
        catch {
            // Malformed XML — treat as no pending pins
        }

        return aResults;

    }


    /// <summary>
    /// Resolves a list of UWP AppUserModelIDs to their friendly display
    /// names via 'Get-StartApps'. Returns a dictionary keyed on AUMID.
    /// Any AUMID not found in Get-StartApps is simply absent from the
    /// returned map (callers should fall back to the raw AUMID).
    /// </summary>
    private static Dictionary<string, string> ResolveUwpFriendlyNames(List<string> aumids)
    {

        var aMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (aumids.Count == 0)
            return aMap;

        try
        {
            // Emit 'Name<TAB>AppID' pairs for every Start Menu app. We filter
            // in C# rather than building a PS pipeline so we can pass an
            // arbitrary number of AUMIDs without worrying about quoting.
            string aScript = "Get-StartApps | ForEach-Object { \"$($_.Name)`t$($_.AppID)\" }";
            string aEncoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(aScript));

            var aProcess = new Process {
                StartInfo = new ProcessStartInfo {
                    FileName = "powershell",
                    Arguments = $"-NoProfile -EncodedCommand {aEncoded}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            aProcess.Start();
            string aOutput = aProcess.StandardOutput.ReadToEnd();
            aProcess.StandardError.ReadToEnd();
            aProcess.WaitForExit(10000);

            var aWanted = new HashSet<string>(aumids, StringComparer.OrdinalIgnoreCase);
            foreach (string aLine in aOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
                int aTab = aLine.IndexOf('\t');
                if (aTab <= 0)
                    continue;
                string aName = aLine.Substring(0, aTab);
                string aAppId = aLine.Substring(aTab + 1);
                if (aWanted.Contains(aAppId) && !aMap.ContainsKey(aAppId))
                    aMap[aAppId] = aName;
            }
        }
        catch
        {
            // Best effort — caller will fall back to the raw AUMID.
        }

        return aMap;

    }


    // ───────────────────────────────────────────────────────────
    //  taskbar pin
    // ───────────────────────────────────────────────────────────

    /// <summary>
    /// Queues one or more applications to be pinned to the taskbar.
    /// Entries prefixed with "UWP:" are pinned as UWP/Store apps;
    /// all others are resolved as desktop apps. When apply is true
    /// (default), immediately applies the changes.
    /// </summary>
    /// <param name="apps">Comma-separated list of apps to pin.</param>
    /// <param name="apply">Whether to immediately apply changes.</param>
    public static int Pin(string apps, bool apply = true)
    {

        string[] aEntries = apps
            .Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim())
            .Where(e => e.Length > 0)
            .ToArray();

        var aQueuedNames = new List<string>();

        foreach (string aEntry in aEntries) {
            if (aEntry.StartsWith("UWP:", StringComparison.OrdinalIgnoreCase)) {
                string aPattern = aEntry.Substring(4).Trim();
                string? aAumid = FindUwpAumid(aPattern);
                if (aAumid == null) {
                    Console.Error.WriteLine($"Taskbar: ERROR: No installed UWP app matches '{aPattern}'.");
                    continue;
                }
                QueuePinEntry($"<taskbar:UWA AppUserModelID=\"{Utilities.XmlEscape(aAumid)}\"/>", aAumid);
                aQueuedNames.Add(aPattern);
            }
            else {
                string? aLnkPath = ResolveLnkPath(aEntry);
                if (aLnkPath == null) {
                    Console.Error.WriteLine($"Taskbar: ERROR: Could not find a Start Menu shortcut for '{aEntry}'.");
                    continue;
                }
                string aName = Path.GetFileNameWithoutExtension(aLnkPath);
                QueuePinEntry($"<taskbar:DesktopApp DesktopApplicationLinkPath=\"{Utilities.XmlEscape(aLnkPath)}\"/>", aName);
                aQueuedNames.Add(aName);
            }
        }

        if (aQueuedNames.Count == 0) {
            Console.Error.WriteLine("Taskbar: ERROR: No apps could be resolved.");
            return 2;
        }

        string aPinWord = aQueuedNames.Count == 1 ? "pin" : "pins";
        Console.WriteLine($"Taskbar: Queued {aPinWord} for {string.Join(", ", aQueuedNames)}");

        if (apply)
            return ApplyPending(order: null);

        return 0;

    }


    /// <summary>
    /// Unpins one or more applications from the taskbar by name. Accepts
    /// a single name or a comma-separated list.
    /// </summary>
    public static int Unpin(string names)
    {

        string[] aNames = names
            .Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(n => n.Trim())
            .Where(n => n.Length > 0)
            .ToArray();

        int aResult = 0;
        using var aPinnedList = new PinnedList3();

        foreach (string aName in aNames) {
            var aLnkPaths = FindPinnedShortcuts(aName);
            if (aLnkPaths.Count == 0) {
                Console.WriteLine($"Taskbar: '{aName}' is not pinned.");
                continue;
            }

            foreach (string aLnkPath in aLnkPaths) {
                string aDisplay = Path.GetFileNameWithoutExtension(aLnkPath);
                IntPtr aPidl = IntPtr.Zero;
                try
                {
                    int aHr = NativeMethods.SHParseDisplayName(aLnkPath, IntPtr.Zero, out aPidl, 0, out _);
                    if (aHr != 0 || aPidl == IntPtr.Zero) {
                        Console.Error.WriteLine($"Taskbar: ERROR: Failed to resolve PIDL for '{aDisplay}'");
                        aResult = 2;
                        continue;
                    }

                    aHr = aPinnedList.Unpin(aPidl);
                    if (aHr != 0) {
                        Console.Error.WriteLine($"Taskbar: ERROR: Unpin failed for '{aDisplay}'");
                        aResult = 2;
                        continue;
                    }

                    Console.WriteLine($"Taskbar: Unpinned {aDisplay}");
                }
                finally
                {
                    if (aPidl != IntPtr.Zero)
                        NativeMethods.CoTaskMemFree(aPidl);
                }
            }
        }

        return aResult;

    }


    // ───────────────────────────────────────────────────────────
    //  Private: Pin queue
    // ───────────────────────────────────────────────────────────

    /// <summary>
    /// Queues a single pin entry to the LayoutModification.xml. If the
    /// XML already exists, the entry is appended to the existing PinList.
    /// Duplicate entries are skipped.
    /// </summary>
    private static void QueuePinEntry(string pinElement, string displayName)
    {

        if (File.Exists(LAYOUT_MOD_PATH)) {
            string aExisting = File.ReadAllText(LAYOUT_MOD_PATH);
            if (aExisting.Contains(pinElement))
                return;
            if (aExisting.Contains("</taskbar:TaskbarPinList>")) {
                string aXml = aExisting.Replace("</taskbar:TaskbarPinList>",
                    $"        {pinElement}\n      </taskbar:TaskbarPinList>");
                File.WriteAllText(LAYOUT_MOD_PATH, aXml, Encoding.UTF8);
                return;
            }
        }

        // Fresh XML
        string aFreshXml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <LayoutModificationTemplate
                xmlns="http://schemas.microsoft.com/Start/2014/LayoutModification"
                xmlns:defaultlayout="http://schemas.microsoft.com/Start/2014/FullDefaultLayout"
                xmlns:start="http://schemas.microsoft.com/Start/2014/StartLayout"
                xmlns:taskbar="http://schemas.microsoft.com/Start/2014/TaskbarLayout"
                Version="1">
              <CustomTaskbarLayoutCollection>
                <defaultlayout:TaskbarLayout>
                  <taskbar:TaskbarPinList>
                    {pinElement}
                  </taskbar:TaskbarPinList>
                </defaultlayout:TaskbarLayout>
              </CustomTaskbarLayoutCollection>
            </LayoutModificationTemplate>
            """;

        File.WriteAllText(LAYOUT_MOD_PATH, aFreshXml, Encoding.UTF8);

    }


    /// <summary>
    /// Searches installed UWP packages for an AUMID matching the given regex
    /// by invoking PowerShell's Get-AppxPackage.
    /// </summary>
    private static string? FindUwpAumid(string pattern)
    {

        try
        {
            string aScript =
                "Get-AppxPackage | Where-Object { $_.Name -match '" + pattern.Replace("'", "''") + "' } | ForEach-Object {\n" +
                "  $pkg = $_\n" +
                "  try {\n" +
                "    $manifest = Get-AppxPackageManifest -Package $pkg.PackageFullName\n" +
                "    $manifest.Package.Applications.Application | ForEach-Object {\n" +
                "      Write-Output ($pkg.PackageFamilyName + '!' + $_.Id)\n" +
                "    }\n" +
                "  } catch { }\n" +
                "} | Select-Object -First 1";

            string aEncoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(aScript));

            var aProcess = new Process {
                StartInfo = new ProcessStartInfo {
                    FileName = "powershell",
                    Arguments = $"-NoProfile -EncodedCommand {aEncoded}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            aProcess.Start();
            string aOutput = aProcess.StandardOutput.ReadToEnd().Trim();
            aProcess.StandardError.ReadToEnd(); // discard progress/CLIXML noise
            aProcess.WaitForExit(10000);

            if (!string.IsNullOrEmpty(aOutput)) {
                Console.WriteLine($"Taskbar: Matched UWP: {aOutput}");
                return aOutput;
            }
        }
        catch (Exception x)
        {
            Console.Error.WriteLine($"Taskbar: ERROR resolving UWP app: {x.Message}");
        }

        return null;

    }


    // ───────────────────────────────────────────────────────────
    //  Private: Snapshot helpers
    // ───────────────────────────────────────────────────────────

    /// <summary>
    /// Copies all .lnk files referenced by pinned items to a target
    /// directory. Returns a mapping from original path to relative
    /// temp path.
    /// </summary>
    private static Dictionary<string, string> CopyLnkFiles(
        List<PinnedList3.PinnedItemWithPidl> items, string targetDir)
    {

        var aMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var aItem in items) {
            if (aItem.LnkPath != null)
                CopyLnkFile(aItem.LnkPath, targetDir, aMapping);
        }

        return aMapping;

    }


    /// <summary>
    /// Copies a single .lnk file into targetDir with collision-safe
    /// suffix numbering (e.g. 'Foo.lnk' -> 'Foo_1.lnk' if 'Foo.lnk'
    /// already exists in targetDir) and records the mapping under
    /// the original source path. No-ops if the source file is missing.
    /// </summary>
    private static void CopyLnkFile(string srcPath, string targetDir,
        Dictionary<string, string> mapping)
    {

        if (!File.Exists(srcPath))
            return;

        string aDestName = Path.GetFileName(srcPath);
        string aDest = Path.Combine(targetDir, aDestName);

        int aSuffix = 1;
        while (File.Exists(aDest)) {
            string aBase = Path.GetFileNameWithoutExtension(aDestName);
            aDest = Path.Combine(targetDir, $"{aBase}_{aSuffix}{Path.GetExtension(aDestName)}");
            aSuffix++;
        }

        File.Copy(srcPath, aDest);
        mapping[srcPath] = "links/" + Path.GetFileName(aDest);

    }


    /// <summary>
    /// Writes a taskbar snapshot XML file. Works with both PinnedItem
    /// and PinnedItemWithPidl since both share the same properties.
    /// </summary>
    private static void WriteSnapshotXml(string path,
        IEnumerable<PinnedList3.PinnedItem> items,
        Dictionary<string, string> paths)
    {

        var aItemList = items.ToList();
        var aSb = new StringBuilder();
        aSb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        aSb.AppendLine($"<TaskbarSnapshot timestamp=\"{DateTime.Now:O}\" count=\"{aItemList.Count}\">");

        foreach (var aItem in aItemList) {
            string aType = aItem.LnkPath != null ? "Desktop" : "UWP";

            aSb.Append($"  <Item displayName=\"{Utilities.XmlEscape(aItem.DisplayName)}\" type=\"{aType}\"");

            if (aItem.LnkPath != null) {
                aSb.Append($" lnkPath=\"{Utilities.XmlEscape(aItem.LnkPath)}\"");
                if (paths.TryGetValue(aItem.LnkPath, out string? aTempPath))
                    aSb.Append($" path=\"{Utilities.XmlEscape(aTempPath)}\"");
            }
            else if (aItem.AppUserModelId != null) {
                aSb.Append($" appUserModelId=\"{Utilities.XmlEscape(aItem.AppUserModelId)}\"");
            }

            aSb.AppendLine("/>");
        }

        aSb.AppendLine("</TaskbarSnapshot>");
        File.WriteAllText(path, aSb.ToString(), Encoding.UTF8);

    }


    // ───────────────────────────────────────────────────────────
    //  Private: Apply helpers
    // ───────────────────────────────────────────────────────────

    /// <summary>
    /// Converts a TaskbarSnapshot XML to a LayoutModification.xml with
    /// PinListPlacement="Replace". If <paramref name="order"/> is provided,
    /// the snapshot items are reordered before being emitted using the
    /// same matching cascade as ApplyPending (exact → _N suffix → substring).
    /// </summary>
    private static string ConvertSnapshotToLayout(string snapshotPath, string? order = null)
    {

        var aDoc = new XmlDocument();
        aDoc.Load(snapshotPath);

        string aBaseDir = Path.GetDirectoryName(Path.GetFullPath(snapshotPath)) ?? ".";

        // First pass: parse each snapshot item into (displayName, pinEntryXml)
        var aItems = new List<(string DisplayName, string PinEntry)>();
        foreach (XmlNode aNode in aDoc.DocumentElement!.SelectNodes("Item")!) {
            var aElem = (XmlElement)aNode;
            string aType = aElem.GetAttribute("type");
            string aDisplayName = aElem.GetAttribute("displayName");

            if (aType == "Desktop") {
                string? aTempPath = aElem.GetAttribute("path");
                string? aLnkPath = aElem.GetAttribute("lnkPath");

                string? aResolvedPath = null;
                if (!string.IsNullOrEmpty(aTempPath)) {
                    aResolvedPath = Path.GetFullPath(Path.Combine(aBaseDir, aTempPath));
                    if (!File.Exists(aResolvedPath))
                        aResolvedPath = null;
                }
                if (aResolvedPath == null && !string.IsNullOrEmpty(aLnkPath) && File.Exists(aLnkPath))
                    aResolvedPath = aLnkPath;

                if (aResolvedPath != null)
                    aItems.Add((aDisplayName, $"        <taskbar:DesktopApp DesktopApplicationLinkPath=\"{Utilities.XmlEscape(aResolvedPath)}\"/>"));
            }
            else if (aType == "UWP") {
                string? aAumid = aElem.GetAttribute("appUserModelId");
                if (!string.IsNullOrEmpty(aAumid))
                    aItems.Add((aDisplayName, $"        <taskbar:UWA AppUserModelID=\"{Utilities.XmlEscape(aAumid)}\"/>"));
            }
        }

        // Apply ordering if specified
        if (!string.IsNullOrEmpty(order)) {
            string[] aOrderNames = order!
                .Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(n => n.Trim()).Where(n => n.Length > 0).ToArray();

            var aOrdered = new List<(string DisplayName, string PinEntry)>();
            var aRemaining = new List<(string DisplayName, string PinEntry)>(aItems);

            foreach (string aName in aOrderNames) {
                // exact → _N suffix → substring (same cascade as ApplyPending)
                int aIdx = aRemaining.FindIndex(i =>
                    i.DisplayName.Equals(aName, StringComparison.OrdinalIgnoreCase));
                if (aIdx < 0)
                    aIdx = aRemaining.FindIndex(i =>
                        i.DisplayName.StartsWith(aName, StringComparison.OrdinalIgnoreCase) &&
                        i.DisplayName.Length > aName.Length && i.DisplayName[aName.Length] == '_');
                if (aIdx < 0)
                    aIdx = aRemaining.FindIndex(i =>
                        i.DisplayName.IndexOf(aName, StringComparison.OrdinalIgnoreCase) >= 0);

                if (aIdx >= 0) {
                    aOrdered.Add(aRemaining[aIdx]);
                    aRemaining.RemoveAt(aIdx);
                }
            }

            aOrdered.AddRange(aRemaining);
            aItems = aOrdered;
        }

        var aPinEntries = new StringBuilder();
        foreach (var aItem in aItems)
            aPinEntries.AppendLine(aItem.PinEntry);

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <LayoutModificationTemplate
                xmlns="http://schemas.microsoft.com/Start/2014/LayoutModification"
                xmlns:defaultlayout="http://schemas.microsoft.com/Start/2014/FullDefaultLayout"
                xmlns:start="http://schemas.microsoft.com/Start/2014/StartLayout"
                xmlns:taskbar="http://schemas.microsoft.com/Start/2014/TaskbarLayout"
                Version="1">
              <CustomTaskbarLayoutCollection PinListPlacement="Replace">
                <defaultlayout:TaskbarLayout>
                  <taskbar:TaskbarPinList>
            {aPinEntries}      </taskbar:TaskbarPinList>
                </defaultlayout:TaskbarLayout>
              </CustomTaskbarLayoutCollection>
            </LayoutModificationTemplate>
            """;

    }


    /// <summary>
    /// Writes a LayoutModification.xml, clears the layout cache,
    /// restarts Explorer, waits for pins to appear, then cleans up.
    /// Returns true on success, false if the final cleanup of
    /// LayoutModification.xml failed (stale XML will be reapplied on
    /// next login).
    /// </summary>
    private static bool ApplyLayoutXml(string xml)
    {

        File.WriteAllText(LAYOUT_MOD_PATH, xml, Encoding.UTF8);

        if (Directory.Exists(LAYOUT_CACHE_PATH))
            Directory.Delete(LAYOUT_CACHE_PATH, recursive: true);

        return ExplorerCommands.RestartAndWaitForPins();

    }


    // ───────────────────────────────────────────────────────────
    //  Private: Helpers
    // ───────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves an .exe path to its Start Menu .lnk shortcut.
    /// </summary>
    private static string? ResolveLnkPath(string path)
    {

        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            return Path.GetFullPath(path);

        string aExeName = Path.GetFileNameWithoutExtension(path);
        string? aPartialMatch = null;

        foreach (string aSearchDir in SHORTCUT_SEARCH_PATHS) {
            if (!Directory.Exists(aSearchDir))
                continue;

            foreach (string aLnk in Directory.EnumerateFiles(aSearchDir, "*.lnk", SearchOption.AllDirectories)) {
                string aLnkName = Path.GetFileNameWithoutExtension(aLnk);

                // Exact match — return immediately
                if (aLnkName.Equals(aExeName, StringComparison.OrdinalIgnoreCase))
                    return aLnk;

                // Partial match — keep as fallback (first one wins)
                if (aPartialMatch == null && aLnkName.IndexOf(aExeName, StringComparison.OrdinalIgnoreCase) >= 0)
                    aPartialMatch = aLnk;
            }
        }

        return aPartialMatch;

    }


    /// <summary>
    /// Finds .lnk shortcuts in the pinned taskbar folder matching the
    /// given name. If the name contains '*' or '?', it is treated as a
    /// glob and every matching shortcut is returned. Otherwise an exact
    /// (case-insensitive) filename match is used and at most one path
    /// is returned.
    /// </summary>
    private static List<string> FindPinnedShortcuts(string nameOrPath)
    {

        var aResults = new List<string>();

        if (!Directory.Exists(TASKBAR_PINNED_PATH))
            return aResults;

        string aSearchName = Path.GetFileNameWithoutExtension(nameOrPath);
        bool aIsGlob = aSearchName.IndexOfAny(new[] { '*', '?' }) >= 0;

        if (aIsGlob) {
            // Convert glob to regex: escape everything, then re-expand * and ?
            string aPattern = "^" + Regex.Escape(aSearchName)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            var aRegex = new Regex(aPattern, RegexOptions.IgnoreCase);

            foreach (string aLnk in Directory.EnumerateFiles(TASKBAR_PINNED_PATH, "*.lnk")) {
                string aLnkName = Path.GetFileNameWithoutExtension(aLnk);
                if (aRegex.IsMatch(aLnkName))
                    aResults.Add(aLnk);
            }
        }
        else {
            foreach (string aLnk in Directory.EnumerateFiles(TASKBAR_PINNED_PATH, "*.lnk")) {
                string aLnkName = Path.GetFileNameWithoutExtension(aLnk);
                if (aLnkName.Equals(aSearchName, StringComparison.OrdinalIgnoreCase)) {
                    aResults.Add(aLnk);
                    break;
                }
            }
        }

        return aResults;

    }


}
