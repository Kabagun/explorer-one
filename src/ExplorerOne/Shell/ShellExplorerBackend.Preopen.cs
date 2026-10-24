using ExplorerOne.Core;
using System.Runtime.InteropServices;

namespace ExplorerOne.Shell;

public sealed partial class ShellExplorerBackend
{
    private readonly NativeTabReservations _nativeReservations;
    private readonly bool _ownsNativeReservations;
    private readonly Dictionary<long, NativeTabCleanupCandidate> _preopenCleanup = [];
    private readonly HashSet<long> _preopenCleanupCloseIssued = [];
    private bool _nativeReservationsDisposed;
    private long _lastPreopenCleanupRequest;

    internal int DiagnosticEarlyTabAdoptions { get; private set; }
    internal int DiagnosticPreopenCleanupClosed { get; private set; }
    internal int DiagnosticPreopenCleanupPreserved { get; private set; }

    private void CompleteNativeReservation(PartialTransfer transfer)
    {
        if (transfer.NativeReservation is not null)
            _nativeReservations.Complete(transfer.NativeReservation);
    }

    private void AbandonNativeReservation(PartialTransfer transfer)
    {
        if (transfer.NativeReservation is not null && !transfer.Verified)
            _nativeReservations.Abandon(transfer.NativeReservation);
    }

    private void AbandonUnclaimedPreopen(WindowId source, WindowId target)
    {
        if (_partialTransfers.Any(transfer => transfer.Source == source))
            return; // Retained transfers, including cancelled ones, keep their exact tab for retry.
        var early = _nativeReservations.ClaimEarly(source, target, new object());
        if (early is not null)
            _nativeReservations.Abandon(early);
    }

    /// <summary>Owner-STA maintenance; never called from the native event watcher.</summary>
    public async Task ProcessPreopenCleanupAsync()
    {
        AssertContext();
        if (_transferring)
            return;
        _transferring = true;
        try
        {
            _nativeReservations.Pulse();
            foreach (var candidate in _nativeReservations.TakeCleanupCandidates())
                if (_preopenCleanup.Count < MaximumRetainedTransfers)
                    _preopenCleanup.TryAdd(candidate.RequestId, candidate);

            // Rotate one candidate per maintenance turn so an unreadable or
            // slow closing tab cannot starve other cleanup candidates.
            var next = _preopenCleanup.Values.FirstOrDefault(candidate => candidate.RequestId > _lastPreopenCleanupRequest)
                ?? _preopenCleanup.Values.FirstOrDefault();
            if (next is not null)
                _lastPreopenCleanupRequest = next.RequestId;
            foreach (var candidate in next is null ? Array.Empty<NativeTabCleanupCandidate>() : [next])
            {
                if (!NativeTabBelongsTo(candidate.Target, candidate.CreatedTab))
                {
                    if (_preopenCleanupCloseIssued.Contains(candidate.RequestId))
                        DiagnosticPreopenCleanupClosed++;
                    ForgetPreopenCleanup(candidate.RequestId);
                    continue;
                }
                if (_preopenCleanupCloseIssued.Contains(candidate.RequestId))
                    continue; // A timed-out close might still be delivered; never post it twice.
                try
                {
                    RefreshShell();
                    var generation = _generation;
                    if (!CanCleanPreopen(candidate))
                    {
                        PreservePreopen(candidate.RequestId);
                        continue;
                    }
                    using var reference = FindRegisteredTab(candidate.Target, candidate.CreatedTab, generation);
                    if (reference is null)
                        continue; // Registration/loading may settle on a later maintenance turn.
                    ValidateReference(reference, candidate.Target);
                    var first = CaptureTab(reference);
                    if (!IsEmptyHome(first))
                    {
                        PreservePreopen(candidate.RequestId);
                        continue;
                    }
                    await Task.Yield();
                    ValidateReference(reference, candidate.Target);
                    var second = CaptureTab(reference);
                    if (generation != _generation || first.Stamp != second.Stamp || !IsEmptyHome(second) ||
                        !CanCleanPreopen(candidate))
                    {
                        PreservePreopen(candidate.RequestId);
                        continue;
                    }

                    // Adapted from ExplorerTabUtility's active-tab Ctrl+W.
                    // Never send Quit/WM_CLOSE to this multi-tab target frame.
                    _preopenCleanupCloseIssued.Add(candidate.RequestId);
                    _ = ShellNative.SendMessageTimeout(candidate.CreatedTab, ShellNative.WmCommand,
                        ShellNative.CloseTabCommand, 1, ShellNative.AbortIfHung | ShellNative.ErrorOnExit, 250, out _);
                    if (!NativeTabBelongsTo(candidate.Target, candidate.CreatedTab))
                    {
                        DiagnosticPreopenCleanupClosed++;
                        ForgetPreopenCleanup(candidate.RequestId);
                    }
                }
                catch (ShellSnapshotException) { /* Keep an unreadable/loading candidate without closing it. */ }
                catch { PreservePreopen(candidate.RequestId); }
            }
        }
        finally { _transferring = false; }
    }

