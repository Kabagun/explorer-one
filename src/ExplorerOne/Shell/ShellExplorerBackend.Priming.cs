using System.Diagnostics;
using ExplorerOne.Core;

namespace ExplorerOne.Shell;

public sealed partial class ShellExplorerBackend
{
    internal long DiagnosticCapturePrimeMilliseconds { get; private set; }
    internal int DiagnosticCapturePrimeAttempts { get; private set; }

    public bool TryPrimeFastCapture(WindowId main)
    {
        AssertContext();
        if (!_closeSourceEarly || _transferring || _backgroundJobs.Count != 0 || !IsExplorerWindow(main))
            return false;
        var startedAt = Stopwatch.GetTimestamp();
        DiagnosticCapturePrimeAttempts++;
        _transferring = true;
        try
        {
            // Read only one existing view. This warms the same COM/binder
            // path used by the first new source without allocating a tab,
            // changing selection, retaining the snapshot or closing anything.
            using var registered = ReadScopedRegisteredTabs(main);
            var generation = _generation;
            var window = registered.FindWindow(main);
            if (window.Tabs.Count == 0) return false;
            var reference = window.Tabs[0];
            ValidateReference(reference, main);
            _ = CaptureTab(reference);
            ValidateReference(reference, main);
            return generation == _generation && NativeTabsMatch(main, window.Tabs.Select(tab => tab.Handle));
        }
        catch { return false; } // Optional startup preparation; normal routing retries readiness.
        finally
        {
            _transferring = false;
            DiagnosticCapturePrimeMilliseconds += (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        }
    }
}
