#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace ExplorerHelper.Interop;


/// <summary>
/// Wraps the undocumented IPinnedList3 COM interface that Windows 11
/// uses for taskbar pinning. Both Firefox and Edge rely on this
/// interface for their "pin to taskbar" features.
/// </summary>
public class PinnedList3 : IDisposable
{
    // CLSID for the TaskbandPin COM class
    private static readonly Guid CLSID_TASKBAND_PIN = new("90AA3A4E-1CBA-4233-B8BB-535773D48449");

    // IID for the IPinnedList3 interface
    private static readonly Guid IID_IPINNED_LIST3 = new("0DD79AE2-D156-45D4-9EEB-3B549769E940");

    // EnumObjects sits at vtable slot 3 (immediately after IUnknown)
    private const int ENUM_OBJECTS_VTABLE_SLOT = 3;

    // Modify sits at vtable slot 16 (3 IUnknown + 13 interface methods)
    private const int MODIFY_VTABLE_SLOT = 16;

    // Caller value used by Firefox when invoking Modify
    private const int CALLER = int.MaxValue;

    // IEnumIDList vtable slots (after IUnknown)
    private const int ENUM_NEXT_VTABLE_SLOT = 3;

    // Pointer to the raw COM object
    private IntPtr _ComObject;

    // Cached delegate for the Modify method at vtable slot 16
    private readonly ModifyDelegate _Modify;

    // Cached delegate for the EnumObjects method at vtable slot 3
    private readonly EnumObjectsDelegate _EnumObjects;

    // Tracks whether Dispose has been called
    private bool _Disposed;


    /// <summary>
    /// Delegate matching the EnumObjects method at vtable slot 3.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumObjectsDelegate(IntPtr thisPtr, out IntPtr enumIdList);

    /// <summary>
    /// Delegate matching IEnumIDList::Next at vtable slot 3.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumNextDelegate(IntPtr thisPtr, uint celt, out IntPtr pidl, out uint fetched);

    /// <summary>
    /// Delegate matching the Modify method signature in the IPinnedList3 vtable.
    /// </summary>
    /// <param name="thisPtr">The COM object pointer (this).</param>
    /// <param name="unpin">PIDL of the item to unpin, or IntPtr.Zero to skip.</param>
    /// <param name="pin">PIDL of the item to pin, or IntPtr.Zero to skip.</param>
    /// <param name="caller">Caller identifier, use int.MaxValue.</param>
    /// <returns>HRESULT indicating success or failure.</returns>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ModifyDelegate(IntPtr thisPtr, IntPtr unpin, IntPtr pin, int caller);


    public PinnedList3()
    {

        // Create the TaskbandPin COM object and get the IPinnedList3 interface
        Guid aClsid = CLSID_TASKBAND_PIN;
        Guid aIid = IID_IPINNED_LIST3;
        int aHr = NativeMethods.CoCreateInstance(
            ref aClsid,
            IntPtr.Zero,
            NativeMethods.CLSCTX_ALL,
            ref aIid,
            out _ComObject);

        if (aHr != 0 || _ComObject == IntPtr.Zero)
            throw new COMException($"Failed to create IPinnedList3 COM object (HRESULT: 0x{aHr:X8})");

        // Walk the vtable to get function pointers
        IntPtr aVtable = Marshal.ReadIntPtr(_ComObject);
        IntPtr aModifyPtr = Marshal.ReadIntPtr(aVtable, MODIFY_VTABLE_SLOT * IntPtr.Size);
        _Modify = Marshal.GetDelegateForFunctionPointer<ModifyDelegate>(aModifyPtr);

        IntPtr aEnumPtr = Marshal.ReadIntPtr(aVtable, ENUM_OBJECTS_VTABLE_SLOT * IntPtr.Size);
        _EnumObjects = Marshal.GetDelegateForFunctionPointer<EnumObjectsDelegate>(aEnumPtr);

    }


    /// <summary>
    /// Pins an item to the taskbar. The PIDL must point to a .lnk shortcut.
    /// </summary>
    /// <param name="pidl">PIDL of the shortcut to pin.</param>
    /// <returns>HRESULT indicating success or failure.</returns>
    public int Pin(IntPtr pidl)
    {
        return _Modify(_ComObject, IntPtr.Zero, pidl, CALLER);
    }


