namespace ExplorerOne.Core;

/// <summary>Serializes routing. It never closes windows; the backend owns the verified transfer.</summary>
public sealed class WindowCoordinator(IExplorerBackend backend, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<WindowId, ObservedWindow> _windows = [];
    private long _sequence;
    private int _polling;
    public WindowId? MainWindow { get; private set; }
    public bool IsBusy => Volatile.Read(ref _polling) != 0;
    public int PendingCount => Math.Max(0, _windows.Count - (MainWindow.HasValue ? 1 : 0));
    public string Status { get; private set; } = "Ожидание Проводника";

    public async Task PollAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Do not discard a main window when COM has failed or is restarting.
            var snapshot = backend.EnumerateWindows();
            var live = snapshot.Select(w => w.Id).ToHashSet();
            foreach (var gone in _windows.Keys.Where(id => !live.Contains(id)).ToArray())
                _windows.Remove(gone);
            foreach (var window in snapshot)
            {
                if (!_windows.TryGetValue(window.Id, out var observed))
                    _windows.Add(window.Id, new(++_sequence, window.TabCount));
                else if (observed.TabCount != window.TabCount)
                {
                    observed.TabCount = window.TabCount;
                    observed.RetryAt = DateTimeOffset.MinValue;
                    observed.Failures = 0;
                }
            }

            if (!MainWindow.HasValue || !live.Contains(MainWindow.Value))
            {
                // At startup preserve the window with most tabs; thereafter the
                // already chosen main remains sticky until it actually disappears.
                MainWindow = snapshot.OrderByDescending(w => w.TabCount)
                    .ThenBy(w => _windows[w.Id].Sequence).Select(w => (WindowId?)w.Id).FirstOrDefault();
                foreach (var observed in _windows.Values)
                {
                    observed.RetryAt = DateTimeOffset.MinValue;
                    observed.Failures = 0;
                }
            }
            if (!MainWindow.HasValue)
            {
                Status = "Ожидание Проводника";
                return;
            }

            var now = _clock.GetUtcNow();
            var source = _windows.Where(pair => pair.Key != MainWindow.Value && pair.Value.RetryAt <= now)
                .OrderBy(pair => pair.Value.Sequence).Select(pair => (WindowId?)pair.Key).FirstOrDefault();
            if (!source.HasValue)
            {
                Status = PendingCount == 0 ? "Один Проводник" : "Окно сохранено; повтор позже";
                return;
            }

            Status = "Перенос вкладок…";
            TransferResult result;
            try
            {
                result = await backend.TransferAsync(source.Value, MainWindow.Value, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                // Paths and raw COM exceptions never enter UI or persistent logs.
                result = TransferResult.Retry;
            }
            if (result is TransferResult.Completed or TransferResult.SourceGone)
            {
                _windows.Remove(source.Value);
                Status = "Перенос подтверждён";
            }
            else
            {
                var observed = _windows[source.Value];
                observed.Failures = Math.Min(5, observed.Failures + 1);
                var seconds = result == TransferResult.Unsafe ? 30 : Math.Min(30, 1 << observed.Failures);
                observed.RetryAt = _clock.GetUtcNow().AddSeconds(seconds);
                Status = "Окно сохранено; повтор позже";
            }
        }
        finally { Volatile.Write(ref _polling, 0); }
    }

    private sealed class ObservedWindow(long sequence, int tabCount)
    {
        public long Sequence { get; } = sequence;
        public int TabCount { get; set; } = tabCount;
        public int Failures { get; set; }
        public DateTimeOffset RetryAt { get; set; }
    }
}
