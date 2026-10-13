using System.IO;
using System.Runtime.InteropServices;

namespace FrameTrail.App;

internal interface IDesktopIconShell
{
    bool Hidden { get; }
    void SetHidden(bool hidden);
}

/// <summary>Uses Explorer's public folder view API; changes only FWF_NOICONS, never files or icon positions.</summary>
internal sealed class DesktopIconShell : IDesktopIconShell
{
    private const uint NoIcons = 0x1000;
    public bool Hidden => WithView(view => { Marshal.ThrowExceptionForHR(view.GetCurrentFolderFlags(out var flags)); return (flags & NoIcons) != 0; });

    public void SetHidden(bool hidden) => WithView(view =>
    {
        Marshal.ThrowExceptionForHR(view.SetCurrentFolderFlags(NoIcons, hidden ? NoIcons : 0));
        Marshal.ThrowExceptionForHR(view.GetCurrentFolderFlags(out var flags));
        if (((flags & NoIcons) != 0) != hidden) throw new IOException("Windows 未能应用桌面图标显示状态。");
        DwmFlush();
        return true;
    });

    internal static uint ExplorerProcessId
    {
        get { GetWindowThreadProcessId(GetShellWindow(), out var id); return id; }
    }

    private static T WithView<T>(Func<IFolderView2, T> action)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw new InvalidOperationException("Desktop folder view requires STA.");
        object? windows = null, desktop = null, browser = null, view = null;
        try
        {
            // IShellWindows::FindWindowSW(SWC_DESKTOP), then SID_STopLevelBrowser / QueryActiveShellView.
            windows = Activator.CreateInstance(Type.GetTypeFromCLSID(new("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), true)!)!;
            object location = 0; // CSIDL_DESKTOP
            object empty = null!; // VT_EMPTY
            desktop = ((dynamic)windows).FindWindowSW(ref location, ref empty, 8, out int _, 1);
            if (desktop is null) throw new IOException("Windows 桌面暂时不可用。");
            var service = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
            var iid = typeof(IShellBrowser).GUID;
            Marshal.ThrowExceptionForHR(((IServiceProvider)desktop).QueryService(ref service, ref iid, out browser));
            Marshal.ThrowExceptionForHR(((IShellBrowser)browser).QueryActiveShellView(out view));
            return action((IFolderView2)view);
        }
        finally
        {
            foreach (var value in new[] { view, browser, desktop, windows })
                if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object value);
    }

    // Unused methods preserve the documented COM vtable order. Only QueryActiveShellView is invoked.
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow(); void ContextSensitiveHelp(); void InsertMenusSB(); void SetMenuSB();
        void RemoveMenusSB(); void SetStatusTextSB(); void EnableModelessSB(); void TranslateAcceleratorSB();
        void BrowseObject(); void GetViewStateStream(); void GetControlWindow(); void SendControlMsg();
        [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out object view);
    }

    // IFolderView's fourteen slots precede IFolderView2. We use only its flag getter and masked setter.
    [ComImport, Guid("1AF3A467-214F-4298-908E-06B03E0B39F9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView2
    {
        void GetCurrentViewMode(); void SetCurrentViewMode(); void GetFolder(); void Item(); void ItemCount();
        void Items(); void GetSelectionMarkedItem(); void GetFocusedItem(); void GetItemPosition();
        void GetSpacing(); void GetDefaultSpacing(); void GetAutoArrange(); void SelectItem(); void SelectAndPositionItems();
        void SetGroupBy(); void GetGroupBy(); void SetViewProperty(); void GetViewProperty();
        void SetTileViewProperties(); void SetExtendedTileViewProperties(); void SetText();
        [PreserveSig] int SetCurrentFolderFlags(uint mask, uint flags);
        [PreserveSig] int GetCurrentFolderFlags(out uint flags);
    }
}
