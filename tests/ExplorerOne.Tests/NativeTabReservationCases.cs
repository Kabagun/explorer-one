using ExplorerOne.Core;
using ExplorerOne.Shell;

internal static class NativeTabReservationCases
{
    internal static IEnumerable<(string Name, Func<Task> Run)> All =>
    [
        ("Stopping preopen cancels unclaimed hints while accepted backend owners drain", () =>
        {
            var native = new Fake(); native.Live.Add(Id(4)); using var manager = new NativeTabReservations(native);
            manager.TryPreopen(Id(2), Id(1));
            var first = manager.ClaimEarly(Id(2), Id(1), new object())!;
            manager.TryPreopen(Id(3), Id(1));
            var owned = manager.GetOrRequest(Id(4), Id(1), new object())!;
            manager.StopPreopening();
            Check(!manager.TryPreopen(Id(4), Id(1)) && native.Posts == 1);
            native.AddTab(11); manager.Pulse();
            Check(first.TargetTab == 11 && native.Posts == 2 && owned.Status == NativeTabReservationStatus.Posted);
            native.AddTab(12); manager.Pulse(); Check(owned.TargetTab == 12);
            return Task.CompletedTask;
        }),
        ("Completed dead-source records release opaque owner history without losing an unresolved barrier", () =>
        {
            var native = new Fake(); using var manager = new NativeTabReservations(native);
            var owner = new object(); var completed = manager.GetOrRequest(Id(2), Id(1), owner)!;
            native.AddTab(11); manager.Pulse(); native.Live.Remove(Id(2)); manager.Complete(completed); manager.Pulse();
            Check(manager.GetOrRequest(Id(2), Id(1), owner) is null);
            var unresolved = manager.GetOrRequest(Id(3), Id(1), new object())!;
            native.Live.Remove(Id(3)); manager.Abandon(unresolved); manager.Pulse();
            Check(manager.HasPending && native.Posts == 2 && unresolved.TargetTab == 0);
            return Task.CompletedTask;
        }),
        ("Standby hidden or iconic roots never preopen but remain eligible at first normal show", () =>
        {
            var native = new Fake(); using var manager = new NativeTabReservations(native);
            native.NotNormal.Add(Id(2));
            Check(!manager.TryPreopen(Id(2), Id(1)) && native.Posts == 0 && !manager.HasPending);
            native.NotNormal.Remove(Id(2));
            Check(manager.TryPreopen(Id(2), Id(1)) && native.Posts == 1);
            // The visibility restriction belongs only to early hints: a
            // captured strict owner can allocate for a minimized source.
            native.NotNormal.Add(Id(3));
            Check(manager.GetOrRequest(Id(3), Id(1), new object()) is not null);
            return Task.CompletedTask;
        }),
        ("Native preopen posts before capture and duplicate callbacks claim exactly one request", () =>
        {
            var native = new Fake(); using var manager = new NativeTabReservations(native);
            Check(manager.TryPreopen(Id(2), Id(1)) && native.Posts == 1);
            Check(!manager.TryPreopen(Id(2), Id(1)) && native.Posts == 1);
            var owner = new object(); var request = manager.ClaimEarly(Id(2), Id(1), owner)!;
            Check(request.WasEarly && ReferenceEquals(request, manager.GetOrRequest(Id(2), Id(1), owner)));
            Check(request.Status == NativeTabReservationStatus.Posted && request.TargetTab == 0 && native.Posts == 1);
            native.AddTab(11); manager.Pulse();
            Check(request.Status == NativeTabReservationStatus.Resolved && request.TargetTab == 11);
            manager.Complete(request);
            Check(!manager.TryPreopen(Id(2), Id(1)) && native.Posts == 1 && !manager.HasPending);
            return Task.CompletedTask;
        }),
        ("Native burst allocation is serialized and binds distinct new handles only", () =>
        {
            var native = new Fake(); using var manager = new NativeTabReservations(native);
            var first = manager.GetOrRequest(Id(2), Id(1), new object())!;
            var second = manager.GetOrRequest(Id(3), Id(1), new object())!;
            Check(native.Posts == 1 && second.Status == NativeTabReservationStatus.Queued);
            native.AddTab(11); manager.Pulse();
            Check(first.TargetTab == 11 && native.Posts == 2 && second.TargetTab == 0);
            native.AddTab(12); var resolved = manager.PollResolve(second);
            Check(resolved.Status == NativeTabReservationStatus.Resolved && resolved.TargetTab == 12 &&
                resolved.BeforeTabs.ToHashSet().SetEquals(new nint[] { 10, 11 }) && !manager.HasPending);
            return Task.CompletedTask;
        }),
        ("Ambiguous native allocation never guesses a handle or releases the global barrier", () =>
        {
            var native = new Fake(); using var manager = new NativeTabReservations(native);
            var first = manager.GetOrRequest(Id(2), Id(1), new object())!;
            var second = manager.GetOrRequest(Id(3), Id(1), new object())!;
            native.AddTab(11); native.AddTab(12); manager.Pulse();
            Check(first.Status == NativeTabReservationStatus.Ambiguous && first.TargetTab == 0 && native.Posts == 1);
            manager.Abandon(first); manager.Complete(first); manager.Pulse();
            Check(native.Posts == 1 && second.Status == NativeTabReservationStatus.Queued && manager.HasPending);
            return Task.CompletedTask;
        }),
        ("Abandoning a posted request retains late binding solely as an orphan cleanup candidate", () =>
        {
            var native = new Fake(); using var manager = new NativeTabReservations(native);
            Check(manager.TryPreopen(Id(2), Id(1)));
            manager.AbandonEarly(Id(2));
            var next = manager.GetOrRequest(Id(3), Id(1), new object())!;
            Check(native.Posts == 1 && manager.TakeCleanupCandidates().Length == 0);
            native.AddTab(11); manager.Pulse();
            var cleanup = manager.TakeCleanupCandidates();
            Check(cleanup.Length == 1 && cleanup[0].CreatedTab == 11 && cleanup[0].BeforeTabs.SequenceEqual(new nint[] { 10 }) &&
                next.TargetTab == 0 && native.Posts == 2 && manager.TakeCleanupCandidates().Length == 0);
            native.AddTab(12); manager.Pulse(); Check(next.TargetTab == 12);
            return Task.CompletedTask;
        }),
        ("Unclaimed source disappearance becomes orphan while claimed background allocation survives source close", () =>
        {
            var native = new Fake(); using var manager = new NativeTabReservations(native);
            manager.TryPreopen(Id(2), Id(1)); native.Live.Remove(Id(2)); manager.Pulse();
            native.AddTab(11); manager.Pulse(); Check(manager.TakeCleanupCandidates().Single().CreatedTab == 11);
            var claimed = manager.GetOrRequest(Id(3), Id(1), new object())!;
            native.Live.Remove(Id(3)); native.AddTab(12); manager.Pulse();
            Check(claimed.TargetTab == 12 && claimed.Status == NativeTabReservationStatus.Resolved);
            return Task.CompletedTask;
        }),
        ("Target loss and shell restart invalidate requests without arbitrary native cleanup", () =>
        {
            var native = new Fake(); using var manager = new NativeTabReservations(native);
            var request = manager.GetOrRequest(Id(2), Id(1), new object())!;
            native.ShellProcessId = 2; manager.Pulse();
            Check(request.Status == NativeTabReservationStatus.Invalidated && request.TargetTab == 0 && manager.HasPending);
            native.Live.Remove(Id(1)); manager.Pulse();
            Check(!manager.HasPending && native.Posts == 1 && manager.TakeCleanupCandidates().Length == 0);
            return Task.CompletedTask;
        }),
        ("Native post failure is not retried for the same opaque owner", () =>
        {
            var native = new Fake { PostSucceeds = false }; using var manager = new NativeTabReservations(native);
            var owner = new object(); var request = manager.GetOrRequest(Id(2), Id(1), owner)!;
            Check(request.Status == NativeTabReservationStatus.PostFailed && !manager.HasPending);
            native.PostSucceeds = true;
            Check(ReferenceEquals(request, manager.GetOrRequest(Id(2), Id(1), owner)) && native.Posts == 1);
            return Task.CompletedTask;
        }),
        ("Native queue is bounded and cancelled queued requests are never posted", () =>
        {
            var native = new Fake(); using var manager = new NativeTabReservations(native);
            NativeTabReservation? cancelled = null;
            for (var index = 2; index < 34; index++)
            {
                native.Live.Add(Id(index)); var request = manager.GetOrRequest(Id(index), Id(1), new object());
                Check(request is not null); if (index == 3) cancelled = request;
            }
            native.Live.Add(Id(34)); Check(manager.GetOrRequest(Id(34), Id(1), new object()) is null);
            manager.Cancel(cancelled!); native.AddTab(11); manager.Pulse();
            Check(cancelled!.Status == NativeTabReservationStatus.Abandoned && native.Posts == 2);
            manager.Dispose(); Check(native.Posts == 2);
            return Task.CompletedTask;
        })
    ];
    private static WindowId Id(int value) => new(value, 100);
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Native reservation contract assertion failed."); }
    private sealed class Fake : INativeTabReservationNative
    {
        internal HashSet<WindowId> Live { get; } = [Id(1), Id(2), Id(3)];
        internal HashSet<WindowId> NotNormal { get; } = [];
        private readonly List<nint> _tabs = [10];
        internal int Posts;
        internal bool PostSucceeds = true;
        public uint ShellProcessId { get; set; } = 1;
        public bool IsExplorerWindow(WindowId window) => Live.Contains(window);
        public bool IsVisibleNormal(WindowId window) => Live.Contains(window) && !NotNormal.Contains(window);
        public nint[] ReadTabs(WindowId window) => Live.Contains(window) ? _tabs.ToArray() : [];
        public nint ActiveTab(WindowId target) => _tabs[0];
        public bool PostNewTab(WindowId target, nint activeTab) { Posts++; return PostSucceeds && activeTab == _tabs[0]; }
        internal void AddTab(nint tab) => _tabs.Add(tab);
    }
}
