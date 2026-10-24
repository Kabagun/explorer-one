// Ctrl+T command and native tab enumeration adapted from ExplorerTabUtility
// (w4po, copyright 2025), MIT, commit fad7363. Reservation/ownership state is new.
// See THIRD-PARTY-NOTICES.md.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ExplorerOne.Core;

namespace ExplorerOne.Shell;

internal enum NativeTabReservationStatus { Queued, Posted, Resolved, Ambiguous, Invalidated, PostFailed, Abandoned, Completed }
internal sealed record NativeTabReservationState(NativeTabReservationStatus Status, nint TargetTab, bool WasPosted, bool IsAbandoned, nint[] BeforeTabs);
internal sealed record NativeTabCleanupCandidate(long RequestId, WindowId Source, WindowId Target, nint CreatedTab, nint[] BeforeTabs, uint ShellProcessId);

internal sealed class NativeTabReservation
{
    internal readonly NativeTabReservations Manager;
    internal object? Owner;
    internal NativeTabReservationStatus CurrentStatus = NativeTabReservationStatus.Queued;
    internal nint[] Before = [];
    internal nint CreatedTab;
    internal bool Posted, Abandoned, CleanupReported;
    internal uint ShellPid;
    internal NativeTabReservation(NativeTabReservations manager, long id, WindowId source, WindowId target, object? owner, uint shellPid)
    { Manager = manager; RequestId = id; Source = source; Target = target; Owner = owner; ShellPid = shellPid; WasEarly = owner is null; }
    internal long RequestId { get; }
    internal WindowId Source { get; }
    internal WindowId Target { get; }
    internal bool WasEarly { get; }
    internal object? OwnerToken => Manager.ReadOwner(this);
    internal NativeTabReservationStatus Status => Manager.ReadState(this).Status;
    internal nint TargetTab => Manager.ReadState(this).TargetTab;
}

/// <summary>One native allocation at a time, shared by the WinEvent thread and COM owner STA.</summary>
internal sealed class NativeTabReservations : IDisposable
{
    private const int Capacity = 32;
    private readonly object _gate = new();
    private readonly INativeTabReservationNative _native;
    private readonly Queue<NativeTabReservation> _queue = [];
    private readonly List<NativeTabReservation> _records = [];
    private readonly Dictionary<WindowId, NativeTabReservation> _early = [];
    private readonly Queue<NativeTabCleanupCandidate> _cleanup = [];
    private NativeTabReservation? _inFlight;
    private long _nextId;
    private bool _disposed;
    private bool _acceptPreopening = true;

