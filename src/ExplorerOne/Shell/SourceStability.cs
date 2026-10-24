namespace ExplorerOne.Shell;

// Tracks strict, complete source samples; it never weakens the close guard.
// Empty selection gets a longer grace because /select can publish its folder
// before Explorer publishes the requested file selection.
internal sealed class SourceStability(long startedAtMilliseconds)
{
    internal const int SampleIntervalMilliseconds = 40;
    internal const int SelectedQuietMilliseconds = 80;
    internal const int EmptySelectionQuietMilliseconds = 320;
    internal const int MaximumObservationMilliseconds = 800;

    private readonly long _startedAt = startedAtMilliseconds;
    private TabTransferStamp[]? _previous;
    private bool _previousHasEmptySelection;
    private long _unchangedSince;
    private long _lastObservation = startedAtMilliseconds;
    private int _matchingSamples;

    internal bool Observe(IReadOnlyList<TabTransferStamp> tabs, bool hasEmptySelection, long nowMilliseconds)
    {
        if (IsExpired(nowMilliseconds) || nowMilliseconds < _lastObservation || tabs.Count == 0 ||
            tabs.Any(tab => tab.Tab == 0 || string.IsNullOrEmpty(tab.Location) || string.IsNullOrEmpty(tab.Selection)) ||
            tabs.Select(tab => tab.Tab).Distinct().Count() != tabs.Count)
        {
            _previous = null;
            _matchingSamples = 0;
            _lastObservation = nowMilliseconds;
            return false;
        }

        var unchanged = _previous is not null && _previous.Length == tabs.Count &&
            _previousHasEmptySelection == hasEmptySelection && _previous.ToHashSet().SetEquals(tabs);
        if (!unchanged)
        {
            _previous = tabs.ToArray();
            _previousHasEmptySelection = hasEmptySelection;
            _unchangedSince = nowMilliseconds;
            _matchingSamples = 1;
        }
        else if (nowMilliseconds > _lastObservation)
        {
            _matchingSamples++;
        }
        _lastObservation = nowMilliseconds;

        var quietPeriod = hasEmptySelection ? EmptySelectionQuietMilliseconds : SelectedQuietMilliseconds;
        return _matchingSamples >= 3 && nowMilliseconds - _unchangedSince >= quietPeriod;
    }

    internal bool IsExpired(long nowMilliseconds) =>
        nowMilliseconds - _startedAt >= MaximumObservationMilliseconds;
}
