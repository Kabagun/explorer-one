using ExplorerOne.Core;

namespace ExplorerOne.Shell;

public sealed partial class ShellExplorerBackend
{
    private const int MaximumBackgroundTransfers = 32;
    private const int BackgroundBudgetMilliseconds = 8000;
    private readonly Dictionary<WindowId, BackgroundTransferJob> _backgroundJobs = [];
    private bool _acceptBackgroundTransfers = true;
    private bool _releaseShellAfterBackground;
    private int _failedBackgroundTransfers;

    public int PendingTransferCount => _backgroundJobs.Count;
    public int FailedTransferCount => _failedBackgroundTransfers;
    public event Action? BackgroundTransferChanged;

    public async Task DrainBackgroundTransfersAsync()
    {
        // Draining is also allowed after Dispose requested deferred release.
        if (Environment.CurrentManagedThreadId != _ownerThread || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA ||
            !Application.MessageLoop || SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
            throw new InvalidOperationException("Background transfers require their owning WinForms STA message pump.");
        _acceptBackgroundTransfers = false;
        if (_ownsNativeReservations && !_nativeReservationsDisposed)
            _nativeReservations.StopPreopening();
        while (_backgroundJobs.Count != 0)
            await Task.WhenAll(_backgroundJobs.Values.Select(job => job.Completion).ToArray());
        // An accepted native command can outlive its source hint. Let its
        // exact handle resolve before the final owner-STA orphan cleanup;
        // a hung/ambiguous request stays preserved after this bounded wait.
        var nativeDeadline = Environment.TickCount64 + DestinationTimeoutMilliseconds;
        while (!_nativeReservationsDisposed && _nativeReservations.HasPending &&
            Environment.TickCount64 < nativeDeadline)
        {
            _nativeReservations.Pulse();
            if (_nativeReservations.HasPending)
                await Task.Delay(20);
        }
        if (!_disposed)
            await ProcessPreopenCleanupAsync();
    }

    private bool CanStartBackgroundTransfer(RegisteredWindow source) =>
        _closeSourceEarly && _acceptBackgroundTransfers && _backgroundJobs.Count < MaximumBackgroundTransfers &&
        source.Tabs.Count == 1 && !_partialTransfers.Any(transfer => transfer.Source == source.Id);

    private bool IsOwnedBackgroundTransfer(PartialTransfer transfer) =>
        _backgroundJobs.TryGetValue(transfer.Source, out var job) && ReferenceEquals(job.Transfer, transfer);

    private async Task<TransferResult?> TryQueueBackgroundTransferAsync(WindowState captured, RegisteredTab sourceReference,
        WindowId target, int generation, CancellationToken cancellationToken)
    {
        if (!_acceptBackgroundTransfers || _backgroundJobs.Count >= MaximumBackgroundTransfers)
            return TransferResult.NotReady;
        if (captured.Tabs.Length != 1)
            return null;
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasTransferShell(generation) || !IsExplorerWindow(target) ||
            !NativeTabsMatch(captured.Id, captured.Tabs.Select(tab => tab.Handle)))
            throw new ShellSnapshotException();

        var transfer = GetOrCreateTransfer(captured.Id, target, captured.Tabs[0]);
        transfer.BackgroundOwned = true;
        var job = new BackgroundTransferJob(transfer, captured.Tabs[0], generation);
        _backgroundJobs.Add(captured.Id, job);
        _ = RunBackgroundTransferAsync(job); // Completion is owned before the runner's synchronous prefix starts.
        try
        {
            // The task's synchronous prefix can allocate/post Ctrl+T only.
            // Its fast-only yield precedes every destination COM lookup.
            if (job.Completion.IsCompleted || cancellationToken.IsCancellationRequested || !_acceptBackgroundTransfers ||
                !HasTransferShell(generation) || !IsExplorerWindow(target) ||
                !NativeTabsMatch(captured.Id, [job.Original.Handle]))
            {
                job.Cancel();
                await job.Completion;
                return IsExplorerWindow(captured.Id) ? TransferResult.Retry : TransferResult.SourceGone;
            }
            if (sourceReference.Handle != job.Original.Handle)
                throw new ShellSnapshotException();
            ValidateReference(sourceReference, captured.Id);
            // Source COM validation may pump messages and finish/fail the job.
            // Recheck ownership AFTER it; all remaining pre-commit checks are native.
            if (job.Completion.IsCompleted || !IsOwnedBackgroundTransfer(transfer) ||
                cancellationToken.IsCancellationRequested || !_acceptBackgroundTransfers ||
                !HasTransferShell(generation) || !IsExplorerWindow(target) ||
                !NativeTabsMatch(captured.Id, [job.Original.Handle]))
            {
                job.Cancel();
                await job.Completion;
                return IsExplorerWindow(captured.Id) ? TransferResult.Retry : TransferResult.SourceGone;
            }
            // This fast source was proved to contain exactly its captured tab.
            // Commit BEFORE Quit: COM can pump and the job may finish/fail in
            // that call. This source RCW stays local and is never owned by job.
            job.CloseAccepted = true;
            var closeStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                ((dynamic)sourceReference.Browser).Quit();
            }
            catch
            {
                // A retiring source proxy may disconnect after a successful
                // Quit; that does not mean the ShellWindows collection died.
                if (OriginalSourceStillLive(job))
                {
                    job.Cancel();
                    await job.Completion;
                    return TransferResult.Retry;
                }
            }
            finally { DiagnosticCloseCallMilliseconds += ElapsedMilliseconds(closeStartedAt); }
            // After committed Quit, caller cancellation never owns this job.
            DiagnosticPhase = "background-queued";
            NotifyBackgroundTransferChanged();
            return TransferResult.Queued;
        }
        catch
        {
            if (!job.CloseAccepted)
            {
                job.Cancel();
                await job.Completion;
            }
            throw;
        }
    }

    private async Task RunBackgroundTransferAsync(BackgroundTransferJob job)
    {
        var token = job.Lifetime.Token;
        var verified = false;
        try
        {
            while (!token.IsCancellationRequested && Environment.TickCount64 < job.Deadline)
            {
                if (!HasTransferShell(job.Generation) || !IsExplorerWindow(job.Transfer.Target))
                    break;
                try
                {
                    var attempt = EnsureDestinationAsync(job.Transfer, job.Generation, token);
                    var completedSynchronously = attempt.IsCompleted;
                    bool copied;
                    try { copied = await attempt; }
                    catch when (completedSynchronously && !job.CloseAccepted) { break; }
                    if (copied)
                    {
                        verified = HasTransferShell(job.Generation) &&
                            NativeTabBelongsTo(job.Transfer.Target, job.Transfer.TargetTab) &&
                            job.Transfer.LastManagedState is not null && SameTabContents(job.Original, job.Transfer.LastManagedState);
                        break;
                    }
                    if (completedSynchronously && !job.CloseAccepted)
                        break; // Do not close a source when the initial native request already failed.
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (UnsupportedShellViewException) { break; }
                catch (NativeAllocationAmbiguityException) { break; }
                catch
                {
                    if (!HasTransferShell(job.Generation) || !IsExplorerWindow(job.Transfer.Target)) break;
                }
                await Task.Delay(PollMilliseconds, token);
            }
            // A posted close may remain briefly in Explorer's message queue.
            // Never post it twice; a surviving source keeps its partial copy
            // and falls back to the strict retry path after this job finishes.
            while (verified && OriginalSourceStillLive(job) && !token.IsCancellationRequested &&
                Environment.TickCount64 < job.Deadline)
                await Task.Delay(10, token);
            job.Succeeded = verified && !OriginalSourceStillLive(job);
        }
        catch { /* Only path-free completion/failure counters leave this job. */ }
        finally
        {
            try
            {
                job.Transfer.BackgroundOwned = false;
                if (job.CloseAccepted && !job.Succeeded && _failedBackgroundTransfers < int.MaxValue)
                    _failedBackgroundTransfers++;
                _backgroundJobs.Remove(job.Transfer.Source);
                if (!OriginalSourceStillLive(job))
                {
                    AbandonNativeReservation(job.Transfer);
                    _partialTransfers.Remove(job.Transfer);
                }
                job.Lifetime.Dispose();
                if (_releaseShellAfterBackground && _backgroundJobs.Count == 0)
                {
                    _releaseShellAfterBackground = false;
                    try { ResetShell(); }
                    catch { /* Shared release must not fault an already drained job. */ }
                    finally { DisposeOwnedNativeReservations(); }
                }
                NotifyBackgroundTransferChanged();
            }
            finally { job.Finish(); }
        }
    }

    private static bool OriginalSourceStillLive(BackgroundTransferJob job) =>
        NativeTabBelongsTo(job.Transfer.Source, job.Original.Handle);

    private void NotifyBackgroundTransferChanged()
    {
        try { BackgroundTransferChanged?.Invoke(); }
        catch { /* A UI notification cannot change transfer ownership/outcome. */ }
    }

    private bool HasTransferShell(int generation)
    {
        if (generation != _generation || _shellWindows is null || _paths is null) return false;
        ShellNative.GetWindowThreadProcessId(ShellNative.GetShellWindow(), out var process);
        return process != 0 && process == _shellProcess;
    }

    private void RefreshTransferShell(PartialTransfer transfer, int generation)
    {
        if (!transfer.BackgroundOwned)
            RefreshShell();
        else if (!HasTransferShell(generation))
            throw new ShellSnapshotException(); // No synchronous COM reinitialization before accepted close.
    }

    private sealed class BackgroundTransferJob
    {
        internal PartialTransfer Transfer { get; }
        internal TabState Original { get; }
        internal int Generation { get; }
        internal long Deadline { get; } = Environment.TickCount64 + BackgroundBudgetMilliseconds;
        internal CancellationTokenSource Lifetime { get; } = new(BackgroundBudgetMilliseconds);
        private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Completion => _completion.Task;
        internal bool CloseAccepted { get; set; }
        internal bool Succeeded { get; set; }

        internal BackgroundTransferJob(PartialTransfer transfer, TabState original, int generation)
        {
            Transfer = transfer;
            Original = original; // Full immutable snapshot; never rebase a queued source.
            Generation = generation;
        }

        internal void Cancel()
        {
            try { Lifetime.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        internal void Finish() => _completion.TrySetResult(true);
    }
}