    internal NativeTabReservations(INativeTabReservationNative? native = null) => _native = native ?? new NativeTabReservationNative();
    internal bool HasPending { get { lock (_gate) return _inFlight is not null || _queue.Any(request => request.CurrentStatus == NativeTabReservationStatus.Queued); } }
    internal bool TryPreopen(WindowId source, WindowId target)
    {
        lock (_gate)
        {
            PulseCore();
            if (_disposed || !_acceptPreopening || _early.ContainsKey(source) || !ValidPair(source, target) || !_native.IsVisibleNormal(source) || !HasCapacity()) return false;
            var request = NewRequest(source, target, null);
            _early.Add(source, request);
            PulseCore();
            return request.CurrentStatus is NativeTabReservationStatus.Queued or NativeTabReservationStatus.Posted or NativeTabReservationStatus.Resolved;
        }
    }
    internal NativeTabReservation? ClaimEarly(WindowId source, WindowId target, object ownerToken)
    {
        ArgumentNullException.ThrowIfNull(ownerToken);
        lock (_gate) { PulseCore(); return ClaimEarlyCore(source, target, ownerToken); }
    }
    private NativeTabReservation? ClaimEarlyCore(WindowId source, WindowId target, object ownerToken)
    {
        if (_disposed || !_early.TryGetValue(source, out var request) || request.Target != target || request.Abandoned ||
            request.CurrentStatus is not (NativeTabReservationStatus.Queued or NativeTabReservationStatus.Posted or NativeTabReservationStatus.Resolved) ||
            request.Owner is not null && !ReferenceEquals(request.Owner, ownerToken)) return null;
        request.Owner = ownerToken;
        return request;
    }
    internal NativeTabReservation? GetOrRequest(WindowId source, WindowId target, object ownerToken)
    {
        ArgumentNullException.ThrowIfNull(ownerToken);
        lock (_gate)
        {
            PulseCore();
            if (_disposed) return null;
            var existing = _records.FirstOrDefault(request => request.Source == source && request.Target == target && ReferenceEquals(request.Owner, ownerToken));
            if (existing is not null) return existing; // One post per opaque owner, including failed/completed attempts.
            var early = ClaimEarlyCore(source, target, ownerToken);
            if (early is not null) return early;
            if (!ValidPair(source, target) || !HasCapacity()) return null;
            var request = NewRequest(source, target, ownerToken);
            PulseCore();
            return request;
        }
    }
    internal void Pulse() { lock (_gate) PulseCore(); }
    internal NativeTabReservationState PollResolve(NativeTabReservation request)
    { lock (_gate) { Check(request); PulseCore(); return StateCore(request); } }
    internal NativeTabReservationState ReadState(NativeTabReservation request)
    { lock (_gate) { Check(request); return StateCore(request); } }
    internal object? ReadOwner(NativeTabReservation request) { lock (_gate) { Check(request); return request.Owner; } }
    private static NativeTabReservationState StateCore(NativeTabReservation request) => new(request.CurrentStatus,
        request.CurrentStatus == NativeTabReservationStatus.Resolved ? request.CreatedTab : 0, request.Posted, request.Abandoned, (nint[])request.Before.Clone());

    internal void Complete(NativeTabReservation request)
    {
        lock (_gate)
        {
            Check(request);
            if (ReferenceEquals(_inFlight, request)) { AbandonCore(request); return; }
            request.CurrentStatus = NativeTabReservationStatus.Completed;
        }
    }
    internal void Abandon(NativeTabReservation request) { lock (_gate) { Check(request); AbandonCore(request); PulseCore(); } }
    internal void Cancel(NativeTabReservation request) => Abandon(request);
    internal void AbandonEarly(WindowId source)
    { lock (_gate) { if (_early.TryGetValue(source, out var request) && request.Owner is null) AbandonCore(request); PulseCore(); } }
    internal void StopPreopening()
    {
        lock (_gate)
        {
            _acceptPreopening = false;
            foreach (var request in _early.Values)
                if (request.Owner is null) AbandonCore(request);
            PulseCore(); // Accepted/claimed backend owners keep draining; unposted early hints cannot fire later.
        }
    }
    internal NativeTabCleanupCandidate[] TakeCleanupCandidates()
    { lock (_gate) { PulseCore(); var result = _cleanup.ToArray(); _cleanup.Clear(); return result; } }