    private bool CanCleanPreopen(NativeTabCleanupCandidate candidate)
    {
        ShellNative.GetWindowThreadProcessId(ShellNative.GetShellWindow(), out var shellProcess);
        if (shellProcess == 0 || shellProcess != candidate.ShellProcessId || shellProcess != _shellProcess ||
            !NativeTabBelongsTo(candidate.Target, candidate.CreatedTab) || candidate.BeforeTabs.Length == 0 ||
            ShellNative.FindWindowEx(candidate.Target.Handle, 0, "ShellTabWindowClass", null) != candidate.CreatedTab)
            return false;
        var expected = candidate.BeforeTabs.Append(candidate.CreatedTab).ToHashSet();
        return expected.Count > 1 && ReadNativeTabs(candidate.Target).ToHashSet().SetEquals(expected) &&
            FocusConfirmsPreopen(candidate.Target, candidate.CreatedTab);
    }

    private static bool FocusConfirmsPreopen(WindowId target, nint tab)
    {
        var thread = ShellNative.GetWindowThreadProcessId(target.Handle, out var process);
        var information = new ShellNative.GuiThreadInfo { Size = (uint)Marshal.SizeOf<ShellNative.GuiThreadInfo>() };
        if (thread == 0 || process != target.ProcessId || !ShellNative.GetGUIThreadInfo(thread, ref information) ||
            ShellNative.GetAncestor(information.Active, ShellNative.RootAncestor) != target.Handle || information.Focus == 0 ||
            ShellNative.GetAncestor(information.Focus, ShellNative.RootAncestor) != target.Handle)
            return false;
        var current = information.Focus;
        var found = false;
        for (var count = 0; current != 0 && current != target.Handle && count < 64;
            count++, current = ShellNative.GetParent(current))
            found |= current == tab;
        if (current != target.Handle || !found ||
            ShellNative.GetWindowThreadProcessId(tab, out process) != thread || process != target.ProcessId)
            return false;
        var again = new ShellNative.GuiThreadInfo { Size = information.Size };
        return ShellNative.GetGUIThreadInfo(thread, ref again) && again.Active == information.Active &&
            again.Focus == information.Focus && NativeTabBelongsTo(target, tab) &&
            ShellNative.FindWindowEx(target.Handle, 0, "ShellTabWindowClass", null) == tab;
    }

    private bool IsEmptyHome(TabState state) => state.Selection.Length == 0 &&
        _paths!.Equivalent(state.Location, "shell:::{F874310E-B6B7-47DC-BC84-B9E6B38F5903}");

    private void PreservePreopen(long requestId)
    {
        DiagnosticPreopenCleanupPreserved++;
        ForgetPreopenCleanup(requestId); // Unknown/user-modified tabs are deliberately retained.
    }

    private void ForgetPreopenCleanup(long requestId)
    {
        _preopenCleanup.Remove(requestId);
        _preopenCleanupCloseIssued.Remove(requestId);
    }

    private void DisposeOwnedNativeReservations()
    {
        if (!_ownsNativeReservations || _nativeReservationsDisposed)
            return;
        _nativeReservationsDisposed = true;
        _nativeReservations.Dispose();
    }
}