    /// <summary>
    /// Unpins an item from the taskbar. The PIDL must point to a .lnk shortcut.
    /// </summary>
    /// <param name="pidl">PIDL of the shortcut to unpin.</param>
    /// <returns>HRESULT indicating success or failure.</returns>
    public int Unpin(IntPtr pidl)
    {
        return _Modify(_ComObject, pidl, IntPtr.Zero, CALLER);
    }


    /// <summary>
    /// Represents a single pinned taskbar item with its identifying
    /// information for both display and LayoutModification.xml generation.
    /// </summary>
    public record PinnedItem(
        string DisplayName,
        string? LnkPath,
        string? AppUserModelId);


    /// <summary>
    /// Enumerates pinned taskbar items in display order using the
    /// IPinnedList3::EnumObjects method. Returns structured data for
    /// each item including file path, AUMID, and display name.
    /// </summary>
    /// <returns>Ordered list of pinned items.</returns>
    public List<PinnedItem> GetOrderedItems()
    {

        var aResults = new List<PinnedItem>();

        int aHr = _EnumObjects(_ComObject, out IntPtr aEnumPtr);
        if (aHr != 0 || aEnumPtr == IntPtr.Zero)
            return aResults;

        try
        {
            IntPtr aEnumVtable = Marshal.ReadIntPtr(aEnumPtr);
            IntPtr aNextPtr = Marshal.ReadIntPtr(aEnumVtable, ENUM_NEXT_VTABLE_SLOT * IntPtr.Size);
            var aNext = Marshal.GetDelegateForFunctionPointer<EnumNextDelegate>(aNextPtr);

            while (true) {
                aHr = aNext(aEnumPtr, 1, out IntPtr aPidl, out uint aFetched);
                if (aHr != 0 || aFetched == 0 || aPidl == IntPtr.Zero)
                    break;

                try {
                    string? aLnkPath = GetNameFromPidl(aPidl, NativeMethods.SIGDN_FILESYSPATH);
                    string? aParseName = GetNameFromPidl(aPidl, NativeMethods.SIGDN_DESKTOPABSOLUTEPARSING);
                    string? aDisplayName = GetNameFromPidl(aPidl, NativeMethods.SIGDN_NORMALDISPLAY);

                    // Desktop apps have a .lnk file path; UWP/Store apps have an AUMID
                    bool aIsDesktop = aLnkPath != null && aLnkPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
                    string? aAumid = !aIsDesktop ? aParseName : null;

                    string aName = aDisplayName
                        ?? (aLnkPath != null ? Path.GetFileNameWithoutExtension(aLnkPath) : null)
                        ?? aParseName
                        ?? "Unknown";

                    aResults.Add(new PinnedItem(aName, aIsDesktop ? aLnkPath : null, aAumid));
                }
                finally {
                    NativeMethods.CoTaskMemFree(aPidl);
                }
            }
        }
        finally
        {
            Marshal.Release(aEnumPtr);
        }

        return aResults;

    }


    /// <summary>
    /// Extended pinned item that retains a copied PIDL for deferred
    /// operations like UnpinAll. The PIDL is owned by this record and
    /// must be freed via CoTaskMemFree when no longer needed.
    /// </summary>
    public record PinnedItemWithPidl(
        string DisplayName,
        string? LnkPath,
        string? AppUserModelId,
        IntPtr Pidl) : PinnedItem(DisplayName, LnkPath, AppUserModelId);