    private bool HasCapacity() => _records.Count(request => request.CurrentStatus == NativeTabReservationStatus.Queued || ReferenceEquals(request, _inFlight)) < Capacity;
    private bool ValidPair(WindowId source, WindowId target) => source != target && _native.ShellProcessId != 0 &&
        _native.IsExplorerWindow(source) && _native.IsExplorerWindow(target);
    private NativeTabReservation NewRequest(WindowId source, WindowId target, object? owner)
    {
        var request = new NativeTabReservation(this, ++_nextId, source, target, owner, _native.ShellProcessId);
        _records.Add(request); _queue.Enqueue(request);
        return request;
    }
    private void Check(NativeTabReservation request)
    { if (!ReferenceEquals(request.Manager, this)) throw new ArgumentException("Reservation belongs to another manager.", nameof(request)); }
    private void AbandonCore(NativeTabReservation request)
    {
        if (request.CurrentStatus == NativeTabReservationStatus.Completed) return;
        request.Abandoned = true;
        if (request.CurrentStatus != NativeTabReservationStatus.Ambiguous && request.CurrentStatus != NativeTabReservationStatus.Invalidated)
            request.CurrentStatus = NativeTabReservationStatus.Abandoned;
        ReportCleanup(request);
        // Never release an accepted, unresolved post merely because its owner
        // cancelled. A late native tab must not bind to a later request.
    }
    private void ReportCleanup(NativeTabReservation request)
    {
        if (!request.Abandoned || request.CreatedTab == 0 || request.CleanupReported) return;
        request.CleanupReported = true;
        _cleanup.Enqueue(new NativeTabCleanupCandidate(request.RequestId, request.Source, request.Target,
            request.CreatedTab, (nint[])request.Before.Clone(), request.ShellPid));
    }
    private void PulseCore()
    {
        try
        {
            foreach (var request in _records)
            {
                if (request.CurrentStatus is NativeTabReservationStatus.Completed or NativeTabReservationStatus.PostFailed) continue;
                if (!_native.IsExplorerWindow(request.Target) || request.ShellPid != _native.ShellProcessId)
                {
                    request.CurrentStatus = NativeTabReservationStatus.Invalidated; request.Abandoned = true;
                    if (ReferenceEquals(request, _inFlight) && !_native.IsExplorerWindow(request.Target)) _inFlight = null;
                    ReportCleanup(request);
                }
                else if (request.Owner is null && !_native.IsExplorerWindow(request.Source)) AbandonCore(request);
                if (request.CurrentStatus == NativeTabReservationStatus.Resolved && !_native.ReadTabs(request.Target).Contains(request.CreatedTab))
                { request.CurrentStatus = NativeTabReservationStatus.Invalidated; request.Abandoned = true; }
            }
            foreach (var pair in _early.Where(pair => !_native.IsExplorerWindow(pair.Key) && !ReferenceEquals(pair.Value, _inFlight)).ToArray()) _early.Remove(pair.Key);
            _records.RemoveAll(request => !ReferenceEquals(request, _inFlight) &&
                (request.CurrentStatus is NativeTabReservationStatus.Completed or NativeTabReservationStatus.PostFailed or NativeTabReservationStatus.Abandoned or NativeTabReservationStatus.Invalidated) &&
                !_native.IsExplorerWindow(request.Source));
            // A retained ambiguous barrier can outlive many cancelled hints.
            // Do not let their old queue slots retain dead owner graphs after
            // the record/early indexes above have released them.
            if (_queue.Any(request => request.CurrentStatus != NativeTabReservationStatus.Queued))
            {
                var queued = _queue.Where(request => request.CurrentStatus == NativeTabReservationStatus.Queued).ToArray();
                _queue.Clear();
                foreach (var request in queued) _queue.Enqueue(request);
            }
            if (_inFlight is not null)
            {
                var request = _inFlight;
                if (!_native.IsExplorerWindow(request.Target)) { request.CurrentStatus = NativeTabReservationStatus.Invalidated; _inFlight = null; }
                else
                {
                    var current = _native.ReadTabs(request.Target);
                    var added = current.Except(request.Before).ToArray();
                    if (!request.Before.All(current.Contains) || added.Length > 1)
                        request.CurrentStatus = NativeTabReservationStatus.Ambiguous; // Retain global barrier; no guessed tab or next post.
                    else if (added.Length == 1 && request.CurrentStatus != NativeTabReservationStatus.Ambiguous)
                    {
                        request.CreatedTab = added[0]; _inFlight = null;
                        if (request.ShellPid != _native.ShellProcessId) { request.Abandoned = true; request.CurrentStatus = NativeTabReservationStatus.Invalidated; }
                        else request.CurrentStatus = request.Abandoned ? NativeTabReservationStatus.Abandoned : NativeTabReservationStatus.Resolved;
                        ReportCleanup(request);
                    }
                }
            }
            if (_disposed || _inFlight is not null) return;
            while (_queue.Count != 0)
            {
                var request = _queue.Dequeue();
                if (request.CurrentStatus != NativeTabReservationStatus.Queued) continue;
                if (!_native.IsExplorerWindow(request.Target) || request.Owner is null && !_native.IsExplorerWindow(request.Source) ||
                    request.ShellPid == 0 || request.ShellPid != _native.ShellProcessId)
                { request.CurrentStatus = NativeTabReservationStatus.Invalidated; request.Abandoned = true; continue; }
                var before = _native.ReadTabs(request.Target);
                var active = _native.ActiveTab(request.Target);
                if (before.Length == 0 || before.Distinct().Count() != before.Length || !before.Contains(active) ||
                    !before.ToHashSet().SetEquals(_native.ReadTabs(request.Target)))
                { request.CurrentStatus = NativeTabReservationStatus.PostFailed; continue; }
                request.Before = before;
                _inFlight = request; request.CurrentStatus = NativeTabReservationStatus.Posted;
                request.Posted = _native.PostNewTab(request.Target, active);
                if (request.Posted) return;
                request.CurrentStatus = NativeTabReservationStatus.PostFailed; _inFlight = null;
            }
        }
        catch
        {
            // Keep an uncertain accepted allocation as a barrier. Native hint
            // failures must not escape a WinEvent callback or trigger a repost.
            if (_inFlight is not null) _inFlight.CurrentStatus = NativeTabReservationStatus.Ambiguous;
        }
    }
    public void Dispose()
    { lock (_gate) { if (_disposed) return; _disposed = true; foreach (var request in _records) AbandonCore(request); PulseCore(); } }
}

