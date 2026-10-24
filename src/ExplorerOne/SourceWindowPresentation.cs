using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ExplorerOne.Core;

namespace ExplorerOne;

/// <summary>Temporarily minimizes only new extra windows, retaining a reversible ownership token.</summary>
internal sealed class SourceWindowPresentation : IDisposable
{
    private static readonly TimeSpan PresentationTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan InitialShowGrace = TimeSpan.FromMilliseconds(200);
    private readonly ISourceWindowPresentationNative _native;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<WindowId, Entry> _seen = [];
    private WindowId? _mainWindow;
    private bool _disposed;

    public SourceWindowPresentation(ISourceWindowPresentationNative? native = null, TimeProvider? clock = null)
    {
        _native = native ?? new SourceWindowPresentationNative();
        _clock = clock ?? TimeProvider.System;
        // Only already visible windows belong to the startup baseline. Explorer
        // can reuse a hidden preloaded frame for a genuinely new folder request.
        foreach (var window in _native.GetExistingWindows())
            if (_native.IsVisible(window)) _seen.TryAdd(window, new Entry());
    }

    public TimeSpan? NextExpiryDelay
    {
        get { lock (_gate) return NextExpiryDelayCore; }
    }

    private TimeSpan? NextExpiryDelayCore
    {
        get
        {
            TimeSpan? earliest = null;
            foreach (var entry in _seen.Values)
            {
                if (!entry.OwnsMinimize) continue;
                var remaining = PresentationTimeout - Age(entry);
                remaining = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
                if (!earliest.HasValue || remaining < earliest.Value)
                    earliest = remaining;
            }
            return earliest;
        }
    }

    public void OnShown(WindowId source, WindowId? main)
    {
        lock (_gate)
        {
            _mainWindow = main;
            OnShownCore(source, main);
        }
    }

    // The native watcher never reads the coordinator or crosses into COM.
    public void OnShown(WindowId source)
    {
        lock (_gate) OnShownCore(source, _mainWindow);
    }

    private void OnShownCore(WindowId source, WindowId? main)
    {
        if (_disposed) return;
        Observe(main);
        if (_seen.ContainsKey(source) || !_native.IsValid(source)) return;
        // A newly allocated Explorer frame may first appear iconic while its
        // shell view initializes. Its first normal show is still a new request.
        // Already minimized startup windows are protected by the baseline above.
        if (!_native.IsVisible(source) || _native.IsMinimized(source)) return;

        // A later restore of a window managed here must never re-minimize it.
        var entry = new Entry();
        _seen.Add(source, entry);
        if (!main.HasValue || source == main.Value || !_native.IsValid(main.Value) ||
            !_native.IsVisible(main.Value) || !_native.IsVisible(source) || _native.IsMinimized(source))
            return;

        // Prefer the main window before minimizing the foreground source, so
        // Windows can keep Explorer active rather than select another app.
        _native.Activate(main.Value);
        // Publish the token before user32 can dispatch another SHOW callback.
        entry.MinimizedAt = _clock.GetTimestamp();
        entry.OwnsMinimize = true;
        if (!_native.Minimize(source))
        {
            entry.OwnsMinimize = false;
            return;
        }
        entry.ObservedMinimized |= _native.IsMinimized(source);
    }

    public void Observe(WindowId? main)
    {
        lock (_gate)
        {
            _mainWindow = main;
            ObserveCore(main);
        }
    }

    public void ObserveCurrentMain()
    {
        lock (_gate) ObserveCore(_mainWindow);
    }

    private void ObserveCore(WindowId? main)
    {
        if (_disposed) return;
        var validMain = main.HasValue && _native.IsValid(main.Value);
        foreach (var (window, entry) in _seen.ToArray())
        {
            if (!_native.IsValid(window))
            {
                _seen.Remove(window);
                continue;
            }
            if (!entry.OwnsMinimize) continue;

            var minimized = _native.IsMinimized(window);
            entry.ObservedMinimized |= minimized;

            if (!validMain || window == main || Age(entry) >= PresentationTimeout)
                RestoreOwned(window, entry);
            else if (entry.ObservedMinimized && Age(entry) >= InitialShowGrace && !minimized)
                entry.OwnsMinimize = false; // A user restore relinquishes ownership.
        }
    }

