// Public connection-point contract: Windows SDK ExDisp.idl / ExDispId.h.
// No cookies, window identities or browser RCWs are cached by this wakeup sink.
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace ExplorerOne.Shell;

[ComVisible(true), Guid("FE4106E0-399A-11D0-A48C-00A0C90A8F39")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IShellWindowWakeupEvents
{
    [DispId(200)] void WindowRegistered(int cookie);
    [DispId(201)] void WindowRevoked(int cookie);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class ShellWindowWakeupSink : IShellWindowWakeupEvents
{
    private readonly ShellWindowEventSubscription _subscription;
    internal ShellWindowWakeupSink(ShellWindowEventSubscription subscription) => _subscription = subscription;
    public void WindowRegistered(int cookie) => _subscription.PostWakeup();
    public void WindowRevoked(int cookie) { }
}

internal sealed class ShellWindowEventSubscription : IDisposable
{
    private readonly SynchronizationContext _context;
    private readonly ShellWindowWakeupSink _sink;
    private Action? _callback;
    private IConnectionPoint? _point;
    private int _adviceCookie;
    private bool _advised;
    private int _attached = 1, _posted;

    private ShellWindowEventSubscription(SynchronizationContext context, Action callback)
    {
        _context = context;
        _callback = callback;
        _sink = new ShellWindowWakeupSink(this); // Retained until after Unadvise.
    }

    internal static ShellWindowEventSubscription? TryAttach(object windows, SynchronizationContext context, Action callback)
    {
        var subscription = new ShellWindowEventSubscription(context, callback);
        try
        {
            if (windows is not IConnectionPointContainer container)
            {
                subscription.Dispose();
                return null;
            }
            var iid = typeof(IShellWindowWakeupEvents).GUID;
            container.FindConnectionPoint(ref iid, out subscription._point);
            if (subscription._point is null)
            {
                subscription.Dispose();
                return null;
            }
            subscription._point.Advise(subscription._sink, out subscription._adviceCookie);
            subscription._advised = true;
            return subscription;
        }
        catch
        {
            subscription.Dispose();
            return null; // Optional hint; native events and periodic discovery remain authoritative.
        }
    }

    internal void PostWakeup()
    {
        // COM may call this from another thread. Do not access backend/window/
        // RCW state here; at most one owner-context notification is queued.
        if (Volatile.Read(ref _attached) == 0 || Interlocked.Exchange(ref _posted, 1) != 0)
            return;
        try { _context.Post(static state => ((ShellWindowEventSubscription)state!).Deliver(), this); }
        catch { Interlocked.Exchange(ref _posted, 0); }
    }

    private void Deliver()
    {
        Interlocked.Exchange(ref _posted, 0);
        if (Volatile.Read(ref _attached) == 0) return;
        var callback = Volatile.Read(ref _callback);
        try { callback?.Invoke(); }
        catch { /* A wakeup hint cannot affect COM event delivery or routing. */ }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _attached, 0); // Detach BEFORE a possibly reentrant Unadvise.
        Interlocked.Exchange(ref _callback, null);
        var point = _point;
        _point = null;
        if (point is null) return;
        try { if (_advised) point.Unadvise(_adviceCookie); }
        catch { /* Explorer may have disconnected/restarted. */ }
        finally { ComReference.Release(point); }
    }
}

public sealed partial class ShellExplorerBackend
{
    private ShellWindowEventSubscription? _shellWindowEvents;
    private bool _attachingShellWindowEvents;
    private long _nextShellWindowAdviceAttemptAt;

    internal bool DiagnosticShellWindowRegistrationActive => _shellWindowEvents is not null;
    internal long DiagnosticShellWindowRegistrationWakeups { get; private set; }

    private void EnsureShellWindowEvents()
    {
        if (_disposed || _shellWindows is null || _shellWindowEvents is not null || _attachingShellWindowEvents ||
            Environment.TickCount64 < _nextShellWindowAdviceAttemptAt)
            return;
        _attachingShellWindowEvents = true;
        _nextShellWindowAdviceAttemptAt = Environment.TickCount64 + 5000;
        var generation = _generation;
        var windows = _shellWindows;
        try
        {
            var subscription = ShellWindowEventSubscription.TryAttach(windows, _ownerContext, () =>
            {
                if (_disposed || generation != _generation) return;
                DiagnosticShellWindowRegistrationWakeups++;
                try { ShellWindowRegistered?.Invoke(); }
                catch { /* Subscribers schedule a wakeup only. */ }
            });
            if (_disposed || generation != _generation || !ReferenceEquals(windows, _shellWindows))
                subscription?.Dispose();
            else
                _shellWindowEvents = subscription;
        }
        finally { _attachingShellWindowEvents = false; }
    }

    private void DisposeShellWindowEvents()
    {
        var subscription = _shellWindowEvents;
        _shellWindowEvents = null;
        _nextShellWindowAdviceAttemptAt = 0;
        subscription?.Dispose();
    }
}