internal interface INativeTabReservationNative
{
    uint ShellProcessId { get; }
    bool IsExplorerWindow(WindowId window);
    bool IsVisibleNormal(WindowId window);
    nint[] ReadTabs(WindowId window);
    nint ActiveTab(WindowId target);
    bool PostNewTab(WindowId target, nint activeTab);
}

internal sealed class NativeTabReservationNative : INativeTabReservationNative
{
    public uint ShellProcessId { get { GetWindowThreadProcessId(GetShellWindow(), out var pid); return pid; } }
    public bool IsExplorerWindow(WindowId window)
    {
        if (!IsWindow(window.Handle) || GetAncestor(window.Handle, 2) != window.Handle || Class(window.Handle) != "CabinetWClass" ||
            GetWindowThreadProcessId(window.Handle, out var pid) == 0 || pid != window.ProcessId || pid == 0) return false;
        try { using var process = Process.GetProcessById(checked((int)pid)); return process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
    public bool IsVisibleNormal(WindowId window) => IsExplorerWindow(window) && IsWindowVisible(window.Handle) && !IsIconic(window.Handle);
    public nint[] ReadTabs(WindowId window)
    {
        if (!IsExplorerWindow(window)) return [];
        var result = new List<nint>(); nint child = 0;
        while ((child = FindWindowEx(window.Handle, child, "ShellTabWindowClass", null)) != 0)
            if (Belongs(window, child)) result.Add(child);
        return result.ToArray();
    }
    public nint ActiveTab(WindowId target) => FindWindowEx(target.Handle, 0, "ShellTabWindowClass", null);
    public bool PostNewTab(WindowId target, nint activeTab)
    {
        // Focus changes can wait on another GUI thread. The early allocation
        // path only posts the command; appearance/routing activate separately.
        return IsExplorerWindow(target) && Belongs(target, activeTab) &&
            PostMessage(activeTab, 0x0111, 0xA21B, 0);
    }
    private static bool Belongs(WindowId target, nint tab) => IsWindow(tab) && Class(tab) == "ShellTabWindowClass" &&
        GetAncestor(tab, 2) == target.Handle && GetWindowThreadProcessId(tab, out var pid) != 0 && pid == target.ProcessId;
    private static string Class(nint window)
    { var name = new StringBuilder(64); return GetClassName(window, name, name.Capacity) == 0 ? string.Empty : name.ToString(); }
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nuint first, nint second);
}