    public void OnTransfer(WindowId source, TransferResult result)
    {
        lock (_gate) OnTransferCore(source, result);
    }

    private void OnTransferCore(WindowId source, TransferResult result)
    {
        if (_disposed || !_seen.TryGetValue(source, out var entry)) return;
        if (result is TransferResult.Completed or TransferResult.SourceGone)
            _seen.Remove(source);
        else if (result is not (TransferResult.NotReady or TransferResult.Queued))
            RestoreOwned(source, entry);
    }

    public void RestoreAll()
    {
        lock (_gate) RestoreAllCore();
    }

    private void RestoreAllCore()
    {
        foreach (var (window, entry) in _seen.ToArray())
            RestoreOwned(window, entry);
    }

    private TimeSpan Age(Entry entry) => _clock.GetElapsedTime(entry.MinimizedAt, _clock.GetTimestamp());

    private void RestoreOwned(WindowId window, Entry entry)
    {
        if (!entry.OwnsMinimize) return;
        entry.OwnsMinimize = false;
        // ShowWindowAsync may still be queued on Explorer's busy thread. Queue
        // its inverse too rather than abandon an accepted, pending minimize.
        if (_native.IsValid(window) && (!entry.ObservedMinimized || _native.IsMinimized(window)))
            _native.Restore(window);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            RestoreAllCore();
            _seen.Clear();
        }
    }

    private sealed class Entry
    {
        public bool OwnsMinimize { get; set; }
        public bool ObservedMinimized { get; set; }
        public long MinimizedAt { get; set; }
    }
}

internal interface ISourceWindowPresentationNative
{
    IReadOnlyList<WindowId> GetExistingWindows();
    bool IsValid(WindowId window);
    bool IsVisible(WindowId window);
    bool IsMinimized(WindowId window);
    bool Minimize(WindowId window);
    void Restore(WindowId window);
    void Activate(WindowId window);
}

internal sealed class SourceWindowPresentationNative : ISourceWindowPresentationNative
{
    private const int SwForceMinimize = 11;
    private const int SwRestore = 9;
    private const int SwShow = 5;

    public IReadOnlyList<WindowId> GetExistingWindows()
    {
        var windows = new List<WindowId>();
        try
        {
            EnumWindows((handle, _) =>
            {
                GetWindowThreadProcessId(handle, out var processId);
                var window = new WindowId(handle, processId);
                if (IsValid(window)) windows.Add(window);
                return true;
            }, 0);
        }
        catch { }
        return windows;
    }

    public bool IsValid(WindowId window)
    {
        try
        {
            if (window.Handle == 0 || window.ProcessId == 0 || !IsWindow(window.Handle) ||
                GetWindowThreadProcessId(window.Handle, out var processId) == 0 || processId != window.ProcessId)
                return false;

            var className = new StringBuilder(64);
            if (GetClassName(window.Handle, className, className.Capacity) == 0 ||
                !string.Equals(className.ToString(), "CabinetWClass", StringComparison.Ordinal))
                return false;

            using var process = Process.GetProcessById(checked((int)processId));
            return string.Equals(process.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public bool IsVisible(WindowId window) => IsValid(window) && IsWindowVisible(window.Handle);
    public bool IsMinimized(WindowId window) => IsValid(window) && IsIconic(window.Handle);
    public bool Minimize(WindowId window)
    {
        if (!IsValid(window)) return false;
        // The watcher belongs to another thread/process. Do not wait for the
        // initializing Explorer thread to consume a queued minimize request.
        // ShowWindow's return value is previous visibility, not success.
        _ = ShowWindow(window.Handle, SwForceMinimize);
        return IsValid(window) && IsIconic(window.Handle);
    }

    public void Restore(WindowId window)
    {
        if (IsValid(window)) ShowWindowAsync(window.Handle, SwRestore);
    }

    public void Activate(WindowId window)
    {
        if (!IsVisible(window)) return;
        // Ask Explorer's owning thread to activate as well. SW_SHOW preserves
        // an already maximized main; SW_RESTORE is needed only when minimized.
        var command = IsMinimized(window) ? SwRestore : SwShow;
        if (IsValid(window)) ShowWindowAsync(window.Handle, command);
        if (IsValid(window) && GetForegroundWindow() != window.Handle)
            SetForegroundWindow(window.Handle);
    }

    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder className, int capacity);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
