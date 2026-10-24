using ExplorerOne.Core;

var cases = new (string Name, Func<Task> Run)[]
{
    ("No windows does not open Explorer", async () =>
    {
        var b = new FakeBackend(); var c = new WindowCoordinator(b);
        await c.PollAsync(); Check(c.MainWindow is null && b.Calls.Count == 0);
    }),
    ("First observed window becomes main", async () =>
    {
        var b = Backend(1); var c = new WindowCoordinator(b);
        await c.PollAsync(); Check(c.MainWindow == Id(1) && b.Calls.Count == 0);
    }),
    ("Startup preserves window with most tabs", async () =>
    {
        var b = Backend(1, 2); b.Windows[1] = new(Id(2), 4);
        var c = new WindowCoordinator(b); await c.PollAsync();
        Check(c.MainWindow == Id(2) && b.Calls.Single() == (Id(1), Id(2)));
    }),
    ("Registration order breaks startup ties", async () =>
    {
        var b = Backend(9, 3); var c = new WindowCoordinator(b); await c.PollAsync();
        Check(c.MainWindow == Id(9));
    }),
    ("Main remains sticky when new window has more tabs", async () =>
    {
        var b = Backend(1); var c = new WindowCoordinator(b); await c.PollAsync();
        b.Windows.Insert(0, new(Id(2), 5)); await c.PollAsync();
        Check(c.MainWindow == Id(1) && b.Calls.Single().Source == Id(2));
    }),
    ("Closed main is replaced", async () =>
    {
        var b = Backend(1); var c = new WindowCoordinator(b); await c.PollAsync();
        b.Windows.Clear(); b.Windows.Add(new(Id(2), 1)); await c.PollAsync();
        Check(c.MainWindow == Id(2) && b.Calls.Count == 0);
    }),
    ("Burst of new windows drains exactly once", async () =>
    {
        var b = Backend(1, 2, 3, 4, 5, 6); var c = new WindowCoordinator(b);
        for (var i = 0; i < 8; i++) await c.PollAsync();
        Check(b.Calls.Count == 5 && b.Calls.All(x => x.Target == Id(1)) && c.PendingCount == 0);
        Check(b.Calls.Select(x => x.Source).Distinct().Count() == 5);
    }),
    ("Overlapping polls cannot launch concurrent transfers", async () =>
    {
        var b = Backend(1, 2, 3); var gate = new TaskCompletionSource<TransferResult>();
        b.Transfer = (_, _, _) => gate.Task; var c = new WindowCoordinator(b);
        var first = c.PollAsync(); Check(c.IsBusy);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => c.PollAsync()));
        Check(b.Calls.Count == 1); gate.SetResult(TransferResult.Retry); await first;
        Check(!c.IsBusy);
    }),
    ("Windows arriving during a transfer are observed next poll", async () =>
    {
        var b = Backend(1, 2); var gate = new TaskCompletionSource<TransferResult>();
        b.Transfer = (_, _, _) => gate.Task; var c = new WindowCoordinator(b);
        var first = c.PollAsync(); b.Windows.Add(new(Id(3), 1));
        gate.SetResult(TransferResult.Completed); await first;
        b.Transfer = null; await c.PollAsync(); Check(b.Calls.Count == 2 && b.Windows.Count == 1);
    }),
    ("Transient failure retains source and backs off", async () =>
    {
        var b = Backend(1, 2); b.Transfer = (_, _, _) => Task.FromResult(TransferResult.Retry);
        var time = new FakeClock(); var c = new WindowCoordinator(b, time);
        await c.PollAsync(); await c.PollAsync();
        Check(b.Windows.Count == 2 && b.Calls.Count == 1 && c.PendingCount == 1);
        time.Advance(2); await c.PollAsync(); Check(b.Calls.Count == 2);
    }),
    ("Unsafe transfer stays visible and waits thirty seconds", async () =>
    {
        var b = Backend(1, 2); b.Transfer = (_, _, _) => Task.FromResult(TransferResult.Unsafe);
        var time = new FakeClock(); var c = new WindowCoordinator(b, time);
        await c.PollAsync(); time.Advance(29); await c.PollAsync(); Check(b.Calls.Count == 1);
        time.Advance(1); await c.PollAsync(); Check(b.Calls.Count == 2 && b.Windows.Count == 2);
    }),
    ("A failed source cannot starve the next source", async () =>
    {
        var b = Backend(1, 2, 3); b.Transfer = (s, _, _) => Task.FromResult(s == Id(2) ? TransferResult.Retry : TransferResult.Completed);
        var c = new WindowCoordinator(b, new FakeClock()); await c.PollAsync(); await c.PollAsync();
        Check(b.Calls.Count == 2 && b.Calls[1].Source == Id(3));
    }),
    ("Transfer exception is safely retried without leaking message", async () =>
    {
        var b = Backend(1, 2); b.Transfer = (_, _, _) => throw new InvalidOperationException("sensitive test sentinel");
        var c = new WindowCoordinator(b); await c.PollAsync();
        Check(b.Windows.Count == 2 && !c.IsBusy && !c.Status.Contains("sentinel"));
    }),
    ("Incomplete enumeration preserves current main", async () =>
    {
        var b = Backend(1); var c = new WindowCoordinator(b); await c.PollAsync();
        b.EnumerationError = true;
        await Throws<InvalidOperationException>(() => c.PollAsync());
        Check(c.MainWindow == Id(1) && !c.IsBusy && b.Calls.Count == 0);
        b.EnumerationError = false; b.Windows.Add(new(Id(2), 1)); await c.PollAsync(); Check(b.Calls.Count == 1);
    }),
    ("Cancellation before enumeration has no side effects", async () =>
    {
        var b = Backend(1, 2); var c = new WindowCoordinator(b);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Throws<OperationCanceledException>(() => c.PollAsync(stop.Token));
        Check(b.Calls.Count == 0 && !c.IsBusy && c.MainWindow is null);
    }),
    ("Cancellation during transfer releases serialization", async () =>
    {
        var b = Backend(1, 2); b.Transfer = async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return TransferResult.Completed; };
        var c = new WindowCoordinator(b); using var stop = new CancellationTokenSource();
        var poll = c.PollAsync(stop.Token); stop.Cancel();
        await Throws<OperationCanceledException>(() => poll); Check(!c.IsBusy && b.Windows.Count == 2);
        b.Transfer = null; await c.PollAsync(); Check(b.Windows.Count == 1);
    }),
    ("Recycled HWND in another process is a new main", async () =>
    {
        var b = Backend(1); var c = new WindowCoordinator(b); await c.PollAsync();
        var replacement = new WindowId(1, 999); b.Windows[0] = new(replacement, 1);
        await c.PollAsync(); Check(c.MainWindow == replacement && b.Calls.Count == 0);
    }),
    ("Tab count change clears a source cooldown", async () =>
    {
        var b = Backend(1, 2); b.Transfer = (_, _, _) => Task.FromResult(TransferResult.Unsafe);
        var c = new WindowCoordinator(b, new FakeClock()); await c.PollAsync();
        b.Windows[1] = new(Id(2), 2); await c.PollAsync(); Check(b.Calls.Count == 2);
    }),
    ("Main replacement clears stale target cooldown", async () =>
    {
        var b = Backend(1, 2, 3); b.Transfer = (_, _, _) => Task.FromResult(TransferResult.Unsafe);
        var c = new WindowCoordinator(b, new FakeClock()); await c.PollAsync();
        b.Windows.RemoveAll(w => w.Id == Id(1)); await c.PollAsync();
        Check(c.MainWindow == Id(2) && b.Calls.Last() == (Id(3), Id(2)));
    }),
    ("Shell restart forgets windows without restoring them", async () =>
    {
        var b = Backend(1); var c = new WindowCoordinator(b); await c.PollAsync();
        b.Windows.Clear(); await c.PollAsync(); Check(c.MainWindow is null && c.PendingCount == 0);
        b.Windows.Add(new(new(20, 200), 1)); await c.PollAsync(); Check(c.MainWindow == new WindowId(20, 200));
    }),
    ("Queued sources release routing while background copies remain pending", async () =>
    {
        var b = Backend(1, 2, 3);
        b.Transfer = (_, _, _) => Task.FromResult(TransferResult.Queued);
        b.PendingTransferCount = 2;
        var c = new WindowCoordinator(b);
        await c.PollAsync();
        Check(c.PendingCount == 1 && c.BackgroundPendingCount == 2 && !c.IsBusy);
        Check(c.Status == "Открытие вкладок…" && c.LastTransfer?.Result == TransferResult.Queued);
        await c.PollAsync();
        Check(b.Calls.Count == 2 && c.PendingCount == 0 && c.MainWindow == Id(1));
        Check(c.NextPendingDelay is null);
        await c.PollAsync();
        Check(c.Status == "Открытие вкладок…" && b.Calls.Count == 2);
        b.PendingTransferCount = 0;
        await c.PollAsync();
        Check(c.Status == "Один Проводник");
    }),
    ("A queued source still visible is observed again without losing the main", async () =>
    {
        var b = Backend(1, 2);
        b.KeepQueuedSourceVisible = true;
        b.Transfer = (_, _, _) => Task.FromResult(TransferResult.Queued);
        var c = new WindowCoordinator(b, new FakeClock());
        await c.PollAsync();
        b.Transfer = (_, _, _) => Task.FromResult(TransferResult.NotReady);
        await c.PollAsync();
        Check(c.MainWindow == Id(1) && c.PendingCount == 1 && b.Calls.Count == 2);
        Check(c.NextPendingDelay == TimeSpan.FromMilliseconds(25));
    }),
    ("Preparation is limited to the next ready observed single-tab source", async () =>
    {
        var inner = Backend(1, 2, 3, 4);
        inner.Windows[0] = new(Id(1), 3);
        inner.Windows[2] = new(Id(3), 2);
        var b = new PreparingBackend(inner);
        var c = new WindowCoordinator(b);
        await c.PollAsync();
        Check(inner.Calls.Single() == (Id(2), Id(1)));
        Check(b.Plans.Single() == (Id(4), Id(1)));
        Check(inner.Windows.Any(window => window.Id == Id(4)));
    }),
    ("Preparation excludes a source in cooldown and clears an absent plan", async () =>
    {
        var inner = Backend(1, 2, 3);
        inner.Transfer = (source, _, _) => Task.FromResult(source == Id(2) ? TransferResult.Unsafe : TransferResult.Completed);
        var b = new PreparingBackend(inner);
        var c = new WindowCoordinator(b, new FakeClock());
        await c.PollAsync();
        await c.PollAsync();
        Check(b.Plans.Count == 2 && b.Plans[0] == (Id(3), Id(1)) && b.Plans[1] == (null, Id(1)));
        Check(inner.Calls.Select(call => call.Source).SequenceEqual([Id(2), Id(3)]));
    }),
    ("A multi-tab source does not enable speculative preparation", async () =>
    {
        var inner = Backend(1, 2, 3);
        inner.Windows[0] = new(Id(1), 3);
        inner.Windows[1] = new(Id(2), 2);
        var b = new PreparingBackend(inner);
        var c = new WindowCoordinator(b);
        await c.PollAsync();
        Check(b.Plans.Single() == (null, Id(1)) && inner.Calls.Single().Source == Id(2));
    })
};
cases = cases.Concat(SafetyCases.All).ToArray();
cases = cases.Concat(LocationCases.All).ToArray();
cases = cases.Concat(LatencyCases.All).ToArray();
cases = cases.Concat(PresentationCases.All).ToArray();
var failures = 0;
foreach (var test in cases)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL {test.Name}: {ex.GetType().Name}"); }
}
Console.WriteLine($"{cases.Length - failures}/{cases.Length} passed");
return failures == 0 ? 0 : 1;

