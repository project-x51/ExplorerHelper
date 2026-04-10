#nullable enable

using System;
using System.Runtime.InteropServices;

namespace ExplorerHelper.Interop;


/// <summary>
/// Provides P/Invoke declarations for Win32 shell and COM functions
/// used by the taskbar and Quick Access commands.
/// </summary>
public static class NativeMethods
{
    // CLSCTX_INPROC_SERVER | CLSCTX_INPROC_HANDLER | CLSCTX_LOCAL_SERVER
    public const uint CLSCTX_ALL = 0x17;


    /// <summary>
    /// Converts a file system path to a PIDL (pointer to an item identifier list)
    /// used by shell COM interfaces to identify files and folders.
    /// </summary>
    /// <param name="pszName">The file system path to parse.</param>
    /// <param name="pbc">Optional bind context, typically IntPtr.Zero.</param>
    /// <param name="ppidl">Receives the PIDL for the parsed path.</param>
    /// <param name="sfgaoIn">Requested SFGAO attributes.</param>
    /// <param name="psfgaoOut">Receives the actual SFGAO attributes.</param>
    /// <returns>HRESULT indicating success or failure.</returns>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHParseDisplayName(
        string pszName,
        IntPtr pbc,
        out IntPtr ppidl,
        uint sfgaoIn,
        out uint psfgaoOut);


    /// <summary>
    /// Frees a block of COM task memory, used to release PIDLs returned
    /// by shell functions.
    /// </summary>
    /// <param name="pv">Pointer to the memory block to free.</param>
    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(IntPtr pv);


    /// <summary>
    /// Retrieves the display name of an item identified by its PIDL.
    /// </summary>
    /// <param name="pidl">The PIDL of the item.</param>
    /// <param name="sigdnName">The type of display name to retrieve.</param>
    /// <param name="ppszName">Receives the display name string.</param>
    /// <returns>HRESULT indicating success or failure.</returns>
    [DllImport("shell32.dll")]
    public static extern int SHGetNameFromIDList(
        IntPtr pidl,
        uint sigdnName,
        out IntPtr ppszName);

    // SIGDN_NORMALDISPLAY = 0x00000000
    public const uint SIGDN_NORMALDISPLAY = 0x00000000;

    // SIGDN_FILESYSPATH = 0x80058000 — returns the full file system path
    public const uint SIGDN_FILESYSPATH = 0x80058000;

    // SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000 — returns a parseable path
    public const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;


    /// <summary>
    /// Returns the byte size of a PIDL (item ID list) by walking its
    /// SHITEMID chain. Used to copy PIDLs for deferred use.
    /// </summary>
    /// <param name="pidl">Pointer to the PIDL.</param>
    /// <returns>Total size in bytes including the terminating zero WORD.</returns>
    [DllImport("shell32.dll")]
    public static extern uint ILGetSize(IntPtr pidl);

    /// <summary>
    /// Allocates a block of COM task memory. Used to allocate space for
    /// PIDL copies that can later be freed with CoTaskMemFree.
    /// </summary>
    /// <param name="cb">Number of bytes to allocate.</param>
    /// <returns>Pointer to the allocated memory, or IntPtr.Zero on failure.</returns>
    [DllImport("ole32.dll")]
    public static extern IntPtr CoTaskMemAlloc(uint cb);


    /// <summary>
    /// Creates a single uninitialized COM object of the specified class.
    /// Used to instantiate the IPinnedList3 taskbar pinning interface.
    /// </summary>
    /// <param name="rclsid">The CLSID of the COM class to create.</param>
    /// <param name="pUnkOuter">Outer unknown for aggregation, typically IntPtr.Zero.</param>
    /// <param name="dwClsContext">Context in which the COM object will run.</param>
    /// <param name="riid">The IID of the interface to retrieve.</param>
    /// <param name="ppv">Receives the interface pointer.</param>
    /// <returns>HRESULT indicating success or failure.</returns>
    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        ref Guid riid,
        out IntPtr ppv);
}
