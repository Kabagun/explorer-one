using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ExplorerOne.Core;

namespace ExplorerOne;

/// <summary>Native Explorer hints on a dedicated STA; backend polls stay on the owning WinForms STA.</summary>
internal sealed class ExplorerWakeupMonitor : IDisposable
{
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectShow = 0x8002;
    private const uint EventSystemForeground = 0x0003;
    private const uint EventSystemMinimizeEnd = 0x0017;
    private const uint WinEventOutOfContext = 0;
    private const uint WinEventSkipOwnProcess = 2;
    private const uint WmQuit = 0x0012;
    private const uint WmTimer = 0x0113;
    private const int ObjectIdWindow = 0;
    private const int ChildIdSelf = 0;

    private readonly Action _requestPoll;
    private readonly Action<WindowId> _windowShown;
    private readonly Action<WindowId>? _windowCreated;
    private readonly Action? _presentationPulse;
    private readonly Func<bool>? _nativeWorkPending;
    private readonly WindowsFormsSynchronizationContext _ownerContext;
    private readonly WinEventCallback _callback;
    private readonly Thread _worker;
    private readonly object _queueGate = new();
    private uint _workerThreadId;
    private int _queueReady, _active, _disposed, _pollPosted;
    private nuint _pulseTimer;
    private uint _pulseInterval;

    public ExplorerWakeupMonitor(Action requestPoll, Action<WindowId> windowShown, Action? presentationPulse = null,
        Action<WindowId>? windowCreated = null, Func<bool>? nativeWorkPending = null)
    {
        ArgumentNullException.ThrowIfNull(requestPoll);
        ArgumentNullException.ThrowIfNull(windowShown);
        _ownerContext = SynchronizationContext.Current as WindowsFormsSynchronizationContext ??
            throw new InvalidOperationException("Explorer wakeup monitoring requires the owning WinForms context.");
        _requestPoll = requestPoll;
        _windowShown = windowShown;
        _windowCreated = windowCreated;
        _presentationPulse = presentationPulse;
        _nativeWorkPending = nativeWorkPending;
        _callback = OnWinEvent;
        _worker = new Thread(RunHookLoop) { IsBackground = true, Name = "ExplorerOne native window hints" };
        try
        {
            _worker.SetApartmentState(ApartmentState.STA);
            _worker.Start();
        }
        catch
        {
            // Hooks are hints only. The owner retains its faster fallback poll.
            Volatile.Write(ref _active, 0);
        }
    }

