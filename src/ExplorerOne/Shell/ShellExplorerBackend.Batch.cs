using System.Diagnostics;
using System.Globalization;
using ExplorerOne.Core;

namespace ExplorerOne.Shell;

public sealed partial class ShellExplorerBackend
{
    private WindowId? _nextPreparationCandidate;
    private WindowId _nextPreparationTarget;
    private PreparedOtherSource? _preparedOtherSource;

    internal int DiagnosticPreparationAttempts { get; private set; }
    internal int DiagnosticPreparedOtherSources { get; private set; }
    internal int DiagnosticPreparedCacheReuse { get; private set; }
    internal int DiagnosticPreparedCacheMiss { get; private set; }
    internal int DiagnosticPreparationFailures { get; private set; }
    internal int DiagnosticPreparationCancelledOnPrimaryFinish { get; private set; }
    internal int DiagnosticPreviousPreparationCancelledOnPrimaryFinish { get; private set; }
    internal long DiagnosticPreparationMilliseconds { get; private set; }
    internal long DiagnosticAllocationMilliseconds { get; private set; }
    internal long DiagnosticCandidateRegistryMilliseconds { get; private set; }
    internal long DiagnosticCandidateCaptureMilliseconds { get; private set; }
    internal long DiagnosticPreviousCandidateRegistryMilliseconds { get; private set; }
    internal long DiagnosticPreviousCandidateCaptureMilliseconds { get; private set; }

    public void SetPreparationCandidate(WindowId? candidate, WindowId target)
    {
        AssertContext();
        _nextPreparationCandidate = candidate;
        _nextPreparationTarget = target;
    }

    private WindowId? TakePreparationCandidate(WindowId source, WindowId target)
    {
        var candidate = _nextPreparationCandidate;
        var plannedTarget = _nextPreparationTarget;
        _nextPreparationCandidate = null;
        _nextPreparationTarget = default;
        return plannedTarget == target && candidate.HasValue && candidate != source && candidate != target
            ? candidate : null;
    }

    private void ResetPreparationDiagnostics()
    {
        DiagnosticPreviousCandidateRegistryMilliseconds = DiagnosticCandidateRegistryMilliseconds;
        DiagnosticPreviousCandidateCaptureMilliseconds = DiagnosticCandidateCaptureMilliseconds;
        DiagnosticPreviousPreparationCancelledOnPrimaryFinish = DiagnosticPreparationCancelledOnPrimaryFinish;
        DiagnosticPreparationAttempts = 0;
        DiagnosticPreparedOtherSources = 0;
        DiagnosticPreparedCacheReuse = 0;
        DiagnosticPreparedCacheMiss = 0;
        DiagnosticPreparationFailures = 0;
        DiagnosticPreparationCancelledOnPrimaryFinish = 0;
        DiagnosticPreparationMilliseconds = 0;
        DiagnosticAllocationMilliseconds = 0;
        DiagnosticCandidateRegistryMilliseconds = 0;
        DiagnosticCandidateCaptureMilliseconds = 0;
    }

    private async Task<WindowState?> PrepareWithCandidateAsync(RegisteredWindow source, WindowState provisional,
        long provisionalObservedAt, PartialTransfer transfer, int generation, long observationStartedAt,
        WindowId? candidate, CancellationToken cancellationToken)
    {
        // Obtain B's optional complete source lease BEFORE A can post Ctrl+T.
        // Enumerating ShellWindows while an unrelated Home view initializes
        // can synchronously block that registry scan on Explorer's UI thread.
        using var captured = candidate.HasValue
            ? TryCaptureCandidate(candidate.Value, transfer.Target, generation, cancellationToken) : null;
        if (captured is null)
            return await PrepareSingleTabAsync(source, provisional, provisionalObservedAt, transfer,
                generation, observationStartedAt, cancellationToken);

        using var transaction = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // A begins first. Both requests and every retained COM reference remain
        // inside this public transfer; B is never closed during preparation.
        var primary = PrepareSingleTabAsync(source, provisional, provisionalObservedAt, transfer,
            generation, observationStartedAt, transaction.Token);
        var other = PrepareOtherSourceAsync(captured, transfer.Target, generation, transaction.Token);
        try
        {
            if (await Task.WhenAny(primary, other) == other)
                await other; // Only native allocation ambiguity escapes warm-up.
            var original = await primary;
            if (!other.IsCompleted)
                DiagnosticPreparationCancelledOnPrimaryFinish++;
            // Optional preparation gets only the time A itself needs. Retain
            // uncertain requests, cancel cooperatively, and always join B
            // before the unchanged complete final source/target close gate.
            transaction.Cancel();
            await other;
            return original;
        }
        finally
        {
            transaction.Cancel();
            try { await Task.WhenAll(primary, other); }
            catch { /* Preserve the primary exception after fully draining both tasks. */ }
        }
    }

