namespace ExplorerOne.Core;

/// <summary>Serializes routing; the backend owns copying and the selected close policy.</summary>
public sealed class WindowCoordinator(IExplorerBackend backend, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<WindowId, ObservedWindow> _windows = [];
    private long _sequence;
    private int _polling;
    public WindowId? MainWindow { get; private set; }
    public bool IsBusy => Volatile.Read(ref _polling) != 0;
    public int PendingCount => Math.Max(0, _windows.Count - (MainWindow.HasValue ? 1 : 0));
    public int BackgroundPendingCount => (backend as IExplorerBackgroundBackend)?.PendingTransferCount ?? 0;
    public (WindowId Source, TransferResult Result)? LastTransfer { get; private set; }
    public bool HasReadyPending => NextPendingDelay == TimeSpan.Zero;
    public TimeSpan? NextPendingDelay
    {
        get
        {
            if (!MainWindow.HasValue) return null;
            DateTimeOffset? earliest = null;
            foreach (var (id, observed) in _windows)
                if (id != MainWindow.Value && (!earliest.HasValue || observed.RetryAt < earliest.Value))
                    earliest = observed.RetryAt;
            if (!earliest.HasValue) return null;
            var remaining = earliest.Value - _clock.GetUtcNow();
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }
    public string Status { get; private set; } = "Ожидание Проводника";

    public async Task PollAsync(CancellationToken cancellationToken = default, bool preferFastDiscovery = false)
    {
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;
        try
        {
            LastTransfer = null;
            cancellationToken.ThrowIfCancellationRequested();
            // Do not discard a main window when COM has failed or is restarting.
            IReadOnlyList<ExplorerWindow> snapshot;
            if (preferFastDiscovery && MainWindow.HasValue && backend is IExplorerHintBackend hints &&
                hints.TryEnumerateRoutingHint(MainWindow.Value, out var hinted))
                snapshot = hinted;
            else
                snapshot = backend.EnumerateWindows();
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
                    observed.ReadinessFailures = 0;
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
                    observed.ReadinessFailures = 0;
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
                Status = PendingCount != 0 ? "Окно сохранено; повтор позже" :
                    BackgroundPendingCount != 0 ? "Открытие вкладок…" : "Один Проводник";
                return;
            }

            Status = "Перенос вкладок…";
            TransferResult result;
            try
            {
                if (backend is IExplorerPreparationBackend preparation)
                {
                    // The backend receives only a source from this complete
                    // routing inventory, respecting its existing cooldown.
                    // Direct backend calls never acquire unrelated windows.
                    var candidate = snapshot.Single(window => window.Id == source.Value).TabCount == 1
                        ? _windows.Where(pair => pair.Key != MainWindow.Value && pair.Key != source.Value &&
                                pair.Value.TabCount == 1 && pair.Value.RetryAt <= now)
                            .OrderBy(pair => pair.Value.Sequence).Select(pair => (WindowId?)pair.Key).FirstOrDefault()
                        : null;
                    preparation.SetPreparationCandidate(candidate, MainWindow.Value);
                }
                result = await backend.TransferAsync(source.Value, MainWindow.Value, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                // Paths and raw COM exceptions never enter UI or persistent logs.
                result = TransferResult.Retry;
            }
            LastTransfer = (source.Value, result);
            if (result is TransferResult.Completed or TransferResult.SourceGone or TransferResult.Queued)
            {
                _windows.Remove(source.Value);
                Status = result == TransferResult.Queued ? "Открытие вкладок…" : "Перенос подтверждён";
            }
            else
            {
                var observed = _windows[source.Value];
                int milliseconds;
                if (result == TransferResult.NotReady)
                {
                    observed.ReadinessFailures = Math.Min(3, observed.ReadinessFailures + 1);
                    milliseconds = 25 << (observed.ReadinessFailures - 1);
                }
                else
                {
                    observed.ReadinessFailures = 0;
                    observed.Failures = Math.Min(5, observed.Failures + 1);
                    milliseconds = result == TransferResult.Unsafe ? 30_000 : Math.Min(4_000, 250 << (observed.Failures - 1));
                }
                observed.RetryAt = _clock.GetUtcNow().AddMilliseconds(milliseconds);
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
        public int ReadinessFailures { get; set; }
        public DateTimeOffset RetryAt { get; set; }
    }
}