    /// <summary>
    /// Enumerates pinned taskbar items in display order, returning each
    /// item with a copied PIDL that remains valid after enumeration.
    /// The caller must eventually pass the list to <see cref="UnpinAll"/>
    /// or free each PIDL manually via CoTaskMemFree.
    /// </summary>
    /// <returns>Ordered list of pinned items with owned PIDLs.</returns>
    public List<PinnedItemWithPidl> GetOrderedItemsWithPidls()
    {

        var aResults = new List<PinnedItemWithPidl>();

        int aHr = _EnumObjects(_ComObject, out IntPtr aEnumPtr);
        if (aHr != 0 || aEnumPtr == IntPtr.Zero)
            return aResults;

        try
        {
            IntPtr aEnumVtable = Marshal.ReadIntPtr(aEnumPtr);
            IntPtr aNextPtr = Marshal.ReadIntPtr(aEnumVtable, ENUM_NEXT_VTABLE_SLOT * IntPtr.Size);
            var aNext = Marshal.GetDelegateForFunctionPointer<EnumNextDelegate>(aNextPtr);

            while (true) {
                aHr = aNext(aEnumPtr, 1, out IntPtr aPidl, out uint aFetched);
                if (aHr != 0 || aFetched == 0 || aPidl == IntPtr.Zero)
                    break;

                // Copy the PIDL so it survives after enumeration
                IntPtr aCopiedPidl = CopyPidl(aPidl);

                try {
                    string? aLnkPath = GetNameFromPidl(aPidl, NativeMethods.SIGDN_FILESYSPATH);
                    string? aParseName = GetNameFromPidl(aPidl, NativeMethods.SIGDN_DESKTOPABSOLUTEPARSING);
                    string? aDisplayName = GetNameFromPidl(aPidl, NativeMethods.SIGDN_NORMALDISPLAY);

                    bool aIsDesktop = aLnkPath != null && aLnkPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
                    string? aAumid = !aIsDesktop ? aParseName : null;

                    string aName = aDisplayName
                        ?? (aLnkPath != null ? Path.GetFileNameWithoutExtension(aLnkPath) : null)
                        ?? aParseName
                        ?? "Unknown";

                    aResults.Add(new PinnedItemWithPidl(aName, aIsDesktop ? aLnkPath : null, aAumid, aCopiedPidl));
                }
                finally {
                    NativeMethods.CoTaskMemFree(aPidl);
                }
            }
        }
        finally
        {
            Marshal.Release(aEnumPtr);
        }

        return aResults;

    }


    /// <summary>
    /// Unpins all items in the list using their stored PIDLs, then frees
    /// every PIDL. After this call the PIDL pointers in the list are invalid.
    /// </summary>
    /// <param name="items">Items previously returned by GetOrderedItemsWithPidls.</param>
    public void UnpinAll(List<PinnedItemWithPidl> items)
    {

        foreach (var aItem in items) {
            if (aItem.Pidl != IntPtr.Zero) {
                Unpin(aItem.Pidl);
                NativeMethods.CoTaskMemFree(aItem.Pidl);
            }
        }

    }


    /// <summary>
    /// Copies a PIDL into newly allocated COM task memory so it can be
    /// used after the original is freed.
    /// </summary>
    private static IntPtr CopyPidl(IntPtr pidl)
    {

        uint aSize = NativeMethods.ILGetSize(pidl);
        IntPtr aCopy = NativeMethods.CoTaskMemAlloc(aSize);
        if (aCopy == IntPtr.Zero)
            return IntPtr.Zero;

        unsafe {
            Buffer.MemoryCopy(pidl.ToPointer(), aCopy.ToPointer(), aSize, aSize);
        }

        return aCopy;

    }


    /// <summary>
    /// Helper to get a name string from a PIDL using the specified format.
    /// </summary>
    private static string? GetNameFromPidl(IntPtr pidl, uint sigdn)
    {

        int aHr = NativeMethods.SHGetNameFromIDList(pidl, sigdn, out IntPtr aNamePtr);
        if (aHr != 0 || aNamePtr == IntPtr.Zero)
            return null;

        string? aResult = Marshal.PtrToStringUni(aNamePtr);
        NativeMethods.CoTaskMemFree(aNamePtr);
        return aResult;

    }


    public void Dispose()
    {

        if (!_Disposed && _ComObject != IntPtr.Zero) {
            Marshal.Release(_ComObject);
            _ComObject = IntPtr.Zero;
            _Disposed = true;
        }

        GC.SuppressFinalize(this);

    }

    ~PinnedList3() => this.Dispose();
}
