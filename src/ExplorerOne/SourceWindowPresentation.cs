using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using ExplorerOne.Core;

namespace ExplorerOne;

/// <summary>Suppresses only new extra frames with reversible, native-only appearance ownership.</summary>
internal sealed class SourceWindowPresentation : IDisposable
{
    private const uint Layered = 0x00080000, AlphaFlag = 2;
    private static readonly TimeSpan PresentationTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan InitialShowGrace = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan RestoreRetryDelay = TimeSpan.FromMilliseconds(100);
    private readonly ISourceWindowPresentationNative _native;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<WindowId, Entry> _seen = [];
    private readonly ConcurrentDictionary<WindowId, byte> _protected = new();
    private MainReference? _publishedMain;
    private WindowId? _mainWindow;
    private volatile bool _disposed;

    public SourceWindowPresentation(ISourceWindowPresentationNative? native = null, TimeProvider? clock = null)
    {
        _native = native ?? new SourceWindowPresentationNative();
        _clock = clock ?? TimeProvider.System;
        foreach (var window in _native.GetExistingWindows())
            if (_native.IsVisible(window) || _native.IsMinimized(window))
            { _seen.TryAdd(window, new Entry()); _protected.TryAdd(window, 0); }
    }

    public TimeSpan? NextExpiryDelay
    {
        get
        {
            lock (_gate)
            {
                TimeSpan? earliest = null;
                foreach (var entry in _seen.Values)
                {
                    if (!entry.OwnsMinimize && entry.Appearance is null) continue;
                    var remaining = entry.RestorePending ? RestoreRetryDelay - SinceRestoreAttempt(entry) : PresentationTimeout - Age(entry);
                    remaining = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
                    if (!earliest.HasValue || remaining < earliest.Value) earliest = remaining;
                }
                return earliest;
            }
        }
    }

    public void OnCreated(WindowId source)
    {
        lock (_gate) OnWindowCore(source, _mainWindow, created: true);
    }

    public bool TryGetPreopenTarget(WindowId source, bool created, out WindowId target)
    {
        // The WinEvent critical path must never wait for synchronous style
        // writes or appearance rollback running under the presentation gate.
        target = default;
        var published = Volatile.Read(ref _publishedMain);
        if (_disposed || published is null || source == published.Value || _protected.ContainsKey(source) ||
            !_native.IsValid(source) || !_native.IsValid(published.Value) || !_native.IsVisible(published.Value) ||
            !_native.IsVisible(source) || _native.IsMinimized(source)) return false;
        if (_disposed || !ReferenceEquals(published, Volatile.Read(ref _publishedMain)) || _protected.ContainsKey(source)) return false;
        target = published.Value;
        return true;
    }
    public void OnShown(WindowId source, WindowId? main)
    {
        PublishMain(main);
        lock (_gate) { _mainWindow = main; OnWindowCore(source, main, created: false); }
    }
    public void OnShown(WindowId source)
    {
        lock (_gate) OnWindowCore(source, _mainWindow, created: false);
    }