    public bool IsActive => Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _active) != 0;

    private void RunHookLoop()
    {
        // The running thread and this local keep the native delegate rooted,
        // even if Dispose's bounded join returns before a callback finishes.
        var callback = _callback;
        nint hook = 0;
        nint foregroundHook = 0;
        nint minimizeEndHook = 0;
        try
        {
            Volatile.Write(ref _workerThreadId, GetCurrentThreadId());
            // PostThreadMessage needs an existing queue. Publish readiness only
            // after PeekMessage creates it; Dispose-before-ready is checked next.
            _ = PeekMessage(out _, 0, 0, 0, 0);
            lock (_queueGate) Volatile.Write(ref _queueReady, 1);
            if (Volatile.Read(ref _disposed) != 0) return;

            // OUTOFCONTEXT events are delivered on the registering thread.
            // This thread never creates a shell RCW or performs COM work.
            hook = SetWinEventHook(EventObjectCreate, EventObjectShow, 0, callback, 0, 0,
                WinEventOutOfContext | WinEventSkipOwnProcess);
            // Preloaded frames may restore without OBJECT_SHOW. Subscribe only
            // to these two exact lifecycle events, not the intervening range.
            foregroundHook = SetWinEventHook(EventSystemForeground, EventSystemForeground, 0, callback, 0, 0,
                WinEventOutOfContext | WinEventSkipOwnProcess);
            minimizeEndHook = SetWinEventHook(EventSystemMinimizeEnd, EventSystemMinimizeEnd, 0, callback, 0, 0,
                WinEventOutOfContext | WinEventSkipOwnProcess);
            if (_presentationPulse is not null)
            {
                _pulseInterval = 250;
                _pulseTimer = SetTimer(0, 0, _pulseInterval, 0);
            }
            if (Volatile.Read(ref _disposed) != 0) return;
            if (hook != 0 || foregroundHook != 0 || minimizeEndHook != 0)
            {
                // Partial coverage still supplies hints, while the owner keeps
                // its faster safety poll if any required registration failed.
                Volatile.Write(ref _active, hook != 0 && foregroundHook != 0 && minimizeEndHook != 0 ? 1 : 0);
                PostOwnerPoll(); // Readiness hint; constructor never waits on the UI.
            }

            while (Volatile.Read(ref _disposed) == 0)
            {
                // GetMessage returns -1 on failure, zero on WM_QUIT.
                if (GetMessage(out var message, 0, 0, 0) <= 0) break;
                if (Volatile.Read(ref _disposed) != 0) break;
                if (_pulseTimer != 0 && message.Window == 0 && message.Message == WmTimer && message.WParam == _pulseTimer)
                {
                    try { _presentationPulse?.Invoke(); }
                    catch { /* A stale native window cannot terminate the hook pump. */ }
                    RefreshPulseInterval();
                    continue;
                }
                _ = TranslateMessage(ref message);
                _ = DispatchMessage(ref message);
            }
        }
        catch
        {
            // Native monitoring is optional; the owner still performs safety polls.
        }
        finally
        {
            Volatile.Write(ref _active, 0);
            if (_pulseTimer != 0) _ = KillTimer(0, _pulseTimer);
            _pulseTimer = 0;
            if (minimizeEndHook != 0) _ = UnhookWinEvent(minimizeEndHook);
            if (foregroundHook != 0) _ = UnhookWinEvent(foregroundHook);
            if (hook != 0) _ = UnhookWinEvent(hook);
            // Retire the queue under the same short gate used by Dispose's
            // asynchronous post. No callback, COM call or join holds this gate.
            lock (_queueGate)
            {
                Volatile.Write(ref _queueReady, 0);
                Volatile.Write(ref _workerThreadId, 0);
            }
            GC.KeepAlive(callback);
        }
    }

    private void OnWinEvent(nint hook, uint eventType, nint window, int objectId, int childId,
        uint eventThread, uint eventTime)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            eventType is not (EventObjectCreate or EventObjectShow or EventSystemForeground or EventSystemMinimizeEnd) ||
            window == 0 || objectId != ObjectIdWindow || childId != ChildIdSelf)
            return;
        try
        {
            // Reject controls before opening a process handle. This callback
            // shares no shell object/native COM pointer with the owner STA.
            var className = new StringBuilder(64);
            if (GetClassName(window, className, className.Capacity) == 0 ||
                !string.Equals(className.ToString(), "CabinetWClass", StringComparison.Ordinal)) return;
            if (GetWindowThreadProcessId(window, out var processId) == 0 || processId == 0) return;
            using var process = Process.GetProcessById(checked((int)processId));
            if (!string.Equals(process.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase)) return;
            if (Volatile.Read(ref _disposed) != 0) return;
            if (eventType == EventObjectCreate)
            {
                // Suppress a newly allocated extra frame before its first
                // visible show when Explorer supplies a CREATE notification.
                try { _windowCreated?.Invoke(new WindowId(window, processId)); }
                catch { /* SHOW remains an independent presentation retry. */ }
            }
            else if (IsWindowVisible(window))
            {
                // The caller supplies a thread-safe, native-only presentation
                // action. It must react even while owner COM calls are blocked.
                try { _windowShown(new WindowId(window, processId)); }
                catch { /* Presentation failure must not suppress the poll hint. */ }
            }
            PostOwnerPoll();
            RefreshPulseInterval();
        }
        catch
        {
            // Queued accessibility events can outlive their window/process.
            // Never propagate managed exceptions across the user32 callback.
        }
    }

    private void RefreshPulseInterval()
    {
        // Native allocation resolution must not wait for the COM actor. A
        // short timer is active only while posted/queued requests need work.
        if (_pulseTimer == 0 || Volatile.Read(ref _disposed) != 0) return;
        uint interval;
        try { interval = _nativeWorkPending?.Invoke() == true ? 10u : 250u; }
        catch { interval = 250; }
        if (interval == _pulseInterval) return;
        var timer = SetTimer(0, _pulseTimer, interval, 0);
        if (timer != 0) { _pulseTimer = timer; _pulseInterval = interval; }
    }

    private void PostOwnerPoll()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _pollPosted, 1) != 0) return;
        try
        {
            _ownerContext.Post(static state =>
            {
                var monitor = (ExplorerWakeupMonitor)state!;
                Interlocked.Exchange(ref monitor._pollPosted, 0);
                if (Volatile.Read(ref monitor._disposed) != 0) return;
                try { monitor._requestPoll(); }
                catch { /* A poll hint cannot fault the owning WinForms pump. */ }
            }, this);
        }
        catch
        {
            Interlocked.Exchange(ref _pollPosted, 0); // Owner context may already be shutting down.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Volatile.Write(ref _active, 0);
        lock (_queueGate)
        {
            if (Volatile.Read(ref _queueReady) != 0)
                _ = PostThreadMessage(Volatile.Read(ref _workerThreadId), WmQuit, 0, 0);
        }
        if (Thread.CurrentThread == _worker) return;
        try { _ = _worker.Join(1000); }
        catch (ThreadStateException) { /* Thread startup failed; no hook was registered. */ }
        // Hook/timer removal belongs to the registering thread's finally block.
        // A late callback keeps its own delegate/monitor alive until unhooked.
    }

    private delegate void WinEventCallback(nint hook, uint eventType, nint window, int objectId,
        int childId, uint eventThread, uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        internal nint Window;
        internal uint Message;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal NativePoint Point;
        internal uint Private;
    }

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module,
        WinEventCallback callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder className, int capacity);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", EntryPoint = "PeekMessageW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMessage message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll", EntryPoint = "GetMessageW", ExactSpelling = true)]
    private static extern int GetMessage(out NativeMessage message, nint window, uint min, uint max);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW", ExactSpelling = true)]
    private static extern nint DispatchMessage(ref NativeMessage message);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern nuint SetTimer(nint window, nuint id, uint milliseconds, nint callback);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool KillTimer(nint window, nuint id);
}
