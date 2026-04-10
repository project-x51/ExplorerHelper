#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;

using ExplorerHelper.Shared;

namespace ExplorerHelper.Commands;


/// <summary>
/// Handles listing, pinning, unpinning, snapshotting and restoring folders
/// in Windows Explorer's Quick Access panel via the Shell.Application COM
/// interface. Pin is idempotent — it checks before invoking to avoid the
/// toggle behaviour of the pintohome verb.
/// </summary>
public static class QuickAccessCommands
{
    // Shell namespace GUID for Quick Access
    private const string QUICK_ACCESS_GUID = "shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}";


    /// <summary>
    /// Normalizes a path for equality comparison: resolves to full path
    /// (expanding '..', relative segments) and strips any trailing
    /// directory separator. Falls back to the trimmed input if the path
    /// can't be resolved (UNC, shell namespace, etc.).
    /// </summary>
    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;
        try {
            return Path.GetFullPath(path).TrimEnd('\\', '/');
        }
        catch {
            return path.TrimEnd('\\', '/');
        }
    }


    /// <summary>
    /// Case-insensitive path equality using NormalizePath on both sides.
    /// </summary>
    private static bool PathsEqual(string a, string b)
        => string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.OrdinalIgnoreCase);


    /// <summary>
    /// Creates a Shell.Application COM instance. Callers are responsible
    /// for releasing it via ReleaseCom() in a finally block so the RCW
    /// isn't left to the GC.
    /// </summary>
    private static dynamic CreateShell()
        => Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;


    /// <summary>
    /// Releases a COM RCW explicitly. Swallows exceptions — release is
    /// best-effort cleanup.
    /// </summary>
    private static void ReleaseCom(object? com)
    {
        if (com == null)
            return;
        try { Marshal.FinalReleaseComObject(com); } catch { }
    }


    /// <summary>
    /// Lists all items currently pinned to Quick Access.
    /// </summary>
    public static int List()
    {

        var aItems = GetPinnedItems();
        if (aItems.Count == 0) {
            Console.WriteLine("QuickAccess: No items pinned.");
            return 0;
        }

        Console.WriteLine("QuickAccess: Pinned items:");
        foreach (string aPath in aItems)
            Console.WriteLine($"  {aPath}");

        return 0;

    }


    /// <summary>
    /// Pins one or more folders to Quick Access. Accepts a single path or
    /// a comma-separated list.
    /// </summary>
    public static int Pin(string paths, bool restart = true)
    {

        // Dedupe inputs on normalized full path (case-insensitive). Passing
        // the same path twice in one call would otherwise toggle it back off
        // because 'pintohome' is a toggle verb.
        string[] aFolders = Utilities.SplitList(paths)
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        dynamic? aShell = null;
        try {
            aShell = CreateShell();
            List<string> aPinnedPaths = GetPinnedItems(aShell);
            int aResult = 0;
            bool aChanged = false;

            foreach (string aFolderPath in aFolders) {
                bool aAlreadyPinned = aPinnedPaths.Any(p => PathsEqual(p, aFolderPath));

                if (aAlreadyPinned) {
                    Console.WriteLine($"QuickAccess: Already pinned {aFolderPath}");
                    continue;
                }

                try {
                    dynamic aFolder = aShell.NameSpace(aFolderPath);
                    if (aFolder == null) {
                        Console.Error.WriteLine($"QuickAccess: ERROR: Could not access folder '{aFolderPath}'.");
                        aResult = 2;
                        continue;
                    }

                    aFolder.Self.InvokeVerb("pintohome");
                    Console.WriteLine($"QuickAccess: Pinning {aFolderPath}");
                    aChanged = true;

                    // Track the newly-pinned path so a later duplicate in the
                    // same invocation is caught as already pinned.
                    aPinnedPaths.Add(aFolderPath);
                }
                catch (Exception x) {
                    Console.Error.WriteLine($"QuickAccess: ERROR: {x.Message}");
                    aResult = 2;
                }
            }

            if (aChanged && restart) {
                Console.WriteLine("QuickAccess: Restarting Explorer");
                ExplorerCommands.Restart();
            }
            else if (aChanged)
                Console.WriteLine("QuickAccess: Pins applied. Restart Explorer to refresh.");
            else
                Console.WriteLine("QuickAccess: No changes needed.");

            return aResult;
        }
        finally {
            ReleaseCom(aShell);
        }

    }


    /// <summary>
    /// Unpins a folder from Quick Access.
    /// </summary>
    public static int Unpin(string folderPath)
    {

        dynamic? aShell = null;
        try {
            aShell = CreateShell();
            dynamic aQuickAccess = aShell.NameSpace(QUICK_ACCESS_GUID);
            if (aQuickAccess == null) {
                Console.Error.WriteLine("QuickAccess: ERROR: Could not access Quick Access namespace.");
                return 2;
            }

            foreach (dynamic aItem in aQuickAccess.Items()) {
                if (PathsEqual((string)aItem.Path, folderPath)) {
                    aItem.InvokeVerb("unpinfromhome");
                    Console.WriteLine($"QuickAccess: Unpinned {folderPath}");
                    return 0;
                }
            }

            Console.WriteLine($"QuickAccess: '{folderPath}' is not pinned.");
            return 0;
        }
        catch (Exception x) {
            Console.Error.WriteLine($"QuickAccess: ERROR: {x.Message}");
            return 2;
        }
        finally {
            ReleaseCom(aShell);
        }

    }


    /// <summary>
    /// Snapshots the current Quick Access pinned items to an XML file.
    /// </summary>
    public static int Snapshot(string? pathArg = null)
    {

        try
        {
            string aSnapshotPath = Utilities.ResolveSnapshotPath(pathArg, "ExplorerHelper_QA_Snapshot", "quickaccess");
            string aSnapshotDir = Path.GetDirectoryName(aSnapshotPath)!;
            Directory.CreateDirectory(aSnapshotDir);

            var aItems = GetPinnedItems();

            var aSb = new StringBuilder();
            aSb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            aSb.AppendLine($"<QuickAccessSnapshot timestamp=\"{DateTime.Now:O}\" count=\"{aItems.Count}\">");
            foreach (string aPath in aItems)
                aSb.AppendLine($"  <Item path=\"{Utilities.XmlEscape(aPath)}\"/>");
            aSb.AppendLine("</QuickAccessSnapshot>");

            File.WriteAllText(aSnapshotPath, aSb.ToString(), Encoding.UTF8);

            Console.WriteLine($"QuickAccess: Snapshot saved to {aSnapshotPath}");
            Console.WriteLine($"  Items: {aItems.Count}");
            return 0;
        }
        catch (Exception x)
        {
            Console.Error.WriteLine($"QuickAccess: ERROR: {x.Message}");
            return 2;
        }

    }


    /// <summary>
    /// Applies a Quick Access snapshot from an XML file. Pins items in the
    /// file that aren't pinned, unpins items not in the file. Idempotent.
    /// </summary>
    public static int Apply(string xmlPath, bool restart = true)
    {

        dynamic? aShell = null;
        try
        {
            if (!File.Exists(xmlPath)) {
                Console.Error.WriteLine($"QuickAccess: ERROR: File not found: {xmlPath}");
                return 2;
            }

            // Read desired paths from the snapshot
            var aDesiredPaths = new List<string>();
            var aDoc = new XmlDocument();
            aDoc.Load(xmlPath);
            foreach (XmlNode aNode in aDoc.DocumentElement!.SelectNodes("Item")!) {
                string? aPath = ((XmlElement)aNode).GetAttribute("path");
                if (!string.IsNullOrEmpty(aPath))
                    aDesiredPaths.Add(aPath);
            }

            if (aDesiredPaths.Count == 0) {
                Console.WriteLine("QuickAccess: Snapshot is empty.");
                return 0;
            }

            aShell = CreateShell();
            List<string> aCurrentPaths = GetPinnedItems(aShell);
            bool aChanged = false;

            // Fetch the Quick Access namespace once — reused by the unpin
            // loop below. The Items() enumeration inside is cheap.
            dynamic aQuickAccess = aShell.NameSpace(QUICK_ACCESS_GUID);

            // Unpin items not in the snapshot
            foreach (string aCurrent in aCurrentPaths) {
                bool aInSnapshot = aDesiredPaths.Any(d => PathsEqual(d, aCurrent));

                if (!aInSnapshot) {
                    try {
                        foreach (dynamic aItem in aQuickAccess.Items()) {
                            if (PathsEqual((string)aItem.Path, aCurrent)) {
                                aItem.InvokeVerb("unpinfromhome");
                                Console.WriteLine($"QuickAccess: Unpinning {aCurrent}");
                                aChanged = true;
                                break;
                            }
                        }
                    }
                    catch { /* best effort */ }
                }
            }

            // Pin items from the snapshot that aren't currently pinned
            // Re-read current state after unpins
            aCurrentPaths = GetPinnedItems(aShell);
            foreach (string aDesired in aDesiredPaths) {
                bool aAlreadyPinned = aCurrentPaths.Any(c => PathsEqual(c, aDesired));

                if (aAlreadyPinned) {
                    Console.WriteLine($"QuickAccess: Already pinned {aDesired}");
                    continue;
                }

                try {
                    dynamic aFolder = aShell.NameSpace(aDesired);
                    if (aFolder == null) {
                        Console.Error.WriteLine($"QuickAccess: ERROR: Could not access folder '{aDesired}'.");
                        continue;
                    }
                    aFolder.Self.InvokeVerb("pintohome");
                    Console.WriteLine($"QuickAccess: Pinning {aDesired}");
                    aChanged = true;
                }
                catch (Exception x) {
                    Console.Error.WriteLine($"QuickAccess: ERROR: {x.Message}");
                }
            }

            if (aChanged && restart) {
                Console.WriteLine("QuickAccess: Restarting Explorer");
                ExplorerCommands.Restart();
            }
            else if (aChanged)
                Console.WriteLine("QuickAccess: Changes applied. Restart Explorer to refresh.");
            else
                Console.WriteLine("QuickAccess: No changes needed.");

            return 0;
        }
        catch (Exception x)
        {
            Console.Error.WriteLine($"QuickAccess: ERROR: {x.Message}");
            return 2;
        }
        finally {
            ReleaseCom(aShell);
        }

    }


    /// <summary>
    /// Returns all paths currently pinned to Quick Access.
    /// </summary>
    public static List<string> GetPinnedItems()
    {
        dynamic? aShell = null;
        try {
            aShell = CreateShell();
            return GetPinnedItems(aShell);
        }
        catch {
            return new List<string>();
        }
        finally {
            ReleaseCom(aShell);
        }
    }


    /// <summary>
    /// Returns all paths currently pinned to Quick Access using a caller-
    /// provided Shell.Application instance. The caller owns the COM object
    /// lifetime.
    /// </summary>
    private static List<string> GetPinnedItems(dynamic shell)
    {

        var aItems = new List<string>();
        try {
            dynamic aQuickAccess = shell.NameSpace(QUICK_ACCESS_GUID);
            if (aQuickAccess == null)
                return aItems;

            foreach (dynamic aItem in aQuickAccess.Items())
                aItems.Add((string)aItem.Path);
        }
        catch {
            // Silently return empty if COM fails
        }

        return aItems;

    }


}
