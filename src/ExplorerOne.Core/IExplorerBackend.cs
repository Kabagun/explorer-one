namespace ExplorerOne.Core;

// A process ID prevents a recycled HWND from inheriting an old routing decision.
public readonly record struct WindowId(nint Handle, uint ProcessId);
public sealed record ExplorerWindow(WindowId Id, int TabCount);
public enum TransferResult { Completed, SourceGone, Retry, Unsafe }

public interface IExplorerBackend
{
    // Must throw on an incomplete enumeration rather than report a partial snapshot.
    IReadOnlyList<ExplorerWindow> EnumerateWindows();
    // Completed is legal only after all source tabs were verified at the destination
    // and a final unchanged-source check passed. All failures leave the source open.
    Task<TransferResult> TransferAsync(WindowId source, WindowId target, CancellationToken cancellationToken);
}
