// Explorer tab commands, QueryService and FolderView operations are adapted from
// ExplorerTabUtility (w4po, copyright 2025), MIT, commit fad7363. See THIRD-PARTY-NOTICES.md.
// Sources: Hooks/ExplorerWatcher.cs (RequestToOpenNewTab, GetTabHandle,
// GetLocation, GetSelectedItems, SelectItems, Navigate) and Helpers/Helper.cs
// (GetAllExplorerTabs). Transactional snapshots and close verification are new.
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ExplorerOne.Core;

namespace ExplorerOne.Shell;

/// <summary>
/// Accesses Explorer on the owning WinForms STA. The default policy verifies
/// before closing; explicitly selected early-close mode owns background copies.
/// </summary>
public sealed partial class ShellExplorerBackend : IExplorerBackend, IExplorerPreparationBackend, IExplorerBackgroundBackend, IExplorerHintBackend, IDisposable
{
    private static readonly Guid ShellWindowsClass = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid ShellApplicationClass = new("13709620-C279-11CE-A49E-444553540000");
    private static readonly Guid ShellBrowserInterface = typeof(IShellBrowser).GUID;
    private const int DestinationTimeoutMilliseconds = 6000;
    private const int PollMilliseconds = 40;
    private const int MaximumRetainedTransfers = 256;

    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private readonly SynchronizationContext _ownerContext;
    private readonly bool _closeSourceEarly;
    private readonly List<PartialTransfer> _partialTransfers = [];
    private object? _shellWindows;
    private ShellPathComparer? _paths;
    private uint _shellProcess;
    private int _generation;
    private bool _transferring;
    private bool _disposed;

    // Safe, in-memory diagnostics for support and the isolated integration
    // harness. These never contain exception messages, URLs, or item names.
    internal string DiagnosticPhase { get; private set; } = "idle";
    internal string? DiagnosticFailureType { get; private set; }
    internal int DiagnosticFailureHResult { get; private set; }
    internal long DiagnosticTransferMilliseconds { get; private set; }
    internal long DiagnosticSourceStabilityMilliseconds { get; private set; }
    internal long DiagnosticRegistryMilliseconds { get; private set; }
    internal int DiagnosticRegistryReadCount { get; private set; }
    internal int DiagnosticRegistryBrowsersVisited { get; private set; }
    internal int DiagnosticTargetBrowsersVisited { get; private set; }
    internal int DiagnosticPreviousRegistryBrowsersVisited { get; private set; }
    internal int DiagnosticPreviousTargetBrowsersVisited { get; private set; }
    internal long DiagnosticTabReadyMilliseconds { get; private set; }
    internal long DiagnosticNavigateCallMilliseconds { get; private set; }
    internal long DiagnosticNavigationReadyMilliseconds { get; private set; }
    internal int DiagnosticDestinationPollCount { get; private set; }
    internal long DiagnosticActivationMilliseconds { get; private set; }
    internal long DiagnosticInitialCaptureMilliseconds { get; private set; }
    internal int DiagnosticLoadingFileCaptureCount { get; private set; }
    internal long DiagnosticFinalVerifyMilliseconds { get; private set; }
    internal long DiagnosticCloseCallMilliseconds { get; private set; }
    internal long DiagnosticCloseWaitMilliseconds { get; private set; }
    internal long DiagnosticPreviousTransferMilliseconds { get; private set; }
    internal long DiagnosticPreviousRegistryMilliseconds { get; private set; }
    internal long DiagnosticPreviousInitialCaptureMilliseconds { get; private set; }
    internal long DiagnosticPreviousSourceStabilityMilliseconds { get; private set; }
    internal long DiagnosticPreviousPreparationMilliseconds { get; private set; }
    internal long DiagnosticPreviousAllocationMilliseconds { get; private set; }
    internal long DiagnosticPreviousTabReadyMilliseconds { get; private set; }
    internal long DiagnosticPreviousNavigationReadyMilliseconds { get; private set; }
    internal long DiagnosticPreviousFinalVerifyMilliseconds { get; private set; }
    internal long DiagnosticPreviousCloseCallMilliseconds { get; private set; }
    internal long DiagnosticPreviousCloseWaitMilliseconds { get; private set; }
    internal long DiagnosticLastEnumerationMilliseconds { get; private set; }
    internal long DiagnosticLastControlPanelClassificationMilliseconds { get; private set; }
    internal int DiagnosticLastControlPanelBrowsersVisited { get; private set; }
    internal int DiagnosticLastControlPanelTabsClassified { get; private set; }
    internal int DiagnosticLastControlPanelTabsAssociated { get; private set; }

    /// <summary>A coalesced owner-STA hint; subscribers schedule work rather than reentering the backend.</summary>
    public event Action? ShellWindowRegistered;
    /// <summary>A positively excluded native root; subscribers may restore native presentation only.</summary>
    public event Action<WindowId>? WindowExcluded;

    public ShellExplorerBackend(bool closeSourceEarly = false)
        : this(closeSourceEarly, new NativeTabReservations(), ownsNativeReservations: true)
    {
    }

    internal ShellExplorerBackend(bool closeSourceEarly, NativeTabReservations nativeReservations)
        : this(closeSourceEarly, nativeReservations, ownsNativeReservations: false)
    {
    }

    private ShellExplorerBackend(bool closeSourceEarly, NativeTabReservations nativeReservations, bool ownsNativeReservations)
    {
        AssertContext();
        _ownerContext = SynchronizationContext.Current!;
        _closeSourceEarly = closeSourceEarly;
        _nativeReservations = nativeReservations;
        _ownsNativeReservations = ownsNativeReservations;
    }