    private void OnWindowCore(WindowId source, WindowId? main, bool created)
    {
        if (_disposed) return;
        ObserveCore(main);
        if (!_native.IsValid(source)) return;
        if (_seen.TryGetValue(source, out var existing))
        {
            if (!created && existing.Appearance is not null && !existing.RestorePending &&
                _native.IsVisible(source) && !_native.IsMinimized(source) && !existing.HasNormalShow)
            {
                existing.HasNormalShow = true;
                // Explorer can finish its initial style setup after CREATE.
                // Reapply once using the ORIGINAL snapshot, never recapture ours.
                if (!ApplyTransparency(source, existing)) RestoreOwned(source, existing);
                if (main.HasValue && _native.IsValid(main.Value)) _native.Activate(main.Value);
            }
            return;
        }
        var visible = _native.IsVisible(source);
        if (!created && (!visible || _native.IsMinimized(source))) return;
        var validMain = main.HasValue && source != main.Value && _native.IsValid(main.Value) && _native.IsVisible(main.Value);
        if (!validMain)
        {
            // A skipped early CREATE remains eligible for a later SHOW.
            if (!created) { _seen.TryAdd(source, new Entry()); _protected.TryAdd(source, 0); }
            return;
        }
        // A currently visible iconic frame is not a new normal show. Invisible
        // CREATE frames can be made transparent before their first appearance.
        if (visible && _native.IsMinimized(source)) return;
        var entry = new Entry { MinimizedAt = _clock.GetTimestamp(), HasNormalShow = !created && visible };
        _seen.Add(source, entry); // Publish ownership before user32 can reenter.
        try
        {
            if (CaptureAndSuppress(source, entry))
            {
                if (visible) _native.Activate(main!.Value);
                return; // Successful alpha suppression never minimizes.
            }
            RestoreAppearance(source, entry);
            if (visible && !_native.IsMinimized(source))
            {
                entry.OwnsMinimize = true;
                if (!_native.Minimize(source)) entry.OwnsMinimize = false;
                else entry.ObservedMinimized = _native.IsMinimized(source);
                _native.Activate(main!.Value);
            }
            if (entry.Appearance is null && !entry.OwnsMinimize && (!created || visible)) _protected.TryAdd(source, 0);
            if (created && entry.Appearance is null && !entry.OwnsMinimize) _seen.Remove(source);
        }
        catch
        {
            RestoreOwned(source, entry);
            if (created && entry.Appearance is null && !entry.OwnsMinimize) _seen.Remove(source);
        }
    }

    private bool CaptureAndSuppress(WindowId window, Entry entry)
    {
        if (!_native.TryGetExtendedStyle(window, out var style)) return false;
        var attributes = new LayeredWindowAttributes(0, 255, AlphaFlag);
        if ((style & Layered) != 0 && !_native.TryGetLayeredAttributes(window, out attributes)) return false;
        entry.Appearance = new Appearance(style, attributes);
        return ApplyTransparency(window, entry);
    }
    private bool ApplyTransparency(WindowId window, Entry entry)
    {
        if (entry.Appearance is not { } original || !_native.IsValid(window) ||
            !_native.TryGetExtendedStyle(window, out var current)) return false;
        if ((current & Layered) == 0 && !_native.TrySetExtendedStyle(window, current | Layered)) return false;
        // Alpha-zero idea adapted from ExplorerTabUtility Helpers/Helper.cs,
        // HideWindow (https://github.com/w4po/ExplorerTabUtility). Unlike the
        // upstream shortcut, retain exact original attributes/style ownership.
        var hidden = new LayeredWindowAttributes(original.Attributes.ColorKey, 0,
            (original.Style & Layered) != 0 ? original.Attributes.Flags | AlphaFlag : AlphaFlag);
        return _native.TrySetLayeredAttributes(window, hidden);
    }

    public void Observe(WindowId? main)
    {
        PublishMain(main);
        lock (_gate) { _mainWindow = main; ObserveCore(main); }
    }
    public void ObserveCurrentMain()
    {
        lock (_gate) ObserveCore(_mainWindow);
    }
    private void ObserveCore(WindowId? main)
    {
        if (_disposed) return;
        var validMain = main.HasValue && _native.IsValid(main.Value) && _native.IsVisible(main.Value);
        foreach (var window in _protected.Keys)
            if (!_native.IsValid(window)) _protected.TryRemove(window, out _);
        foreach (var (window, entry) in _seen.ToArray())
        {
            if (!_native.IsValid(window)) { _seen.Remove(window); _protected.TryRemove(window, out _); continue; }
            if (!entry.OwnsMinimize && entry.Appearance is null) continue;
            if (entry.RestorePending && SinceRestoreAttempt(entry) < RestoreRetryDelay) continue;
            var minimized = _native.IsMinimized(window);
            if (entry.OwnsMinimize || entry.HasNormalShow && Age(entry) >= InitialShowGrace)
                entry.ObservedMinimized |= minimized;
            if (entry.RestorePending || !validMain || window == main || Age(entry) >= PresentationTimeout)
                RestoreOwned(window, entry);
            else if (entry.ObservedMinimized && Age(entry) >= InitialShowGrace && !minimized)
            {
                // A user restore relinquishes both kinds of presentation
                // ownership. A seen source can never be suppressed again.
                _protected.TryAdd(window, 0);
                entry.OwnsMinimize = false;
                RestoreAppearance(window, entry);
            }
        }
    }

