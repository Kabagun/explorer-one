// Adapted from ExplorerTabUtility (w4po, copyright 2025), MIT, commit fad7363.
// Sources: Interop/{IServiceProvider,IShellBrowser,IShellFolder,ShellPathComparer}.cs
// and WinAPI/WinApi.cs in the upstream ExplorerTabUtility project.
// The upstream license is reproduced in THIRD-PARTY-NOTICES.md.
using System.Runtime.InteropServices;
using System.Text;

namespace ExplorerOne.Shell;

[ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellServiceProvider
{
    [PreserveSig]
    int QueryService(ref Guid service, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellBrowser? browser);
}

[ComImport, Guid("000214E2-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellBrowser
{
    [PreserveSig]
    int GetWindow(out nint handle);
}

[ComImport, Guid("000214E6-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellFolder
{
    [PreserveSig]
    int ParseDisplayName(nint hwnd, nint bindContext, [MarshalAs(UnmanagedType.LPWStr)] string displayName,
        out uint eaten, out nint pidl, ref uint attributes);

    void _VtblGap1_3(); // EnumObjects, BindToObject, BindToStorage.

    [PreserveSig]
    int CompareIDs(nint flags, nint first, nint second);
}

internal static class ShellNative
{
    internal const uint WmCommand = 0x0111;
    internal const uint WmClose = 0x0010;
    internal const nuint NewTabCommand = 0xA21B;
    internal const nuint CloseTabCommand = 0xA021;
    internal const uint AbortIfHung = 0x0002;
    internal const uint ErrorOnExit = 0x0020;
    internal const uint RootAncestor = 2;
    internal const uint DesktopAbsoluteParsing = 0x80028000;
    internal const uint ParentRelativeParsing = 0x80018001;

    internal delegate bool EnumWindowsCallback(nint window, nint parameter);

    // WinUser.h GUITHREADINFO; HWND fields and RECT use the native process layout.
    [StructLayout(LayoutKind.Sequential)]
    internal struct GuiThreadInfo
    {
        internal uint Size, Flags;
        internal nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        internal int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint FindWindowEx(nint parent, nint after, string className, string? title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(nint window, StringBuilder className, int capacity);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo information);

    [DllImport("user32.dll")]
    internal static extern nint GetParent(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindowAsync(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    internal static extern nint GetAncestor(nint window, uint flags);

    [DllImport("user32.dll")]
    internal static extern nint GetShellWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SendMessageTimeout(nint window, uint message, nuint wParam, nint lParam,
        uint flags, uint timeout, out nuint result);

    [DllImport("shell32.dll")]
    internal static extern int SHGetDesktopFolder([MarshalAs(UnmanagedType.Interface)] out IShellFolder desktop);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHGetNameFromIDList(nint pidl, uint nameType, out nint name);

    internal static string ClassName(nint window)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(window, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }
}

internal static class ComReference
{
    internal static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.ReleaseComObject(value);
    }
}

internal sealed class ShellPathComparer : IDisposable
{
    private IShellFolder? _desktop;

    internal ShellPathComparer()
    {
        Marshal.ThrowExceptionForHR(ShellNative.SHGetDesktopFolder(out var desktop));
        _desktop = desktop;
    }

    internal string Canonicalize(string path)
        => GetParsingName(path, ShellNative.DesktopAbsoluteParsing);

    internal string RelativeParsingName(string path)
        => GetParsingName(path, ShellNative.ParentRelativeParsing);

    private string GetParsingName(string path, uint nameType)
    {
        var pidl = Parse(path);
        nint name = 0;
        try
        {
            Marshal.ThrowExceptionForHR(ShellNative.SHGetNameFromIDList(pidl, nameType, out name));
            return Marshal.PtrToStringUni(name) is { Length: > 0 } canonical
                ? canonical : throw new UnsupportedShellViewException();
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    internal bool Equivalent(string first, string second)
    {
        if (StringComparer.Ordinal.Equals(first, second))
            return true;

        nint firstPidl = 0;
        nint secondPidl = 0;
        try
        {
            firstPidl = Parse(first);
            secondPidl = Parse(second);
            // Canonical comparison; never fall back to case-insensitive URL/path text.
            return _desktop!.CompareIDs(0x10000000, firstPidl, secondPidl) == 0;
        }
        finally
        {
            Marshal.FreeCoTaskMem(firstPidl);
            Marshal.FreeCoTaskMem(secondPidl);
        }
    }

    private nint Parse(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new UnsupportedShellViewException();

        uint attributes = 0;
        var parsingName = ShellLocation.NormalizeParsingName(path);
        var hr = _desktop!.ParseDisplayName(0, 0, parsingName, out _, out var pidl, ref attributes);
        if (hr < 0 || pidl == 0)
        {
            Marshal.FreeCoTaskMem(pidl);
            throw new UnsupportedShellViewException();
        }
        return pidl;
    }

    public void Dispose()
    {
        ComReference.Release(_desktop);
        _desktop = null;
    }
}

internal sealed class UnsupportedShellViewException : Exception
{
    internal UnsupportedShellViewException() : base("This Explorer view cannot be transferred safely.") { }
}

internal sealed class ShellSnapshotException : Exception
{
    internal ShellSnapshotException() : base("Explorer has not provided a complete stable tab snapshot.") { }
}
