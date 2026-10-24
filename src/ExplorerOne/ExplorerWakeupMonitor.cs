using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ExplorerOne.Core;

namespace ExplorerOne;

/// <summary>Optional hints for newly created/shown Explorer windows on the owning UI STA.</summary>
internal sealed class ExplorerWakeupMonitor : IDisposable
{
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectShow = 0x8002;
    private const uint WinEventOutOfContext = 0;
    private const uint WinEventSkipOwnProcess = 2;
    private const int ObjectIdWindow = 0;
    private const int ChildIdSelf = 0;

    private readonly Action _requestPoll;
    private readonly Action<WindowId> _windowShown;
    // Native hooks do not own a managed delegate reference. Keep it alive until
    // after UnhookWinEvent has removed the callback.
    private readonly WinEventCallback _callback;
    private nint _hook;
    private bool _disposed;

    public ExplorerWakeupMonitor(Action requestPoll, Action<WindowId> windowShown)
    {
        _requestPoll = requestPoll;
        _windowShown = windowShown;
        _callback = OnWinEvent;
        try
        {
            // Zero process/thread IDs cover a restarted Explorer as well. The
            // out-of-context callback runs on the thread registering this hook.
            _hook = SetWinEventHook(EventObjectCreate, EventObjectShow, 0, _callback, 0, 0,
                WinEventOutOfContext | WinEventSkipOwnProcess);
        }
        catch
        {
            // The host uses a faster safety poll if hooks are unavailable.
            _hook = 0;
        }
    }

    public bool IsActive => !_disposed && _hook != 0;

    private void OnWinEvent(nint hook, uint eventType, nint window, int objectId, int childId,
        uint eventThread, uint eventTime)
    {
        if (_disposed || eventType is not (EventObjectCreate or EventObjectShow) ||
            window == 0 || objectId != ObjectIdWindow || childId != ChildIdSelf)
            return;

        try
        {
            // Most accessibility events concern controls. Reject those before
            // opening a process handle, and keep COM out of this callback.
            var className = new StringBuilder(64);
            if (GetClassName(window, className, className.Capacity) == 0 ||
                !string.Equals(className.ToString(), "CabinetWClass", StringComparison.Ordinal))
                return;

            if (GetWindowThreadProcessId(window, out var processId) == 0 || processId == 0)
                return;

            using var process = Process.GetProcessById(checked((int)processId));
            if (!string.Equals(process.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase))
                return;

            if (eventType == EventObjectShow && IsWindowVisible(window))
                _windowShown(new WindowId(window, processId));
            _requestPoll();
        }
        catch
        {
            // The window/process may disappear while the event is queued. A
            // native callback must never propagate an exception across user32.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_hook != 0)
        {
            UnhookWinEvent(_hook);
            _hook = 0;
        }
        GC.KeepAlive(_callback);
    }

    private delegate void WinEventCallback(nint hook, uint eventType, nint window, int objectId,
        int childId, uint eventThread, uint eventTime);

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
}