    public void OnTransfer(WindowId source, TransferResult result)
    {
        lock (_gate)
        {
            if (_disposed || !_seen.TryGetValue(source, out var entry)) return;
            if (!_native.IsValid(source)) { _seen.Remove(source); _protected.TryRemove(source, out _); return; }
            // Queued owns captured background work, not native disappearance.
            if (result is not (TransferResult.NotReady or TransferResult.Queued))
                RestoreOwned(source, entry); // Even claimed Completed/Gone must unhide a still-live frame.
        }
    }
    public void RestoreAll()
    {
        lock (_gate) RestoreAllCore();
    }
    private void RestoreAllCore()
    {
        foreach (var (window, entry) in _seen.ToArray()) RestoreOwned(window, entry);
    }
    private TimeSpan Age(Entry entry) => _clock.GetElapsedTime(entry.MinimizedAt, _clock.GetTimestamp());
    private TimeSpan SinceRestoreAttempt(Entry entry) => _clock.GetElapsedTime(entry.LastRestoreAttempt, _clock.GetTimestamp());

    private void RestoreAppearance(WindowId window, Entry entry)
    {
        if (entry.Appearance is not { } original) return;
        if (!_native.IsValid(window)) { entry.Appearance = null; entry.RestorePending = false; return; }
        entry.RestorePending = true;
        entry.LastRestoreAttempt = _clock.GetTimestamp();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (!_native.IsValid(window)) { entry.Appearance = null; entry.RestorePending = false; return; }
                if (!_native.TryGetExtendedStyle(window, out var current)) continue;
                var restored = (original.Style & Layered) != 0
                    ? ((current & Layered) != 0 || _native.TrySetExtendedStyle(window, current | Layered)) &&
                        _native.TrySetLayeredAttributes(window, original.Attributes)
                    : (current & Layered) == 0 || _native.TrySetExtendedStyle(window, current & ~Layered);
                if (restored) { entry.Appearance = null; entry.RestorePending = false; return; }
            }
            catch { /* Keep the original token and try the bounded safety path. */ }
        }
        // Avoid leaving a valid frame alpha-zero if exact rollback temporarily
        // fails. Preserve every unrelated current style bit, retain the token,
        // and retry exact restoration on the next native presentation pulse.
        try
        {
            if (_native.IsValid(window) && _native.TryGetExtendedStyle(window, out var current))
                _native.TrySetExtendedStyle(window, current & ~Layered);
        }
        catch { }
    }
    private void RestoreOwned(WindowId window, Entry entry)
    {
        _protected.TryAdd(window, 0); // Publish before potentially blocking native rollback.
        RestoreAppearance(window, entry);
        if (entry.OwnsMinimize)
        {
            entry.OwnsMinimize = false;
            if (_native.IsValid(window) && (!entry.ObservedMinimized || _native.IsMinimized(window))) _native.Restore(window);
        }
        // An invisible standby CREATE is not a user-restored window. Once its
        // original appearance is back, leave its eventual first SHOW eligible.
        if (entry.Appearance is null && !entry.RestorePending && !entry.HasNormalShow &&
            _native.IsValid(window) && !_native.IsVisible(window) && !_native.IsMinimized(window))
        { _seen.Remove(window); _protected.TryRemove(window, out _); }
    }
    private void PublishMain(WindowId? main)
    {
        var current = Volatile.Read(ref _publishedMain);
        if (main.HasValue ? current?.Value == main.Value : current is null) return;
        Volatile.Write(ref _publishedMain, main.HasValue ? new MainReference(main.Value) : null);
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Volatile.Write(ref _publishedMain, null);
            RestoreAllCore();
            // A second bounded pass resolves transient rollback failures. Keep
            // any still-failed original token for an explicit RestoreAll retry.
            RestoreAllCore();
            foreach (var key in _seen.Where(pair => pair.Value.Appearance is null && !pair.Value.OwnsMinimize).Select(pair => pair.Key).ToArray())
                _seen.Remove(key);
        }
    }
    private sealed record Appearance(uint Style, LayeredWindowAttributes Attributes);
    private sealed record MainReference(WindowId Value);
    private sealed class Entry
    {
        public Appearance? Appearance { get; set; }
        public bool OwnsMinimize { get; set; }
        public bool ObservedMinimized { get; set; }
        public bool HasNormalShow { get; set; }
        public bool RestorePending { get; set; }
        public long MinimizedAt { get; set; }
        public long LastRestoreAttempt { get; set; }
    }
}

