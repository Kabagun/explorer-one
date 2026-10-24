using ExplorerOne.Core;
using ExplorerOne.Shell;

internal static class LatencyCases
{
    internal static IEnumerable<(string Name, Func<Task> Run)> All =>
    [
        ("Lifecycle discovery uses a validated existing main without a global COM scan", async () =>
        {
            var inner = Backend(1);
            var backend = new HintBackend(inner);
            var coordinator = new WindowCoordinator(backend);
            await coordinator.PollAsync(preferFastDiscovery: true);
            Check(backend.FullDiscoveries == 1 && backend.HintDiscoveries == 0 && coordinator.MainWindow == Id(1));
            inner.Windows.Add(new ExplorerWindow(Id(2), 1));
            await coordinator.PollAsync(preferFastDiscovery: true);
            Check(backend.FullDiscoveries == 1 && backend.HintDiscoveries == 1);
            Check(inner.Calls.Single() == (Id(2), Id(1)) && coordinator.PendingCount == 0);
        }),
        ("An unavailable main hint falls back to full discovery", async () =>
        {
            var inner = Backend(1);
            var backend = new HintBackend(inner) { HintAvailable = false };
            var coordinator = new WindowCoordinator(backend);
            await coordinator.PollAsync();
            inner.Windows.Add(new ExplorerWindow(Id(2), 1));
            await coordinator.PollAsync(preferFastDiscovery: true);
            Check(backend.FullDiscoveries == 2 && backend.HintDiscoveries == 1);
            Check(inner.Calls.Single() == (Id(2), Id(1)));
        }),
        ("A vanished main cannot authorize hinted transfers", async () =>
        {
            var inner = Backend(1);
            var backend = new HintBackend(inner);
            var coordinator = new WindowCoordinator(backend);
            await coordinator.PollAsync();
            inner.Windows.Clear();
            inner.Windows.Add(new ExplorerWindow(Id(2), 1));
            await coordinator.PollAsync(preferFastDiscovery: true);
            Check(backend.FullDiscoveries == 2 && backend.HintDiscoveries == 1);
            Check(coordinator.MainWindow == Id(2) && inner.Calls.Count == 0);
        }),
        ("Hinted discovery preserves source rejection and retry policy", async () =>
        {
            var inner = Backend(1);
            inner.Transfer = (_, _, _) => Task.FromResult(TransferResult.Unsafe);
            var backend = new HintBackend(inner);
            var coordinator = new WindowCoordinator(backend, new MillisecondClock());
            await coordinator.PollAsync();
            inner.Windows.Add(new ExplorerWindow(Id(2), 1));
            await coordinator.PollAsync(preferFastDiscovery: true);
            Check(coordinator.MainWindow == Id(1) && coordinator.PendingCount == 1);
            Check(coordinator.LastTransfer is { Result: TransferResult.Unsafe });
            Check(coordinator.NextPendingDelay == TimeSpan.FromSeconds(30));
        }),
        ("Ready pending work drains one source at a time", async () =>
        {
            var backend = Backend(1, 2, 3);
            var clock = new MillisecondClock();
            var coordinator = new WindowCoordinator(backend, clock);

            Check(!coordinator.HasReadyPending && coordinator.NextPendingDelay is null);
            await coordinator.PollAsync();
            Check(backend.Calls.Count == 1 && coordinator.PendingCount == 1);
            Check(coordinator.HasReadyPending && coordinator.NextPendingDelay == TimeSpan.Zero);

            await coordinator.PollAsync();
            Check(backend.Calls.Count == 2 && coordinator.PendingCount == 0);
            Check(!coordinator.HasReadyPending && coordinator.NextPendingDelay is null);
        }),
        ("A ready source is not blocked by an earlier cooling source", async () =>
        {
            var backend = Backend(1, 2, 3);
            backend.Transfer = (source, _, _) => Task.FromResult(
                source == Id(2) ? TransferResult.Retry : TransferResult.Completed);
            var coordinator = new WindowCoordinator(backend, new MillisecondClock());

            await coordinator.PollAsync();
            Check(backend.Calls.Single().Source == Id(2));
            Check(coordinator.HasReadyPending && coordinator.NextPendingDelay == TimeSpan.Zero);

            await coordinator.PollAsync();
            Check(backend.Calls.Count == 2 && backend.Calls[1].Source == Id(3));
            Check(coordinator.PendingCount == 1 && !coordinator.HasReadyPending);
            Check(coordinator.NextPendingDelay == TimeSpan.FromMilliseconds(250));
        }),
        ("Not-ready results use a short backoff capped at 100 ms", async () =>
        {
            var backend = Backend(1, 2);
            var results = new Queue<TransferResult>([
                TransferResult.NotReady, TransferResult.NotReady, TransferResult.NotReady, TransferResult.NotReady]);
            backend.Transfer = (_, _, _) => Task.FromResult(results.Dequeue());
            var clock = new MillisecondClock();
            var coordinator = new WindowCoordinator(backend, clock);
            var expectedDelays = new[] { 25, 50, 100, 100 };

            foreach (var delay in expectedDelays)
            {
                await coordinator.PollAsync();
                Check(!coordinator.HasReadyPending);
                Check(coordinator.NextPendingDelay == TimeSpan.FromMilliseconds(delay));

                clock.AdvanceMilliseconds(delay - 1);
                Check(!coordinator.HasReadyPending);
                Check(coordinator.NextPendingDelay == TimeSpan.FromMilliseconds(1));
                clock.AdvanceMilliseconds(1);
                Check(coordinator.HasReadyPending && coordinator.NextPendingDelay == TimeSpan.Zero);
            }

            Check(backend.Calls.Count == expectedDelays.Length && coordinator.PendingCount == 1);
        }),
        ("A newly ready source is retried within 100 ms without a new window event", async () =>
        {
            var backend = Backend(1, 2);
            var clock = new MillisecondClock();
            var elapsed = 0;
            backend.Transfer = (_, _, _) => Task.FromResult(
                elapsed >= 130 ? TransferResult.Completed : TransferResult.NotReady);
            var coordinator = new WindowCoordinator(backend, clock);
            int? completedAt = null;
            for (; elapsed <= 230; elapsed += 5)
            {
                await coordinator.PollAsync();
                if (coordinator.PendingCount == 0) { completedAt = elapsed; break; }
                clock.AdvanceMilliseconds(5);
            }
            Check(completedAt.HasValue && completedAt.Value >= 130 && completedAt.Value - 130 <= 100);
            Check(backend.Windows.Count == 1);
        }),
        ("Normal retry starts at 250 ms and caps at 4 seconds", async () =>
        {
            var backend = Backend(1, 2);
            var retries = new Queue<TransferResult>(Enumerable.Repeat(TransferResult.Retry, 6));
            backend.Transfer = (_, _, _) => Task.FromResult(retries.Dequeue());
            var clock = new MillisecondClock();
            var coordinator = new WindowCoordinator(backend, clock);
            var expectedDelays = new[] { 250, 500, 1000, 2000, 4000, 4000 };

            foreach (var delay in expectedDelays)
            {
                await coordinator.PollAsync();
                Check(!coordinator.HasReadyPending);
                Check(coordinator.NextPendingDelay == TimeSpan.FromMilliseconds(delay));
                clock.AdvanceMilliseconds(delay);
                Check(coordinator.HasReadyPending && coordinator.NextPendingDelay == TimeSpan.Zero);
            }

            Check(backend.Calls.Count == expectedDelays.Length && coordinator.PendingCount == 1);
        }),
        ("Unsafe results retain the 30-second cooldown", async () =>
        {
            var backend = Backend(1, 2);
            backend.Transfer = (_, _, _) => Task.FromResult(TransferResult.Unsafe);
            var clock = new MillisecondClock();
            var coordinator = new WindowCoordinator(backend, clock);

            await coordinator.PollAsync();
            Check(coordinator.NextPendingDelay == TimeSpan.FromSeconds(30));
            clock.AdvanceMilliseconds(29_999);
            Check(!coordinator.HasReadyPending && coordinator.NextPendingDelay == TimeSpan.FromMilliseconds(1));
            await coordinator.PollAsync();
            Check(backend.Calls.Count == 1);
            clock.AdvanceMilliseconds(1);
            Check(coordinator.HasReadyPending);
            await coordinator.PollAsync();
            Check(backend.Calls.Count == 2 && coordinator.NextPendingDelay == TimeSpan.FromSeconds(30));
        }),
        ("LastTransfer reports the exact source and each backend result", async () =>
        {
            foreach (var result in new[]
                     {
                         TransferResult.Completed,
                         TransferResult.SourceGone,
                         TransferResult.Retry,
                         TransferResult.Unsafe,
                         TransferResult.NotReady
                     })
            {
                var backend = Backend(1, 2);
                backend.Transfer = (_, _, _) => Task.FromResult(result);
                var coordinator = new WindowCoordinator(backend, new MillisecondClock());

                await coordinator.PollAsync();

                Check(coordinator.LastTransfer is { } observed &&
                      observed.Source == Id(2) && observed.Result == result);
            }
        }),
        ("LastTransfer clears on the next idle or cooling poll", async () =>
        {
            var idleBackend = Backend(1, 2);
            var idleCoordinator = new WindowCoordinator(idleBackend, new MillisecondClock());
            await idleCoordinator.PollAsync();
            Check(idleCoordinator.LastTransfer is { Result: TransferResult.Completed });
            await idleCoordinator.PollAsync();
            Check(idleCoordinator.LastTransfer is null);

            var coolingBackend = Backend(1, 2);
            coolingBackend.Transfer = (_, _, _) => Task.FromResult(TransferResult.Retry);
            var coolingCoordinator = new WindowCoordinator(coolingBackend, new MillisecondClock());
            await coolingCoordinator.PollAsync();
            Check(coolingCoordinator.LastTransfer is { Result: TransferResult.Retry });
            await coolingCoordinator.PollAsync();
            Check(coolingCoordinator.LastTransfer is null && coolingBackend.Calls.Count == 1);
        }),
        ("An enumeration failure clears the prior source outcome", async () =>
        {
            var backend = Backend(1, 2, 3);
            backend.Transfer = (source, _, _) => Task.FromResult(
                source == Id(2) ? TransferResult.Retry : TransferResult.Completed);
            var coordinator = new WindowCoordinator(backend, new MillisecondClock());
            await coordinator.PollAsync();
            Check(coordinator.LastTransfer is { Source: var source, Result: TransferResult.Retry } &&
                  source == Id(2));

            backend.EnumerationError = true;
            await Throws<InvalidOperationException>(() => coordinator.PollAsync());
            Check(coordinator.LastTransfer is null && backend.Calls.Count == 1);
        }),
        ("Cancellation before backend work clears the prior outcome", async () =>
        {
            var backend = Backend(1, 2);
            backend.Transfer = (_, _, _) => Task.FromResult(TransferResult.Retry);
            var coordinator = new WindowCoordinator(backend, new MillisecondClock());
            await coordinator.PollAsync();
            Check(coordinator.LastTransfer is { Result: TransferResult.Retry });

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Throws<OperationCanceledException>(() => coordinator.PollAsync(cancellation.Token));
            Check(coordinator.LastTransfer is null && backend.Calls.Count == 1);
        }),
        ("Selected source state stabilizes after three ordered samples over 80 ms", () =>
        {
            var stability = new SourceStability(0);
            var first = Stamp(10, "folder-a", "selection-a");
            var second = Stamp(20, "folder-b", "selection-b");

            Check(!stability.Observe([first, second], hasEmptySelection: false, 0));
            Check(!stability.Observe([second, first], hasEmptySelection: false, 40));
            Check(stability.Observe([first, second], hasEmptySelection: false, 80));
            return Task.CompletedTask;
        }),
        ("Empty source selection waits 320 ms before becoming stable", () =>
        {
            var stability = new SourceStability(0);
            var tabs = new[] { Stamp(10, "folder-a", "empty") };

            for (var time = 0; time < 320; time += SourceStability.SampleIntervalMilliseconds)
                Check(!stability.Observe(tabs, hasEmptySelection: true, time));
            Check(!stability.Observe(tabs, hasEmptySelection: true, 319));
            Check(stability.Observe(tabs, hasEmptySelection: true, 320));
            return Task.CompletedTask;
        }),
        ("Folder, selection, and tab-set changes restart the quiet interval", () =>
        {
            CheckChangeResetsQuietPeriod(
                [Stamp(10, "folder-a", "selection-a")],
                [Stamp(10, "folder-b", "selection-a")]);
            CheckChangeResetsQuietPeriod(
                [Stamp(10, "folder-a", "selection-a")],
                [Stamp(10, "folder-a", "selection-b")]);
            CheckChangeResetsQuietPeriod(
                [Stamp(10, "folder-a", "selection-a")],
                [Stamp(10, "folder-a", "selection-a"), Stamp(20, "folder-b", "selection-b")]);
            return Task.CompletedTask;
        }),
        ("Changing empty-selection state restarts the quiet interval", () =>
        {
            var stability = new SourceStability(0);
            var tabs = new[] { Stamp(10, "folder-a", "selection-a") };
            Check(!stability.Observe(tabs, hasEmptySelection: false, 0));
            Check(!stability.Observe(tabs, hasEmptySelection: false, 40));
            Check(stability.Observe(tabs, hasEmptySelection: false, 80));
            Check(!stability.Observe(tabs, hasEmptySelection: true, 120));
            Check(!stability.Observe(tabs, hasEmptySelection: true, 280));
            Check(stability.Observe(tabs, hasEmptySelection: true, 440));
            return Task.CompletedTask;
        }),
        ("Empty or duplicate source snapshots never become ready", () =>
        {
            var stability = new SourceStability(0);
            var tab = Stamp(10, "folder-a", "selection-a");
            Check(!stability.Observe([], hasEmptySelection: false, 0));
            Check(!stability.Observe([tab, tab], hasEmptySelection: false, 40));
            Check(!stability.Observe([tab], hasEmptySelection: false, 80));
            Check(!stability.Observe([tab], hasEmptySelection: false, 120));
            Check(stability.Observe([tab], hasEmptySelection: false, 160));
            return Task.CompletedTask;
        }),
        ("Backward sample time resets stability and observation expires at 800 ms", () =>
        {
            var stability = new SourceStability(100);
            var tab = Stamp(10, "folder-a", "selection-a");
            Check(!stability.Observe([tab], hasEmptySelection: false, 100));
            Check(!stability.Observe([tab], hasEmptySelection: false, 140));
            Check(!stability.Observe([tab], hasEmptySelection: false, 130));
            Check(!stability.Observe([tab], hasEmptySelection: false, 170));
            Check(!stability.Observe([tab], hasEmptySelection: false, 210));
            Check(stability.Observe([tab], hasEmptySelection: false, 250));
            Check(!stability.IsExpired(899));
            Check(stability.IsExpired(900));
            Check(!stability.Observe([tab], hasEmptySelection: false, 900));
            return Task.CompletedTask;
        })
    ];

