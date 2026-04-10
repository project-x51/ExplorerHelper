#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

using ExplorerHelper.Interop;

namespace ExplorerHelper.Commands;


/// <summary>
/// Handles restarting Windows Explorer and cleaning up layout XML.
/// Single source of truth for all Explorer restart logic.
/// </summary>
public static class ExplorerCommands
{
    // Maximum time to wait for explorer processes to exit
    private const int EXIT_TIMEOUT_MS = 5000;

    // Delay after kill before relaunch to let the shell fully tear down
    private const int RELAUNCH_DELAY_MS = 2000;

    // Maximum seconds to poll for Explorer to process layout changes
    private const int POLL_TIMEOUT_SECONDS = 30;

    // Path to the LayoutModification.xml that pending pins write to
    private static readonly string LAYOUT_MOD_PATH = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        @"Microsoft\Windows\Shell\LayoutModification.xml");


    /// <summary>
    /// Restarts Explorer. If enabled is false, skips entirely.
    /// </summary>
    public static int Restart(bool enabled = true)
    {

        if (!enabled) {
            Console.WriteLine("Explorer: Restart skipped.");
            return 0;
        }

        try
        {
            KillExplorer();
            LaunchExplorer();
            CleanupLayoutXml();

            Console.WriteLine("Explorer: Restarted.");
            return 0;
        }
        catch (Exception x)
        {
            Console.Error.WriteLine($"Explorer: ERROR: {x.Message}");
            return 2;
        }

    }


    /// <summary>
    /// Restarts Explorer and waits for pinned items to appear before
    /// cleaning up the layout XML. Used by taskbar apply operations.
    /// </summary>
    public static void RestartAndWaitForPins()
    {

        KillExplorer();
        LaunchExplorer();

        // Poll until Explorer has processed the layout and pins appear
        try {
            using var aPinnedList = new PinnedList3();
            for (int i = 0; i < POLL_TIMEOUT_SECONDS; i++) {
                Thread.Sleep(1000);
                try {
                    var aItems = aPinnedList.GetOrderedItems();
                    if (aItems.Count > 0)
                        break;
                }
                catch { /* Explorer not ready yet */ }
            }
        }
        catch { /* PinnedList3 COM init failed — fall back to fixed delay */
            Thread.Sleep(5000);
        }

        // Give Explorer a moment to settle after the pins appear — deleting
        // the XML the instant we detect them seems to make the taskbar take
        // much longer to come back.
        Thread.Sleep(1000);

        CleanupLayoutXml();

    }


    /// <summary>
    /// Kills all explorer.exe processes and waits for them to exit.
    /// </summary>
    private static void KillExplorer()
    {

        Process[] aProcesses = Process.GetProcessesByName("explorer");
        if (aProcesses.Length == 0)
            return;

        foreach (Process aProc in aProcesses) {
            try { aProc.Kill(); } catch { /* already exited */ }
        }

        foreach (Process aProc in aProcesses) {
            try { aProc.WaitForExit(EXIT_TIMEOUT_MS); } catch { }
            finally { aProc.Dispose(); }
        }

        Thread.Sleep(RELAUNCH_DELAY_MS);

    }


    /// <summary>
    /// Launches a new explorer.exe instance.
    /// </summary>
    private static void LaunchExplorer()
    {

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true
        });

    }


    /// <summary>
    /// Deletes the LayoutModification.xml if it exists. Retries up to
    /// 5 times in case Explorer still has the file locked.
    /// </summary>
    public static void CleanupLayoutXml()
    {

        if (!File.Exists(LAYOUT_MOD_PATH))
            return;

        for (int i = 0; i < 5; i++) {
            try {
                File.Delete(LAYOUT_MOD_PATH);
                return;
            }
            catch { Thread.Sleep(1000); }
        }

        if (File.Exists(LAYOUT_MOD_PATH))
            Console.Error.WriteLine("Explorer: WARNING: Could not delete LayoutModification.xml. Delete manually to avoid reapply on reboot.");

    }
}