internal readonly record struct LayeredWindowAttributes(uint ColorKey, byte Alpha, uint Flags);

internal interface ISourceWindowPresentationNative
{
    IReadOnlyList<WindowId> GetExistingWindows();
    bool IsValid(WindowId window);
    bool IsVisible(WindowId window);
    bool IsMinimized(WindowId window);
    bool TryGetExtendedStyle(WindowId window, out uint style);
    bool TrySetExtendedStyle(WindowId window, uint style);
    bool TryGetLayeredAttributes(WindowId window, out LayeredWindowAttributes attributes);
    bool TrySetLayeredAttributes(WindowId window, LayeredWindowAttributes attributes);
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
    public bool TryGetExtendedStyle(WindowId window, out uint style)
    {
        style = 0;
        if (!IsValid(window)) return false;
        Marshal.SetLastPInvokeError(0);
        var value = nint.Size == 8 ? GetWindowLongPtr(window.Handle, -20) : new nint(GetWindowLong(window.Handle, -20));
        if (Marshal.GetLastPInvokeError() != 0 || !IsValid(window)) return false;
        style = unchecked((uint)value.ToInt64());
        return true;
    }
    public bool TrySetExtendedStyle(WindowId window, uint style)
    {
        if (!IsValid(window)) return false;
        Marshal.SetLastPInvokeError(0);
        if (nint.Size == 8) _ = SetWindowLongPtr(window.Handle, -20, new nint(unchecked((long)style)));
        else _ = SetWindowLong(window.Handle, -20, unchecked((int)style));
        return Marshal.GetLastPInvokeError() == 0 && IsValid(window);
    }
    public bool TryGetLayeredAttributes(WindowId window, out LayeredWindowAttributes attributes)
    {
        attributes = default;
        if (!IsValid(window) || !GetLayeredWindowAttributes(window.Handle, out var colorKey, out var alpha, out var flags) || !IsValid(window)) return false;
        attributes = new LayeredWindowAttributes(colorKey, alpha, flags);
        return true;
    }
    public bool TrySetLayeredAttributes(WindowId window, LayeredWindowAttributes attributes) => IsValid(window) &&
        SetLayeredWindowAttributes(window.Handle, attributes.ColorKey, attributes.Alpha, attributes.Flags) && IsValid(window);
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
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", ExactSpelling = true, SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", ExactSpelling = true, SetLastError = true)]
    private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", ExactSpelling = true, SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", ExactSpelling = true, SetLastError = true)]
    private static extern int SetWindowLong(nint window, int index, int value);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLayeredWindowAttributes(nint window, out uint colorKey, out byte alpha, out uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);

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