    private static void CheckChangeResetsQuietPeriod(TabTransferStamp[] initial, TabTransferStamp[] changed)
    {
        var stability = new SourceStability(0);
        Check(!stability.Observe(initial, hasEmptySelection: false, 0));
        Check(!stability.Observe(initial, hasEmptySelection: false, 40));
        Check(stability.Observe(initial, hasEmptySelection: false, 80));
        Check(!stability.Observe(changed, hasEmptySelection: false, 120));
        Check(!stability.Observe(changed, hasEmptySelection: false, 160));
        Check(stability.Observe(changed, hasEmptySelection: false, 200));
    }

    private static FakeBackend Backend(params int[] windows) => new()
    {
        Windows = windows.Select(value => new ExplorerWindow(Id(value), 1)).ToList()
    };

    private static WindowId Id(int value) => new(value, 100);

    private sealed class HintBackend(FakeBackend inner) : IExplorerBackend, IExplorerHintBackend
    {
        public int FullDiscoveries { get; private set; }
        public int HintDiscoveries { get; private set; }
        public bool HintAvailable { get; set; } = true;
        public IReadOnlyList<ExplorerWindow> EnumerateWindows()
        {
            FullDiscoveries++;
            return inner.EnumerateWindows();
        }
        public bool TryEnumerateRoutingHint(WindowId main, out IReadOnlyList<ExplorerWindow> windows)
        {
            HintDiscoveries++;
            windows = inner.Windows.ToArray();
            return HintAvailable && windows.Any(window => window.Id == main);
        }
        public Task<TransferResult> TransferAsync(WindowId source, WindowId target, CancellationToken cancellationToken) =>
            inner.TransferAsync(source, target, cancellationToken);
    }

    private static TabTransferStamp Stamp(nint tab, string location, string selection) =>
        new(tab, location, selection);

    private static void Check(bool condition)
    {
        if (!condition)
            throw new InvalidOperationException("Latency contract assertion failed");
    }

    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name} was not thrown");
    }

    private sealed class MillisecondClock : TimeProvider
    {
        private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private long _elapsedMilliseconds;

        public override DateTimeOffset GetUtcNow() => Epoch.AddMilliseconds(_elapsedMilliseconds);

        internal void AdvanceMilliseconds(long milliseconds) => _elapsedMilliseconds += milliseconds;
    }
}