    private CandidateSourceLease? TryCaptureCandidate(WindowId candidate, WindowId target, int generation,
        CancellationToken cancellationToken)
    {
        var captureStartedAt = Environment.TickCount64;
        RegisteredSnapshot? snapshot = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshShell();
            if (generation != _generation || !IsExplorerWindow(candidate) || !ShellNative.IsWindowVisible(candidate.Handle) ||
                candidate == target || _partialTransfers.Any(transfer => transfer.Source == candidate || transfer.Target == candidate))
                return null;

            // B is optional: it can fail or be absent without weakening A's
            // own complete initial snapshot, or its final all-target-tab gate.
            var registryStartedAt = Stopwatch.GetTimestamp();
            try { snapshot = ReadScopedRegisteredTabs(candidate); }
            finally { DiagnosticCandidateRegistryMilliseconds += ElapsedMilliseconds(registryStartedAt); }
            var window = snapshot.FindWindow(candidate);
            if (window.Tabs.Count != 1)
                return null;
            dynamic browser = window.Tabs[0].Browser;
            if ((bool)browser.Busy || Convert.ToInt32(browser.ReadyState, CultureInfo.InvariantCulture) != 4)
                return null;
            string url = browser.LocationURL;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsFile)
                return null;
            cancellationToken.ThrowIfCancellationRequested();
            WindowState provisional;
            var strictCaptureStartedAt = Stopwatch.GetTimestamp();
            try { provisional = CaptureRetainedWindow(window, generation); }
            finally { DiagnosticCandidateCaptureMilliseconds += ElapsedMilliseconds(strictCaptureStartedAt); }
            var observedAt = Environment.TickCount64;
            if (!Path.IsPathFullyQualified(provisional.Tabs.Single().Location) || observedAt - captureStartedAt >= 1000)
                return null;
            cancellationToken.ThrowIfCancellationRequested();
            var captured = new CandidateSourceLease(snapshot, window, provisional, observedAt, captureStartedAt);
            snapshot = null; // The local lease owns these RCWs until both tasks join.
            return captured;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { DiagnosticPreparationFailures++; return null; }
        finally { snapshot?.Dispose(); }
    }

    private async Task PrepareOtherSourceAsync(CandidateSourceLease captured, WindowId target, int generation,
        CancellationToken cancellationToken)
    {
        // At most 800ms after the strict B capture, and at most 1000ms including
        // its optional registry/capture work. COM calls themselves cannot be
        // forcibly cancelled; every async operation is still joined on exit.
        var deadline = Math.Min(captured.ObservedAt + SourceStability.MaximumObservationMilliseconds,
            captured.CaptureStartedAt + 1000);
        var remaining = deadline - Environment.TickCount64;
        if (remaining <= 0)
            return;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromMilliseconds(remaining));
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            budget.Token.ThrowIfCancellationRequested();
            var transfer = GetOrCreateTransfer(captured.Source.Id, target, captured.Provisional.Tabs[0]);
            DiagnosticPreparationAttempts++;
            var original = await PrepareSingleTabAsync(captured.Source, captured.Provisional, captured.ObservedAt,
                transfer, generation, captured.ObservedAt, budget.Token);
            if (original is null || generation != _generation || !transfer.Verified || transfer.LastManagedState is null)
                return;
            budget.Token.ThrowIfCancellationRequested();
            if (Environment.TickCount64 >= deadline)
                return;
            _preparedOtherSource = new PreparedOtherSource(original, target, generation, Environment.TickCount64,
                transfer.LastManagedState);
            DiagnosticPreparedOtherSources++;
        }
        catch (NativeAllocationAmbiguityException) { throw; }
        catch
        {
            // B stays open, and any posted request remains retained. A may
            // continue only through its unchanged complete final close gate.
            DiagnosticPreparationFailures++;
        }
        finally { DiagnosticPreparationMilliseconds += ElapsedMilliseconds(startedAt); }
    }

    private sealed record CandidateSourceLease(RegisteredSnapshot Snapshot, RegisteredWindow Source,
        WindowState Provisional, long ObservedAt, long CaptureStartedAt) : IDisposable
    {
        public void Dispose() => Snapshot.Dispose();
    }

    private async Task<(WindowState State, PartialTransfer Transfer)?> TryReusePreparedSourceAsync(
        RegisteredWindow source, WindowId target, int generation, CancellationToken cancellationToken)
    {
        var prepared = _preparedOtherSource;
        if (prepared is null || prepared.State.Id != source.Id)
            return null;
        _preparedOtherSource = null; // Cache consumption never prepares a third source.
        var age = Environment.TickCount64 - prepared.CompletedAt;
        var matches = _partialTransfers.Where(transfer => transfer.Source == source.Id && transfer.Target == target &&
            transfer.SourceState.Stamp == prepared.State.Tabs.Single().Stamp).ToArray();
        if (age is < 0 or > 1000 || prepared.Generation != generation || prepared.Target != target || matches.Length != 1 ||
            !matches[0].Verified || matches[0].LastManagedState?.Stamp != prepared.LastManagedState.Stamp)
        {
            DiagnosticPreparedCacheMiss++;
            return null;
        }
        var fresh = CaptureRetainedWindow(source, generation);
        if (!SameWindowState(prepared.State, fresh))
        {
            DiagnosticPreparedCacheMiss++;
            return null; // Changed source starts a new full quiet interval.
        }
        var transfer = matches[0];
        if (!await EnsureDestinationAsync(transfer, generation, cancellationToken))
            throw new ShellSnapshotException();
        if (transfer.LastManagedState?.Stamp != prepared.LastManagedState.Stamp)
            throw new UnsupportedShellViewException();
        DiagnosticPreparedCacheReuse++;
        return (fresh, transfer);
    }

    private void PrunePreparedSource()
    {
        if (_preparedOtherSource is not null && (!IsExplorerWindow(_preparedOtherSource.State.Id) ||
            !IsExplorerWindow(_preparedOtherSource.Target)))
            _preparedOtherSource = null;
    }

    // Immutable snapshots only: no browser/view/RCW is cached across transfers.
    private sealed record PreparedOtherSource(WindowState State, WindowId Target, int Generation, long CompletedAt,
        TabState LastManagedState);

    private async Task<bool> EnsureNativeTabAsync(PartialTransfer transfer, int generation, CancellationToken cancellationToken)
    {
        if (transfer.TargetTab != 0)
        {
            if (!NativeTabBelongsTo(transfer.Target, transfer.TargetTab))
                throw new UnsupportedShellViewException();
            return true;
        }
        var nativeFolder = await TryEnsureNativeFallbackTabAsync(transfer, generation, cancellationToken);
        if (nativeFolder.HasValue) return nativeFolder.Value;
        if (_nativeFallbackAllocation is not null) return false;
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            RefreshTransferShell(transfer, generation);
            if (generation != _generation || (!transfer.BackgroundOwned && !IsExplorerWindow(transfer.Source)) || !IsExplorerWindow(transfer.Target))
                throw new ShellSnapshotException();
            if (transfer.NativeReservation is null)
            {
                transfer.NativeReservation = _nativeReservations.ClaimEarly(transfer.Source, transfer.Target, transfer);
                if (transfer.NativeReservation is null)
                {
                    DiagnosticPhase = "request-new-tab";
                    ActivateTarget(transfer.Target);
                    transfer.NativeReservation = _nativeReservations.GetOrRequest(transfer.Source, transfer.Target, transfer);
                }
                if (transfer.NativeReservation is null)
                    return false;
                transfer.AdoptedEarly = transfer.NativeReservation.WasEarly;
                if (transfer.AdoptedEarly)
                    DiagnosticEarlyTabAdoptions++;
            }
            var deadline = Environment.TickCount64 + DestinationTimeoutMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RefreshTransferShell(transfer, generation);
                if (generation != _generation || (!transfer.BackgroundOwned && !IsExplorerWindow(transfer.Source)) || !IsExplorerWindow(transfer.Target))
                    throw new ShellSnapshotException();
                DiagnosticPhase = "wait-native-tab";
                var state = _nativeReservations.PollResolve(transfer.NativeReservation);
                switch (state.Status)
                {
                    case NativeTabReservationStatus.Resolved:
                    case NativeTabReservationStatus.Completed:
                        if (state.IsAbandoned || !NativeTabBelongsTo(transfer.Target, state.TargetTab))
                            throw new UnsupportedShellViewException();
                        transfer.TargetTab = state.TargetTab;
                        return true; // COM registration/navigation never holds the global native allocator.
                    case NativeTabReservationStatus.Ambiguous:
                        throw new NativeAllocationAmbiguityException();
                    case NativeTabReservationStatus.Invalidated:
                    case NativeTabReservationStatus.PostFailed:
                    case NativeTabReservationStatus.Abandoned:
                        throw new ShellSnapshotException();
                }
                await Task.Delay(PollMilliseconds, cancellationToken);
            }
            return false;
        }
        finally
        {
            DiagnosticAllocationMilliseconds += ElapsedMilliseconds(startedAt);
        }
    }

    private sealed class NativeAllocationAmbiguityException : Exception
    {
        internal NativeAllocationAmbiguityException() : base("Explorer native tab allocation is ambiguous.") { }
    }
}
