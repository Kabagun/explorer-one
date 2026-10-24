namespace ExplorerOne.Core;

// A process ID prevents a recycled HWND from inheriting an old routing decision.
public readonly record struct WindowId(nint Handle, uint ProcessId);
public sealed record ExplorerWindow(WindowId Id, int TabCount);
public enum TransferResult { Completed, SourceGone, Retry, Unsafe, NotReady, Queued }

public interface IExplorerBackend
{
    // Must report a complete native-window set, even while individual COM views
    // are still registering. View readiness belongs to TransferAsync.
    IReadOnlyList<ExplorerWindow> EnumerateWindows();
    // Completed requires verified destination contents and an unchanged-source gate.
    // Queued is the explicitly selected early-close policy: the source close was
    // requested and an owned background copy retains its captured folder/selection.
    Task<TransferResult> TransferAsync(WindowId source, WindowId target, CancellationToken cancellationToken);
}

public interface IExplorerBackgroundBackend
{
    int PendingTransferCount { get; }
    int FailedTransferCount { get; }
}

/// <summary>Allows bounded preparation of another source already approved for routing.</summary>
public interface IExplorerPreparationBackend
{
    // Applies only to the next TransferAsync call. Null clears any previous plan.
    // Preparation may copy a tab, but must leave the candidate source open.
    void SetPreparationCandidate(WindowId? candidate, WindowId target);
}