    public IReadOnlyList<ExplorerWindow> EnumerateWindows()
    {
        AssertContext();
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            // Discovery is a complete NATIVE inventory. A different window
            // registering its COM view must not prevent routing a ready source.
            var native = ReadNativeWindows();
            var excluded = FindKnownControlPanelWindows(native);
            if (!NativeWindowSetsEqual(native, ReadNativeWindows()))
                throw new ShellSnapshotException();
            foreach (var id in excluded)
            {
                _nativeReservations.AbandonEarly(id);
                try { WindowExcluded?.Invoke(id); }
                catch { /* Presentation notifications cannot change discovery. */ }
            }
            return native.Where(window => !excluded.Contains(window.Id))
                .Select(window => new ExplorerWindow(window.Id, window.Tabs.Length)).ToArray();
        }
        catch
        {
            // Never expose a shell exception containing a location, or report
            // an incomplete native inventory as the entire set of windows.
            throw new ShellSnapshotException();
        }
        finally { DiagnosticLastEnumerationMilliseconds = ElapsedMilliseconds(startedAt); }
    }

    public async Task<TransferResult> TransferAsync(WindowId source, WindowId target, CancellationToken cancellationToken)
    {
        AssertContext();
        // An explicit coordinator plan is one-shot, even for a rejected call.
        // Direct backend calls never discover or prepare unrelated windows.
        var preparationCandidate = TakePreparationCandidate(source, target);
        if (_closeSourceEarly) preparationCandidate = null; // Fast jobs never speculate on another source.
        DiagnosticPreviousTransferMilliseconds = DiagnosticTransferMilliseconds;
        DiagnosticPreviousRegistryMilliseconds = DiagnosticRegistryMilliseconds;
        DiagnosticPreviousRegistryBrowsersVisited = DiagnosticRegistryBrowsersVisited;
        DiagnosticPreviousTargetBrowsersVisited = DiagnosticTargetBrowsersVisited;
        DiagnosticPreviousInitialCaptureMilliseconds = DiagnosticInitialCaptureMilliseconds;
        DiagnosticPreviousSourceStabilityMilliseconds = DiagnosticSourceStabilityMilliseconds;
        DiagnosticPreviousPreparationMilliseconds = DiagnosticPreparationMilliseconds;
        DiagnosticPreviousAllocationMilliseconds = DiagnosticAllocationMilliseconds;
        DiagnosticPreviousTabReadyMilliseconds = DiagnosticTabReadyMilliseconds;
        DiagnosticPreviousNavigationReadyMilliseconds = DiagnosticNavigationReadyMilliseconds;
        DiagnosticPreviousFinalVerifyMilliseconds = DiagnosticFinalVerifyMilliseconds;
        DiagnosticPreviousCloseCallMilliseconds = DiagnosticCloseCallMilliseconds;
        DiagnosticPreviousCloseWaitMilliseconds = DiagnosticCloseWaitMilliseconds;
        DiagnosticPhase = "transfer-start";
        DiagnosticFailureType = null;
        DiagnosticFailureHResult = 0;
        DiagnosticTransferMilliseconds = 0;
        DiagnosticSourceStabilityMilliseconds = 0;
        DiagnosticRegistryMilliseconds = 0;
        DiagnosticRegistryReadCount = 0;
        DiagnosticRegistryBrowsersVisited = 0;
        DiagnosticTargetBrowsersVisited = 0;
        DiagnosticTabReadyMilliseconds = 0;
        DiagnosticNavigateCallMilliseconds = 0;
        DiagnosticNavigationReadyMilliseconds = 0;
        DiagnosticDestinationPollCount = 0;
        DiagnosticActivationMilliseconds = 0;
        DiagnosticInitialCaptureMilliseconds = 0;
        DiagnosticLoadingFileCaptureCount = 0;
        DiagnosticEarlyTabAdoptions = 0;
        DiagnosticFinalVerifyMilliseconds = 0;
        DiagnosticCloseCallMilliseconds = 0;
        DiagnosticCloseWaitMilliseconds = 0;
        ResetPreparationDiagnostics();
        if (_transferring)
            return TransferResult.Retry;
        if (_backgroundJobs.ContainsKey(source))
            return TransferResult.NotReady; // This source is already exclusively owned by its background job.
        if (source == target || source.Handle == target.Handle || !IsExplorerWindow(target))
            return TransferResult.Unsafe;
        if (!IsExplorerWindow(source))
        {
            _nativeReservations.AbandonEarly(source);
            return TransferResult.SourceGone;
        }

        _transferring = true;
        var transferStartedAt = Stopwatch.GetTimestamp();
        var copyingStarted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            PruneVanishedTransfers();
            if (_closeSourceEarly && (!_acceptBackgroundTransfers || _backgroundJobs.Count >= MaximumBackgroundTransfers) &&
                ReadNativeTabs(source).Length == 1)
                return TransferResult.NotReady;
            // Only the source's complete initial view is consumed here. The
            // destination is identified/bound natively during preparation; its
            // ALL-tab COM/native snapshot remains mandatory at the close gate.
            using var initial = ReadScopedRegisteredTabs(source);
            var sourceWindow = initial.FindWindow(source);
            var generation = _generation;
            var observationStartedAt = Environment.TickCount64;
            if (_closeSourceEarly && sourceWindow.Tabs.Count == 1 &&
                (!_acceptBackgroundTransfers || _backgroundJobs.Count >= MaximumBackgroundTransfers))
                return TransferResult.NotReady;
            if (CanStartBackgroundTransfer(sourceWindow))
            {
                WindowState captured;
                var captureStartedAt = Stopwatch.GetTimestamp();
                try { captured = CaptureRetainedWindow(sourceWindow, generation, allowLoadingFileView: true); }
                finally { DiagnosticInitialCaptureMilliseconds += ElapsedMilliseconds(captureStartedAt); }
                var queued = await TryQueueBackgroundTransferAsync(captured, sourceWindow.Tabs[0], target, generation, cancellationToken);
                if (queued.HasValue)
                    return queued.Value;
            }
            var transfers = new List<PartialTransfer>();
            WindowState? original;
            (WindowState State, PartialTransfer Transfer)? prepared = null;
            if (sourceWindow.Tabs.Count == 1)
                prepared = await TryReusePreparedSourceAsync(sourceWindow, target, generation, cancellationToken);
            if (prepared.HasValue)
            {
                copyingStarted = true;
                original = prepared.Value.State;
                transfers.Add(prepared.Value.Transfer);
            }
            else if (sourceWindow.Tabs.Count == 1)
            {
                // Prepare our destination while the independently sampled
                // source satisfies the unchanged /select quiet period. Only
                // the stable snapshot below may become the close baseline.
                WindowState provisional;
                long provisionalObservedAt;
                var initialCaptureStartedAt = Stopwatch.GetTimestamp();
                try
                {
                    provisional = CaptureRetainedWindow(sourceWindow, generation);
                    provisionalObservedAt = Environment.TickCount64;
                }
                finally { DiagnosticInitialCaptureMilliseconds += ElapsedMilliseconds(initialCaptureStartedAt); }
                var transfer = GetOrCreateTransfer(source, target, provisional.Tabs[0]);
                copyingStarted = true;
                original = await PrepareWithCandidateAsync(sourceWindow, provisional, provisionalObservedAt,
                    transfer, generation, observationStartedAt, preparationCandidate, cancellationToken);
                if (original is not null)
                    transfers.Add(transfer);
            }
            else
            {
                original = await CaptureStableSourceAsync(sourceWindow, generation, observationStartedAt, cancellationToken);
            }
            if (original is null)
            {
                DiagnosticPhase = "source-not-settled";
                return IsExplorerWindow(source) ? TransferResult.NotReady : TransferResult.SourceGone;
            }
            for (var index = transfers.Count; index < original.Tabs.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = CaptureRetainedWindow(sourceWindow, generation);
                if (generation != _generation)
                    return copyingStarted ? TransferResult.Retry : TransferResult.NotReady;
                if (!SameWindowState(original, current))
                {
                    if (copyingStarted)
                        return TransferResult.Retry;

                    // A late /select update before Ctrl+T is still preflight:
                    // stabilize the latest source within the same bounded
                    // observation period instead of taking a long retry.
                    original = await CaptureStableSourceAsync(sourceWindow, generation, observationStartedAt, cancellationToken);
                    if (original is null)
                        return IsExplorerWindow(source) ? TransferResult.NotReady : TransferResult.SourceGone;
                    index = -1;
                    continue;
                }
                var tab = original.Tabs[index];

                var transfer = GetOrCreateTransfer(source, target, tab);
                if (transfer.SourceState.Stamp != tab.Stamp)
                    return TransferResult.Unsafe; // Multi-tab transfers retain their serial policy.

                copyingStarted = true;
                if (!await EnsureDestinationAsync(transfer, generation, cancellationToken))
                {
                    DiagnosticPhase = "destination-timeout";
                    return TransferResult.Retry;
                }
                transfers.Add(transfer);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _generation || !IsExplorerWindow(target))
                return TransferResult.Retry;
            if (!IsExplorerWindow(source))
                return TransferResult.SourceGone;

            // Re-read both endpoint windows in a fresh complete scoped COM/
            // native snapshot. All source AND target native tabs are covered;
            // no source mutation precedes this gate.
            var finalVerifyStartedAt = Stopwatch.GetTimestamp();
            try
            {
                using (var final = ReadScopedRegisteredTabs(source, target))
                {
                    var targetWindow = final.FindWindow(target);
                    var destinations = new List<VerifiedTabTransfer>();
                    foreach (var transfer in transfers)
                    {
                        var reference = targetWindow.Tabs.SingleOrDefault(tab => tab.Handle == transfer.TargetTab)
                            ?? throw new ShellSnapshotException();
                        var copied = CaptureTab(reference);
                        if (!SameTabContents(transfer.SourceState, copied))
                            return TransferResult.Unsafe;
                        destinations.Add(new VerifiedTabTransfer(transfer.SourceState.Handle, copied.Stamp));
                    }

                    var current = CaptureWindow(final.FindWindow(source));
                    if (!TransferSafety.CanCloseSource(original.Stamps, current.Stamps, destinations) ||
                        generation != _generation || !IsExplorerWindow(source) || !IsExplorerWindow(target) ||
                        !NativeTabsMatch(source, original.Tabs.Select(tab => tab.Handle)) ||
                        !NativeTabsMatch(target, targetWindow.Tabs.Select(tab => tab.Handle)))
                    {
                        DiagnosticPhase = "close-guard-rejected";
                        return TransferResult.Retry;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    // Quit() can close a different scope on tabbed Explorer. Send a
                    // bounded close only to this exact HWND+PID after verification.
                    var closeCallStartedAt = Stopwatch.GetTimestamp();
                    try
                    {
                        _ = ShellNative.SendMessageTimeout(source.Handle, ShellNative.WmClose, 0, 0,
                            ShellNative.AbortIfHung | ShellNative.ErrorOnExit, 1000, out _);
                    }
                    finally { DiagnosticCloseCallMilliseconds += ElapsedMilliseconds(closeCallStartedAt); }
                }
            }
            finally
            {
                // Account for snapshot capture/disposal and the complete gate,
                // with the separately measured native close call excluded.
                DiagnosticFinalVerifyMilliseconds += Math.Max(0,
                    ElapsedMilliseconds(finalVerifyStartedAt) - DiagnosticCloseCallMilliseconds);
            }

            var deadline = Environment.TickCount64 + 1500;
            var closeWaitStartedAt = Stopwatch.GetTimestamp();
            try
            {
                while (IsExplorerWindow(source) && Environment.TickCount64 < deadline)
                    await Task.Delay(10, cancellationToken); // Cheap native close observation only.
            }
            finally { DiagnosticCloseWaitMilliseconds += ElapsedMilliseconds(closeWaitStartedAt); }
            if (IsExplorerWindow(source))
            {
                DiagnosticPhase = "source-close-timeout";
                return TransferResult.Retry;
            }

            _partialTransfers.RemoveAll(transfer => transfer.Source == source);
            ActivateTarget(target);
            DiagnosticPhase = "completed";
            DiagnosticFailureType = null;
            DiagnosticFailureHResult = 0;
            return TransferResult.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AbandonUnclaimedPreopen(source, target);
            return TransferResult.Retry;
        }
        catch (UnsupportedShellViewException exception)
        {
            AbandonUnclaimedPreopen(source, target);
            RecordDiagnosticFailure(exception);
            return TransferResult.Unsafe;
        }
        catch (NativeAllocationAmbiguityException exception)
        {
            RecordDiagnosticFailure(exception);
            return TransferResult.Unsafe;
        }
        catch (ShellSnapshotException exception) when (!copyingStarted)
        {
            RecordDiagnosticFailure(exception);
            DiagnosticPhase = "source-not-ready";
            if (!IsExplorerWindow(source))
                AbandonUnclaimedPreopen(source, target);
            return IsExplorerWindow(source) ? TransferResult.NotReady : TransferResult.SourceGone;
        }
        catch (Exception exception)
        {
            RecordDiagnosticFailure(exception);
            // The host reports only this classification, never private shell
            // paths or raw automation exception messages.
            if (!IsExplorerWindow(source))
                AbandonUnclaimedPreopen(source, target);
            return IsExplorerWindow(source) ? TransferResult.Retry : TransferResult.SourceGone;
        }
        finally
        {
            DiagnosticTransferMilliseconds = ElapsedMilliseconds(transferStartedAt);
            _transferring = false;
        }
    }

    private PartialTransfer GetOrCreateTransfer(WindowId source, WindowId target, TabState tab)
    {
        // An unresolved request belongs to the source native tab even if a
        // late /select update changes its contents. Never issue another Ctrl+T
        // merely because the selection/location part of its stamp changed.
        var retained = _partialTransfers.Where(candidate => candidate.Source == source &&
            candidate.SourceState.Handle == tab.Handle).ToArray();
        if (retained.Length > 1 || (retained.Length == 1 && retained[0].Target != target))
            throw new UnsupportedShellViewException();
        if (retained.Length == 1)
            return retained[0];
        if (_partialTransfers.Count >= MaximumRetainedTransfers)
            throw new UnsupportedShellViewException();
        var transfer = new PartialTransfer(source, target, tab);
        _partialTransfers.Add(transfer);
        return transfer;
    }

    private async Task<WindowState?> PrepareSingleTabAsync(RegisteredWindow source, WindowState provisional,
        long provisionalObservedAt, PartialTransfer transfer, int generation, long observationStartedAt,
        CancellationToken cancellationToken)
    {
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var destination = EnsureDestinationAsync(transfer, generation, pending.Token);
        var stable = CaptureStableSourceAsync(source, generation, observationStartedAt, pending.Token,
            provisional, provisionalObservedAt);
        WindowState? original;
        try
        {
            // Observe either failure immediately, and drain both tasks before
            // returning or releasing their retained COM references.
            if (await Task.WhenAny(destination, stable) == destination)
            {
                if (!await destination)
                    throw new ShellSnapshotException();
                original = await stable;
            }
            else
            {
                original = await stable;
                if (original is null)
                    return null;
                if (!await destination)
                    throw new ShellSnapshotException();
            }
        }
        finally
        {
            pending.Cancel();
            try { await Task.WhenAll(destination, stable); }
            catch { /* The primary result/exception above owns classification. */ }
        }
        if (original is null)
            return null;
        if (generation != _generation || !SameWindowState(original, CaptureRetainedWindow(source, generation)))
            throw new ShellSnapshotException(); // A change AFTER stability is never a new close baseline.
        var actual = original.Tabs.Single();
        if (transfer.SourceState.Stamp != actual.Stamp)
        {
            // Rebase only after our provisional copy was verified, and only
            // while its exact last managed contents remain untouched.
            if (!transfer.Verified || transfer.LastManagedState is null || transfer.SourceState.Handle != actual.Handle)
                throw new UnsupportedShellViewException();
            using var reference = FindRegisteredTab(transfer.Target, transfer.TargetTab, generation)
                ?? throw new ShellSnapshotException();
            ValidateReference(reference, transfer.Target);
            var current = CaptureTab(reference);
            if (current.Stamp != transfer.LastManagedState.Stamp)
                throw new UnsupportedShellViewException();
            transfer.RebaseFrom = current;
            transfer.NavigationIssued = _paths!.Equivalent(transfer.SourceState.Location, actual.Location);
            transfer.SourceState = actual;
            transfer.Verified = false;
            if (!await EnsureDestinationAsync(transfer, generation, cancellationToken))
                throw new ShellSnapshotException();
        }
        return original;
    }

    private async Task<WindowState?> CaptureStableSourceAsync(
        RegisteredWindow source, int generation, long observationStartedAt, CancellationToken cancellationToken,
        WindowState? firstSample = null, long firstSampleObservedAt = 0)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var stability = new SourceStability(observationStartedAt);
        try
        {
            if (firstSample is not null)
            {
                if (firstSample.Id != source.Id)
                    throw new ShellSnapshotException();
                // The provisional capture already read the live Document and
                // selection twice. Count that strict observation at the actual
                // capture-finished time, then take later fresh temporal samples.
                stability.Observe(firstSample.Stamps, firstSample.Tabs.Any(tab => tab.Selection.Length == 0),
                    firstSampleObservedAt);
                if (stability.IsExpired(Environment.TickCount64))
                    return null;
                DiagnosticPhase = "source-stabilizing";
                await Task.Delay(SourceStability.SampleIntervalMilliseconds, cancellationToken);
            }
            while (!stability.IsExpired(Environment.TickCount64))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsExplorerWindow(source.Id))
                    return null;

                WindowState current;
                try
                {
                    current = CaptureRetainedWindow(source, generation);
                }
                catch (ShellSnapshotException)
                {
                    // Busy views may settle while our provisional destination
                    // loads. Identity, tab-set or shell-generation changes are
                    // never treated as a transient source sample.
                    RefreshShell();
                    if (generation != _generation || !NativeTabsMatch(source.Id, source.Tabs.Select(tab => tab.Handle)))
                        throw;
                    stability.Observe([], true, Environment.TickCount64);
                    DiagnosticPhase = "source-stabilizing";
                    await Task.Delay(SourceStability.SampleIntervalMilliseconds, cancellationToken);
                    continue;
                }
                if (stability.Observe(current.Stamps, current.Tabs.Any(tab => tab.Selection.Length == 0), Environment.TickCount64))
                    return current;

                DiagnosticPhase = "source-stabilizing";
                await Task.Delay(SourceStability.SampleIntervalMilliseconds, cancellationToken);
            }
            return null;
        }
        finally
        {
            DiagnosticSourceStabilityMilliseconds += ElapsedMilliseconds(startedAt);
        }
    }

    private WindowState CaptureRetainedWindow(RegisteredWindow window, int generation, bool allowLoadingFileView = false)
    {
        RefreshShell();
        if (generation != _generation || !NativeTabsMatch(window.Id, window.Tabs.Select(tab => tab.Handle)))
            throw new ShellSnapshotException();
        var state = CaptureWindow(window, allowLoadingFileView);
        if (generation != _generation)
            throw new ShellSnapshotException();
        return state;
    }

    private async Task<bool> EnsureDestinationAsync(PartialTransfer transfer, int generation, CancellationToken cancellationToken)
    {
        RefreshTransferShell(transfer, generation);
        if (generation != _generation)
            throw new ShellSnapshotException();
        if (!IsExplorerWindow(transfer.Target))
            throw new ShellSnapshotException();

        var tabRequestedAt = Stopwatch.GetTimestamp();
        if (!await EnsureNativeTabAsync(transfer, generation, cancellationToken))
            return false;
        if (transfer.BackgroundOwned)
            await Task.Yield(); // Never enter destination COM before the caller posts its close request.
        var deadline = Environment.TickCount64 + DestinationTimeoutMilliseconds;
        var selectionAppliedThisAttempt = false;
        long? navigationStartedAt = null;
        RegisteredTab? reference = null;
        try
        {
            while (Environment.TickCount64 < deadline)
            {
                DiagnosticDestinationPollCount++;
                cancellationToken.ThrowIfCancellationRequested();
                RefreshTransferShell(transfer, generation);
                if (generation != _generation || (!transfer.BackgroundOwned && !IsExplorerWindow(transfer.Source)) || !IsExplorerWindow(transfer.Target))
                    throw new ShellSnapshotException();

                try
                {
                    if (!NativeTabBelongsTo(transfer.Target, transfer.TargetTab))
                        throw new UnsupportedShellViewException();
                    if (reference is null)
                    {
                        DiagnosticPhase = "wait-tab-registration";
                        reference = FindRegisteredTab(transfer.Target, transfer.TargetTab, generation);
                        if (reference is null)
                        {
                            await Task.Delay(PollMilliseconds, cancellationToken);
                            continue;
                        }
                        DiagnosticTabReadyMilliseconds += ElapsedMilliseconds(tabRequestedAt);
                    }
                    ValidateReference(reference, transfer.Target);
                    if (!transfer.NavigationIssued && transfer.AdoptedEarly && !transfer.EarlyDestinationValidated)
                    {
                        // The early tab was visible before its source could be
                        // captured. Preserve any user navigation/selection.
                        var early = CaptureTab(reference);
                        var alreadyCopied = SameTabContents(transfer.SourceState, early);
                        if (!alreadyCopied && !IsEmptyHome(early))
                            throw new UnsupportedShellViewException();
                        transfer.EarlyDestinationValidated = true;
                        transfer.NavigationIssued = alreadyCopied;
                    }
                    if (!transfer.NavigationIssued)
                    {
                        // The default Home view need not be portable. Navigate
                        // only our newly associated native+registered tab.
                        if (transfer.RebaseFrom is not null)
                        {
                            if (CaptureTab(reference).Stamp != transfer.RebaseFrom.Stamp)
                                throw new UnsupportedShellViewException();
                            transfer.RebaseFrom = null;
                        }
                        transfer.NavigationIssued = true;
                        ActivateTarget(transfer.Target);
                        navigationStartedAt = Stopwatch.GetTimestamp();
                        Navigate(reference.Browser, transfer.SourceState.Location);
                    }

                    TabState current;
                    try
                    {
                        current = CaptureTab(reference);
                    }
                    catch (UnsupportedShellViewException) when (!transfer.Verified)
                    {
                        await Task.Delay(PollMilliseconds, cancellationToken);
                        continue;
                    }
                    if (SameTabContents(transfer.SourceState, current))
                    {
                        if (navigationStartedAt.HasValue)
                            DiagnosticNavigationReadyMilliseconds += ElapsedMilliseconds(navigationStartedAt.Value);
                        transfer.Verified = true;
                        transfer.LastManagedState = current;
                        transfer.RebaseFrom = null;
                        CompleteNativeReservation(transfer);
                        return true;
                    }
                    if (transfer.Verified)
                        throw new UnsupportedShellViewException(); // The user changed an already copied tab.

                    var sameLocation = _paths!.Equivalent(transfer.SourceState.Location, current.Location);
                    if (sameLocation && !selectionAppliedThisAttempt)
                    {
                        if (transfer.RebaseFrom is not null ? current.Stamp != transfer.RebaseFrom.Stamp :
                            !current.Selection.All(selected => transfer.SourceState.Selection.Any(expected =>
                                _paths.Equivalent(expected.Identity, selected.Identity))))
                            throw new UnsupportedShellViewException();
                        selectionAppliedThisAttempt = true;
                        DiagnosticPhase = "apply-selection";
                        ValidateReference(reference, transfer.Target);
                        if (transfer.RebaseFrom is not null)
                        {
                            if (CaptureTab(reference).Stamp != transfer.RebaseFrom.Stamp)
                                throw new UnsupportedShellViewException();
                            transfer.RebaseFrom = null;
                        }
                        ApplySelection(reference.Browser, transfer.SourceState.Selection);
                        var selected = CaptureTab(reference);
                        if (SameTabContents(transfer.SourceState, selected))
                        {
                            if (navigationStartedAt.HasValue)
                                DiagnosticNavigationReadyMilliseconds += ElapsedMilliseconds(navigationStartedAt.Value);
                            transfer.Verified = true;
                            transfer.LastManagedState = selected;
                            CompleteNativeReservation(transfer);
                            return true;
                        }
                    }
                }
                catch (ShellSnapshotException)
                {
                    // Transient registration/busy views are allowed to settle.
                }

                await Task.Delay(PollMilliseconds, cancellationToken);
            }
            return false;
        }
        finally
        {
            reference?.Dispose();
        }
    }

    private WindowState CaptureWindow(WindowId id)
    {
        using var snapshot = ReadScopedRegisteredTabs(id);
        return CaptureWindow(snapshot.FindWindow(id));
    }

    // A targeted lookup proves only this tab association, never a complete
    // window snapshot. Final verification always uses ReadScopedRegisteredTabs.
    private RegisteredTab? FindRegisteredTab(WindowId window, nint handle, int generation)
    {
        RefreshShell();
        if (generation != _generation || !NativeTabBelongsTo(window, handle))
            throw new ShellSnapshotException();
        dynamic shellWindows = _shellWindows!;
        int count = shellWindows.Count;
        // New tab registrations are normally appended. Keep the exact native
        // association checks below while avoiding older rows when it is last.
        for (var index = count - 1; index >= 0; index--)
        {
            object? browser = null;
            IShellBrowser? shellBrowser = null;
            try
            {
                DiagnosticTargetBrowsersVisited++;
                browser = shellWindows.Item(index);
                if (browser is null)
                    continue;
                nint browserWindow = new(Convert.ToInt64(((dynamic)browser).HWND, CultureInfo.InvariantCulture));
                if (ShellNative.GetAncestor(browserWindow, ShellNative.RootAncestor) != window.Handle ||
                    browser is not IShellServiceProvider services)
                    continue;
                var service = ShellBrowserInterface;
                var iid = ShellBrowserInterface;
                if (services.QueryService(ref service, ref iid, out shellBrowser) < 0 || shellBrowser is null ||
                    shellBrowser.GetWindow(out var actual) < 0 || actual != handle)
                    continue;
                if (generation != _generation || !NativeTabBelongsTo(window, handle))
                    throw new ShellSnapshotException();
                var reference = new RegisteredTab(handle, browser);
                browser = null;
                return reference;
            }
            catch (Exception exception) when (exception is COMException or InvalidComObjectException or
                Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                RecordDiagnosticFailure(exception);
                if (exception is COMException disconnected && ResetDisconnectedShell(disconnected))
                    throw new ShellSnapshotException();
            }
            finally
            {
                ComReference.Release(shellBrowser);
                ComReference.Release(browser);
            }
        }
        return null;
    }

    private static void ValidateReference(RegisteredTab reference, WindowId window)
    {
        if (!NativeTabBelongsTo(window, reference.Handle))
            throw new ShellSnapshotException();
        IShellBrowser? shellBrowser = null;
        try
        {
            nint browserWindow = new(Convert.ToInt64(((dynamic)reference.Browser).HWND, CultureInfo.InvariantCulture));
            if (ShellNative.GetAncestor(browserWindow, ShellNative.RootAncestor) != window.Handle ||
                reference.Browser is not IShellServiceProvider services)
                throw new ShellSnapshotException();
            var service = ShellBrowserInterface;
            var iid = ShellBrowserInterface;
            if (services.QueryService(ref service, ref iid, out shellBrowser) < 0 || shellBrowser is null ||
                shellBrowser.GetWindow(out var actual) < 0 || actual != reference.Handle)
                throw new ShellSnapshotException();
        }
        finally
        {
            ComReference.Release(shellBrowser);
        }
    }

    private static bool NativeTabBelongsTo(WindowId window, nint tab)
    {
        if (!IsExplorerWindow(window) || !ShellNative.IsWindow(tab) ||
            ShellNative.ClassName(tab) != "ShellTabWindowClass" ||
            ShellNative.GetAncestor(tab, ShellNative.RootAncestor) != window.Handle)
            return false;
        ShellNative.GetWindowThreadProcessId(tab, out var process);
        return process == window.ProcessId;
    }

    private void ActivateTarget(WindowId target)
    {
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            if (!IsExplorerWindow(target))
                return;
            if (ShellNative.IsIconic(target.Handle))
                _ = ShellNative.ShowWindowAsync(target.Handle, 9); // SW_RESTORE.
            _ = ShellNative.SetForegroundWindow(target.Handle);
        }
        finally { DiagnosticActivationMilliseconds += ElapsedMilliseconds(startedAt); }
    }

    private WindowState CaptureWindow(RegisteredWindow window, bool allowLoadingFileView = false)
    {
        var tabs = window.Tabs.Select(tab => CaptureTab(tab, allowLoadingFileView)).ToArray();
        if (!NativeTabsMatch(window.Id, tabs.Select(tab => tab.Handle)))
            throw new ShellSnapshotException();
        return new WindowState(window.Id, tabs);
    }

    private TabState CaptureTab(RegisteredTab reference, bool allowLoadingFileView = false)
    {
        try
        {
            dynamic browser = reference.Browser;
            DiagnosticPhase = "tab-ready-state";
            // Only a new fast transfer may read an already coherent file view
            // before Explorer finishes loading. Namespace views still wait.
            allowLoadingFileView = allowLoadingFileView && IsAbsoluteFileUrl((string)browser.LocationURL);
            var loadingAtStart = (bool)browser.Busy || Convert.ToInt32(browser.ReadyState, CultureInfo.InvariantCulture) != 4;
            if (loadingAtStart && !allowLoadingFileView)
                throw new ShellSnapshotException();

            // A Document/SelectedItems COM object may remain readable after
            // Explorer has replaced its view. Obtain both afresh on each read.
            var first = CaptureDocument(reference, allowLoadingFileView, out var firstLoading);
            var second = CaptureDocument(reference, allowLoadingFileView, out var secondLoading);
            var loadingAtEnd = (bool)browser.Busy || Convert.ToInt32(browser.ReadyState, CultureInfo.InvariantCulture) != 4;
            var observedLoading = loadingAtStart || firstLoading || secondLoading || loadingAtEnd;
            if (first.Stamp != second.Stamp || (observedLoading && !allowLoadingFileView))
                throw new ShellSnapshotException();
            if (observedLoading)
            {
                string finalUrl = browser.LocationURL;
                if (!IsAbsoluteFileUrl(finalUrl) || !_paths!.Equivalent(second.Location, finalUrl))
                    throw new ShellSnapshotException();
                DiagnosticLoadingFileCaptureCount++;
            }
            return second;
        }
        catch (COMException exception)
        {
            RecordDiagnosticFailure(exception);
            ResetDisconnectedShell(exception);
            throw new ShellSnapshotException();
        }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException exception)
        {
            RecordDiagnosticFailure(exception);
            throw new UnsupportedShellViewException();
        }
    }

    private static bool IsAbsoluteFileUrl(string? url) =>
        url is not null && url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) &&
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.IsFile;

    private TabState CaptureDocument(RegisteredTab reference, bool allowLoadingFileView, out bool observedLoading)
    {
        observedLoading = false;
        object? document = null;
        object? folder = null;
        object? self = null;
        object? selected = null;
        try
        {
            dynamic browser = reference.Browser;
            DiagnosticPhase = "read-document-folder";
            document = browser.Document;
            if (document is null)
                throw new ShellSnapshotException();
            folder = ((dynamic)document).Folder;
            self = ((dynamic)folder).Self;
            if (!(bool)((dynamic)self).IsFolder)
                throw new UnsupportedShellViewException();
            string path = ((dynamic)self).Path;
            DiagnosticPhase = "canonicalize-folder";
            var location = _paths!.Canonicalize(path);

            // Control Panel views are not portable Explorer folder tabs.
            if (IsControlPanelLocation(location))
                throw new UnsupportedShellViewException();

            string url = browser.LocationURL;
            DiagnosticPhase = "compare-folder-url";
            if (!string.IsNullOrEmpty(url) && !_paths.Equivalent(location, url))
                throw new ShellSnapshotException();

            selected = ((dynamic)document).SelectedItems();
            DiagnosticPhase = "read-selected-items";
            int count = ((dynamic)selected).Count;
            var selection = new List<SelectedItem>(count);
            for (var index = 0; index < count; index++)
            {
                object? item = null;
                try
                {
                    item = ((dynamic)selected).Item(index);
                    string itemPath = ((dynamic)item).Path;
                    string itemName = ((dynamic)item).Name;
                    DiagnosticPhase = "canonicalize-selected-item";
                    var identity = _paths.Canonicalize(itemPath);
                    if (selection.Any(existing => _paths.Equivalent(existing.Identity, identity)))
                        throw new UnsupportedShellViewException();
                    selection.Add(new SelectedItem(identity, itemName));
                }
                finally
                {
                    ComReference.Release(item);
                }
            }

            string finalUrl = browser.LocationURL;
            DiagnosticPhase = "validate-document-state";
            observedLoading = (bool)browser.Busy;
            if ((observedLoading && !(allowLoadingFileView && IsAbsoluteFileUrl(url) && IsAbsoluteFileUrl(finalUrl))) ||
                (int)((dynamic)selected).Count != count ||
                !_paths.Equivalent(location, (string)((dynamic)self).Path) ||
                (!string.IsNullOrEmpty(finalUrl) && !_paths.Equivalent(location, finalUrl)))
                throw new ShellSnapshotException();
            selection.Sort((first, second) => StringComparer.Ordinal.Compare(first.Identity, second.Identity));
            return new TabState(reference.Handle, location, selection.ToArray());
        }
        catch (COMException exception)
        {
            RecordDiagnosticFailure(exception);
            ResetDisconnectedShell(exception);
            throw new ShellSnapshotException();
        }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException exception)
        {
            RecordDiagnosticFailure(exception);
            throw new UnsupportedShellViewException();
        }
        finally
        {
            ComReference.Release(selected);
            ComReference.Release(self);
            ComReference.Release(folder);
            ComReference.Release(document);
        }
    }

    private void Navigate(object browser, string location)
    {
        DiagnosticPhase = "navigate-destination";
        var startedAt = Stopwatch.GetTimestamp();
        object? shell = null;
        object? folder = null;
        try
        {
            // Match the upstream fast path for ordinary filesystem parsing
            // names. Keep Folder objects for URI-sensitive literal #/% and
            // namespace locations whose existing behavior is already proven.
            if (Path.IsPathFullyQualified(location) && !location.Contains('#') && !location.Contains('%'))
            {
                ((dynamic)browser).Navigate2(location);
                return;
            }
            // A Folder automation object preserves literal # and % in paths,
            // instead of letting Navigate2 interpret them as URL components.
            shell = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellApplicationClass, true)!);
            folder = ((dynamic)shell!).NameSpace(location);
            if (folder is null)
                throw new UnsupportedShellViewException();
            ((dynamic)browser).Navigate2(folder);
        }
        finally
        {
            ComReference.Release(folder);
            ComReference.Release(shell);
            DiagnosticNavigateCallMilliseconds += ElapsedMilliseconds(startedAt);
        }
    }

    private HashSet<WindowId> FindKnownControlPanelWindows(IReadOnlyList<NativeWindow> native)
    {
        var startedAt = Stopwatch.GetTimestamp();
        try { return FindKnownControlPanelWindowsCore(native); }
        finally { DiagnosticLastControlPanelClassificationMilliseconds = ElapsedMilliseconds(startedAt); }
    }

    private HashSet<WindowId> FindKnownControlPanelWindowsCore(IReadOnlyList<NativeWindow> native)
    {
        var excluded = new HashSet<WindowId>();
        var associatedTabs = new HashSet<nint>();
        var classifiedTabs = new HashSet<nint>();
        DiagnosticLastControlPanelBrowsersVisited = 0;
        DiagnosticLastControlPanelTabsClassified = 0;
        DiagnosticLastControlPanelTabsAssociated = 0;
        if (native.Count == 0)
            return excluded;
        try
        {
            RefreshShell();
            if (native.All(window => window.Tabs.Length == 0))
                return excluded; // No COM row can prove an association to a missing native tab.
            dynamic shellWindows = _shellWindows!;
            int count = shellWindows.Count;
            for (var index = count - 1; index >= 0; index--)
            {
                object? browser = null;
                IShellBrowser? shellBrowser = null;
                try
                {
                    DiagnosticLastControlPanelBrowsersVisited++;
                    browser = shellWindows.Item(index);
                    if (browser is null)
                        continue;
                    nint browserWindow = new(Convert.ToInt64(((dynamic)browser).HWND, CultureInfo.InvariantCulture));
                    var root = ShellNative.GetAncestor(browserWindow, ShellNative.RootAncestor);
                    var window = native.FirstOrDefault(candidate => candidate.Id.Handle == root);
                    if (window is null || excluded.Contains(window.Id) ||
                        browser is not IShellServiceProvider services)
                        continue;
                    var service = ShellBrowserInterface;
                    var iid = ShellBrowserInterface;
                    if (services.QueryService(ref service, ref iid, out shellBrowser) < 0 || shellBrowser is null ||
                        shellBrowser.GetWindow(out var tab) < 0 || !window.Tabs.Contains(tab) || !NativeTabBelongsTo(window.Id, tab))
                        continue;
                    if (!associatedTabs.Add(tab))
                        continue; // Newest exact association is authoritative, as in scoped snapshots.
                    DiagnosticLastControlPanelTabsAssociated = associatedTabs.Count;
                    var classification = BrowserIsControlPanel(browser);
                    if (classification == true)
                        excluded.Add(window.Id);
                    else if (classification == false)
                    {
                        classifiedTabs.Add(tab);
                        RememberMainRouteAnchor(window.Id, tab, index, _generation);
                    }
                    // Null remains unknown/included; an older duplicate COM
                    // row must not replace the newest associated tab's state.
                    DiagnosticLastControlPanelTabsClassified = classifiedTabs.Count;
                }
                catch (Exception exception)
                {
                    // Classification is optional. An unregistered/unreadable
                    // view stays in the complete native inventory until it can
                    // be positively recognized; it cannot block other windows.
                    RecordDiagnosticFailure(exception);
                    if (exception is COMException disconnected && ResetDisconnectedShell(disconnected))
                    {
                        excluded.Clear();
                        return excluded;
                    }
                }
                finally
                {
                    ComReference.Release(shellBrowser);
                    ComReference.Release(browser);
                }
                if (native.All(candidate => excluded.Contains(candidate.Id) || candidate.Tabs.All(associatedTabs.Contains)))
                    break;
            }
        }
        catch (Exception exception)
        {
            RecordDiagnosticFailure(exception);
            if (exception is COMException disconnected)
                ResetDisconnectedShell(disconnected);
            excluded.Clear();
        }
        return excluded;
    }

    private static bool? BrowserIsControlPanel(object browser)
    {
        string url = ((dynamic)browser).LocationURL;
        if (IsControlPanelLocation(url))
            return true;
        if ((bool)((dynamic)browser).Busy || Convert.ToInt32(((dynamic)browser).ReadyState, CultureInfo.InvariantCulture) != 4)
            return null; // Do not count or request an initializing Document.
        if (!string.IsNullOrEmpty(url))
            return false; // A ready ordinary tab needs no view capture.

        object? document = null;
        object? folder = null;
        object? self = null;
        try
        {
            document = ((dynamic)browser).Document;
            if (document is null)
                return null;
            folder = ((dynamic)document).Folder;
            if (folder is null) return null;
            self = ((dynamic)folder).Self;
            if (self is null) return null;
            string path = ((dynamic)self).Path;
            return string.IsNullOrEmpty(path) ? null : IsControlPanelLocation(path);
        }
        finally
        {
            ComReference.Release(self);
            ComReference.Release(folder);
            ComReference.Release(document);
        }
    }

    private static bool IsControlPanelLocation(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        if (value.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            value = value[6..];
        return IsNamespace(value, "::{26EE0668-A00A-44D7-9371-BEB064C98683}") ||
            IsNamespace(value, "::{21EC2020-3AEA-1069-A2DD-08002B30309D}");

        static bool IsNamespace(string location, string root) =>
            location.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
            (location.Length == root.Length || location[root.Length] is '\\' or '/');
    }

    private void ApplySelection(object browser, IReadOnlyList<SelectedItem> selection)
    {
        if (selection.Count == 0)
        {
            // This is reached with existing selection only for an exact,
            // freshly verified managed rebase. Deselecting never changes files.
            object? emptyDocument = null, selected = null;
            var previous = new List<object>();
            try
            {
                emptyDocument = ((dynamic)browser).Document;
                selected = ((dynamic)emptyDocument).SelectedItems();
                int count = ((dynamic)selected).Count;
                for (var index = 0; index < count; index++)
                    previous.Add(((dynamic)selected).Item(index));
                foreach (var selectedItem in previous)
                {
                    object item = selectedItem;
                    ((dynamic)emptyDocument).SelectItem(ref item, 0); // SVSI_DESELECT.
                }
            }
            finally
            {
                foreach (var item in previous) ComReference.Release(item);
                ComReference.Release(selected);
                ComReference.Release(emptyDocument);
            }
            return;
        }

        object? document = null;
        object? folder = null;
        var parsed = new List<object>();
        try
        {
            document = ((dynamic)browser).Document;
            folder = ((dynamic)document).Folder;
            foreach (var expected in selection)
            {
                object? item = null;
                try
                {
                    DiagnosticPhase = "selection-parse-relative";
                    var parsingName = _paths!.RelativeParsingName(expected.Identity);
                    item = ((dynamic)folder).ParseName(parsingName);
                    if (item is null)
                    {
                        DiagnosticPhase = "selection-parse-absolute";
                        item = ((dynamic)folder).ParseName(expected.Identity);
                    }
                    if (item is null)
                    {
                        DiagnosticPhase = "selection-parse-display";
                        item = ((dynamic)folder).ParseName(expected.Name);
                    }
                    DiagnosticPhase = "selection-validate-identity";
                    if (item is null || !_paths!.Equivalent(expected.Identity, (string)((dynamic)item).Path))
                        throw new UnsupportedShellViewException();
                    parsed.Add(item);
                    item = null;
                }
                finally
                {
                    ComReference.Release(item);
                }
            }

            for (var index = 0; index < parsed.Count; index++)
            {
                DiagnosticPhase = "selection-select-item";
                object item = parsed[index];
                // SELECT; first item also DESELECTOTHERS and ENSUREVISIBLE.
                ((dynamic)document).SelectItem(ref item, index == 0 ? 1 | 4 | 8 : 1);
            }
        }
        finally
        {
            foreach (var item in parsed)
                ComReference.Release(item);
            ComReference.Release(folder);
            ComReference.Release(document);
        }
    }

    private RegisteredSnapshot ReadRegisteredTabs() => ReadRegisteredTabsMeasured(null);

    private RegisteredSnapshot ReadScopedRegisteredTabs(params WindowId[] windows) =>
        ReadRegisteredTabsMeasured(windows);

    private RegisteredSnapshot ReadRegisteredTabsMeasured(IReadOnlyList<WindowId>? requiredWindows)
    {
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            return ReadRegisteredTabsCore(requiredWindows);
        }
        finally
        {
            DiagnosticRegistryReadCount++;
            DiagnosticRegistryMilliseconds += ElapsedMilliseconds(startedAt);
        }
    }

    private RegisteredSnapshot ReadRegisteredTabsCore(IReadOnlyList<WindowId>? requiredWindows)
    {
        DiagnosticPhase = "connect-shell";
        RefreshShell();
        DiagnosticPhase = "enumerate-native-windows";
        var native = requiredWindows is null ? ReadNativeWindows() : ReadNativeWindowScope(requiredWindows);
        var expectedTabs = requiredWindows is null ? null : native.SelectMany(window => window.Tabs).ToHashSet();
        var snapshot = new RegisteredSnapshot();
        try
        {
            dynamic shellWindows = _shellWindows!;
            DiagnosticPhase = "registered-tab-count";
            int count = shellWindows.Count;
            var seenTabs = new HashSet<nint>();
            for (var visit = 0; visit < count; visit++)
            {
                // Scoped sources are normally the newest registrations. Global
                // snapshots retain their existing complete enumeration order.
                var index = requiredWindows is null ? visit : count - 1 - visit;
                object? browser = null;
                IShellBrowser? shellBrowser = null;
                try
                {
                    DiagnosticRegistryBrowsersVisited++;
                    browser = shellWindows.Item(index);
                    DiagnosticPhase = "registered-browser-window";
                    if (browser is null)
                        continue; // A closed tab can leave a stale collection slot.
                    nint browserWindow = new(Convert.ToInt64(((dynamic)browser).HWND, CultureInfo.InvariantCulture));
                    var root = ShellNative.GetAncestor(browserWindow, ShellNative.RootAncestor);
                    var window = native.FirstOrDefault(candidate => candidate.Id.Handle == root);
                    if (window is null)
                        continue; // Desktop, browser, dialog, or another shell window.

                    if (browser is not IShellServiceProvider services)
                        continue;
                    var serviceId = ShellBrowserInterface;
                    var interfaceId = ShellBrowserInterface;
                    DiagnosticPhase = "registered-query-service";
                    if (services.QueryService(ref serviceId, ref interfaceId, out shellBrowser) < 0 || shellBrowser is null ||
                        shellBrowser.GetWindow(out var tab) < 0 || !window.Tabs.Contains(tab) || !seenTabs.Add(tab))
                        continue;

                    var registered = snapshot.Windows.FirstOrDefault(candidate => candidate.Id == window.Id);
                    if (registered is null)
                    {
                        registered = new RegisteredWindow(window.Id);
                        snapshot.Windows.Add(registered);
                    }
                    registered.Tabs.Add(new RegisteredTab(tab, browser));
                    browser = null; // The snapshot now owns this COM reference.
                    if (expectedTabs is not null && seenTabs.SetEquals(expectedTabs))
                    {
                        // Completeness is the requested native handle set, not
                        // uniqueness of global COM rows. Duplicate rows already
                        // resolve to one association per tab. Fresh view/state
                        // reads and the unchanged native cross-check still apply.
                        break;
                    }
                }
                catch (Exception exception) when (exception is COMException or InvalidComObjectException or
                    Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                {
                    // Closed/orphan registrations may be unreadable. They may
                    // be skipped only because the final native cross-check
                    // still requires a registered object for EVERY native tab
                    // in the requested scope, including all target tabs.
                    RecordDiagnosticFailure(exception);
                    if (exception is COMException disconnected && ResetDisconnectedShell(disconnected))
                        throw new ShellSnapshotException();
                }
                finally
                {
                    ComReference.Release(shellBrowser);
                    ComReference.Release(browser);
                }
            }

            DiagnosticPhase = "registered-native-cross-check";
            var after = requiredWindows is null ? ReadNativeWindows() : ReadNativeWindowScope(requiredWindows);
            if ((requiredWindows is null && (int)shellWindows.Count != count) || !NativeWindowSetsEqual(native, after) ||
                native.Count != snapshot.Windows.Count || native.Any(window =>
                    snapshot.FindWindow(window.Id).Tabs.Count != window.Tabs.Length))
                throw new ShellSnapshotException();
            return snapshot;
        }
        catch (Exception exception)
        {
            RecordDiagnosticFailure(exception);
            if (exception is COMException disconnected)
                ResetDisconnectedShell(disconnected);
            snapshot.Dispose();
            throw new ShellSnapshotException();
        }
    }

    private void RefreshShell()
    {
        var shellWindow = ShellNative.GetShellWindow();
        ShellNative.GetWindowThreadProcessId(shellWindow, out var process);
        if (process != _shellProcess)
        {
            ResetShell(retainTransfers: true);
            _shellProcess = process;
        }
        if (process == 0)
            throw new ShellSnapshotException();
        if (_shellWindows is not null)
        {
            EnsureShellWindowEvents();
            return;
        }

        try
        {
            _paths = new ShellPathComparer();
            _shellWindows = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellWindowsClass, true)!);
            if (_shellWindows is null)
                throw new ShellSnapshotException();
            EnsureShellWindowEvents();
        }
        catch
        {
            ResetShell(retainTransfers: true);
            throw new ShellSnapshotException();
        }
    }

    private static List<NativeWindow> ReadNativeWindows()
    {
        var windows = new List<NativeWindow>();
        var processes = new Dictionary<uint, bool>();
        Exception? failure = null;
        if (!ShellNative.EnumWindows((handle, _) =>
            {
                try
                {
                    if (ShellNative.ClassName(handle) != "CabinetWClass" || !ShellNative.IsWindowVisible(handle))
                        return true;
                    ShellNative.GetWindowThreadProcessId(handle, out var processId);
                    if (!processes.TryGetValue(processId, out var explorer))
                    {
                        using var process = Process.GetProcessById(checked((int)processId));
                        explorer = StringComparer.OrdinalIgnoreCase.Equals(process.ProcessName, "explorer");
                        processes.Add(processId, explorer);
                    }
                    if (!explorer)
                        return true;
                    var id = new WindowId(handle, processId);
                    var tabs = ReadNativeTabs(id);
                    windows.Add(new NativeWindow(id, tabs));
                    return true;
                }
                catch (Exception exception)
                {
                    failure = exception;
                    return false;
                }
            }, 0) || failure is not null)
            throw new ShellSnapshotException();
        return windows;
    }

    private static List<NativeWindow> ReadNativeWindowScope(IReadOnlyList<WindowId> requiredWindows)
    {
        if (requiredWindows.Count == 0 || requiredWindows.Distinct().Count() != requiredWindows.Count)
            throw new ShellSnapshotException();
        var result = new List<NativeWindow>(requiredWindows.Count);
        foreach (var id in requiredWindows)
        {
            var tabs = ReadNativeTabs(id);
            if (tabs.Length == 0)
                throw new ShellSnapshotException();
            result.Add(new NativeWindow(id, tabs));
        }
        return result;
    }

    private static nint[] ReadNativeTabs(WindowId window)
    {
        if (!IsExplorerWindow(window))
            throw new ShellSnapshotException();
        var result = new List<nint>();
        nint tab = 0;
        while ((tab = ShellNative.FindWindowEx(window.Handle, tab, "ShellTabWindowClass", null)) != 0)
        {
            ShellNative.GetWindowThreadProcessId(tab, out var processId);
            if (processId != window.ProcessId || result.Contains(tab))
                throw new ShellSnapshotException();
            result.Add(tab);
        }
        return result.ToArray();
    }

    private static bool NativeTabsMatch(WindowId window, IEnumerable<nint> expected) =>
        ReadNativeTabs(window).ToHashSet().SetEquals(expected);

    private static bool NativeWindowSetsEqual(IReadOnlyList<NativeWindow> first, IReadOnlyList<NativeWindow> second) =>
        first.Count == second.Count && first.All(window => second.Any(candidate =>
            candidate.Id == window.Id && candidate.Tabs.ToHashSet().SetEquals(window.Tabs)));

    private static bool IsExplorerWindow(WindowId window)
    {
        if (window.Handle == 0 || window.ProcessId == 0 || !ShellNative.IsWindow(window.Handle) ||
            ShellNative.ClassName(window.Handle) != "CabinetWClass")
            return false;
        ShellNative.GetWindowThreadProcessId(window.Handle, out var process);
        return process == window.ProcessId;
    }

    private bool SameTabContents(TabState first, TabState second) =>
        _paths!.Equivalent(first.Location, second.Location) && first.Selection.Length == second.Selection.Length &&
        first.Selection.All(selected => second.Selection.Any(candidate => _paths.Equivalent(selected.Identity, candidate.Identity)));

    private static bool SameWindowState(WindowState first, WindowState second) =>
        first.Id == second.Id && first.Stamps.Length == second.Stamps.Length &&
        first.Stamps.ToHashSet().SetEquals(second.Stamps);

    private void AssertContext()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Environment.CurrentManagedThreadId != _ownerThread || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA ||
            !Application.MessageLoop || SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
            throw new InvalidOperationException("The Explorer backend requires its owning WinForms STA message pump.");
    }

    private void PruneVanishedTransfers()
    {
        foreach (var transfer in _partialTransfers.Where(transfer => !IsOwnedBackgroundTransfer(transfer) &&
            (!IsExplorerWindow(transfer.Source) || !IsExplorerWindow(transfer.Target))))
            AbandonNativeReservation(transfer);
        _partialTransfers.RemoveAll(transfer => !IsOwnedBackgroundTransfer(transfer) &&
            (!IsExplorerWindow(transfer.Source) || !IsExplorerWindow(transfer.Target)));
        PrunePreparedSource();
        _nativeReservations.Pulse(); // Pending commands remain globally owned until resolved or invalidated.
    }

    private void RecordDiagnosticFailure(Exception exception)
    {
        if (DiagnosticFailureType is not null)
            return;
        DiagnosticFailureType = exception.GetType().FullName;
        DiagnosticFailureHResult = exception.HResult;
    }

    private static long ElapsedMilliseconds(long startedAt) =>
        (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

    private bool ResetDisconnectedShell(COMException exception)
    {
        if (exception.HResult is unchecked((int)0x80010108) or unchecked((int)0x800706BA) or
            unchecked((int)0x80010007) or unchecked((int)0x80010012) or unchecked((int)0x800401FD))
        {
            ResetShell(retainTransfers: true);
            return true;
        }
        return false;
    }

    private void ResetShell(bool retainTransfers = false)
    {
        DisposeShellWindowEvents();
        ComReference.Release(_shellWindows);
        _shellWindows = null;
        _paths?.Dispose();
        _paths = null;
        _preparedOtherSource = null;
        _nextPreparationCandidate = null;
        _nextPreparationTarget = default;
        if (retainTransfers)
            PruneVanishedTransfers();
        else
        {
            foreach (var transfer in _partialTransfers)
                AbandonNativeReservation(transfer);
            _partialTransfers.Clear();
        }
        _generation++;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        AssertContext();
        _acceptBackgroundTransfers = false;
        _disposed = true;
        DisposeShellWindowEvents(); // Detach now; pumped work cannot reattach during Unadvise.
        foreach (var job in _backgroundJobs.Values.ToArray()) job.Cancel();
        if (_backgroundJobs.Count == 0)
        {
            ResetShell();
            DisposeOwnedNativeReservations();
        }
        else
            _releaseShellAfterBackground = true; // Never block the owning STA or release RCWs under a live job.
    }

    private sealed record SelectedItem(string Identity, string Name);

    private sealed class TabState(nint handle, string location, SelectedItem[] selection)
    {
        internal nint Handle { get; } = handle;
        internal string Location { get; } = location;
        internal SelectedItem[] Selection { get; } = selection;
        internal TabTransferStamp Stamp { get; } = new(handle, location, SelectionIdentity(selection));

        private static string SelectionIdentity(IEnumerable<SelectedItem> selected)
        {
            var value = new StringBuilder();
            foreach (var item in selected.OrderBy(item => item.Identity, StringComparer.Ordinal))
                value.Append(item.Identity.Length).Append(':').Append(item.Identity);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString())));
        }
    }

    private sealed class WindowState(WindowId id, TabState[] tabs)
    {
        internal WindowId Id { get; } = id;
        internal TabState[] Tabs { get; } = tabs;
        internal TabTransferStamp[] Stamps { get; } = tabs.Select(tab => tab.Stamp).ToArray();
    }

    private sealed class PartialTransfer(WindowId source, WindowId target, TabState sourceState)
    {
        internal WindowId Source { get; } = source;
        internal WindowId Target { get; } = target;
        internal TabState SourceState { get; set; } = sourceState;
        internal TabState? LastManagedState { get; set; }
        internal TabState? RebaseFrom { get; set; }
        internal NativeTabReservation? NativeReservation { get; set; }
        internal bool AdoptedEarly { get; set; }
        internal bool EarlyDestinationValidated { get; set; }
        internal nint TargetTab { get; set; }
        internal bool NavigationIssued { get; set; }
        internal bool Verified { get; set; }
        internal bool BackgroundOwned { get; set; }
    }

    private sealed record NativeWindow(WindowId Id, nint[] Tabs);
    private sealed record RegisteredTab(nint Handle, object Browser) : IDisposable
    {
        public void Dispose() => ComReference.Release(Browser);
    }

    private sealed class RegisteredWindow(WindowId id)
    {
        internal WindowId Id { get; } = id;
        internal List<RegisteredTab> Tabs { get; } = [];
    }

    private sealed class RegisteredSnapshot : IDisposable
    {
        internal List<RegisteredWindow> Windows { get; } = [];

        internal RegisteredWindow FindWindow(WindowId id) =>
            Windows.SingleOrDefault(window => window.Id == id) ?? throw new ShellSnapshotException();

        public void Dispose()
        {
            foreach (var window in Windows)
                foreach (var tab in window.Tabs)
                    tab.Dispose();
            Windows.Clear();
        }
    }
}