static WindowId Id(int value) => new(value, 100);
static FakeBackend Backend(params int[] values) => new() { Windows = values.Select(v => new ExplorerWindow(Id(v), 1)).ToList() };
static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed"); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new InvalidOperationException("Expected exception was not thrown");
}

sealed class FakeClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
}
sealed class FakeBackend : IExplorerBackend, IExplorerBackgroundBackend
{
    public int PendingTransferCount { get; set; }
    public int FailedTransferCount { get; set; }
    public bool KeepQueuedSourceVisible { get; set; }
    public List<ExplorerWindow> Windows { get; init; } = [];
    public List<(WindowId Source, WindowId Target)> Calls { get; } = [];
    public Func<WindowId, WindowId, CancellationToken, Task<TransferResult>>? Transfer { get; set; }
    public bool EnumerationError { get; set; }
    public IReadOnlyList<ExplorerWindow> EnumerateWindows() => EnumerationError ? throw new InvalidOperationException() : Windows.ToArray();
    public async Task<TransferResult> TransferAsync(WindowId source, WindowId target, CancellationToken ct)
    {
        if (source == target || !Windows.Any(w => w.Id == source) || !Windows.Any(w => w.Id == target))
            throw new InvalidOperationException("Invalid transfer");
        Calls.Add((source, target));
        var result = Transfer is null ? TransferResult.Completed : await Transfer(source, target, ct);
        if (result is TransferResult.Completed or TransferResult.SourceGone ||
            (result == TransferResult.Queued && !KeepQueuedSourceVisible)) Windows.RemoveAll(w => w.Id == source);
        return result;
    }
}

sealed class PreparingBackend(FakeBackend inner) : IExplorerBackend, IExplorerPreparationBackend
{
    public List<(WindowId? Candidate, WindowId Target)> Plans { get; } = [];
    public IReadOnlyList<ExplorerWindow> EnumerateWindows() => inner.EnumerateWindows();
    public Task<TransferResult> TransferAsync(WindowId source, WindowId target, CancellationToken cancellationToken) =>
        inner.TransferAsync(source, target, cancellationToken);
    public void SetPreparationCandidate(WindowId? candidate, WindowId target) => Plans.Add((candidate, target));
}
