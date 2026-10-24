using ExplorerOne.Shell;

internal static class SafetyCases
{
    private static readonly TabTransferStamp A = new(10, "folder-a", "[]");
    private static readonly TabTransferStamp B = new(20, "folder-b", "[item-b]");
    private static readonly VerifiedTabTransfer CopyA = new(10, A with { Tab = 110 });
    private static readonly VerifiedTabTransfer CopyB = new(20, B with { Tab = 120 });

    internal static IEnumerable<(string Name, Func<Task> Run)> All =>
    [
        Case("Close guard accepts only complete unchanged verified transfer", () => Guard([A, B], [A, B], [CopyA, CopyB])),
        Case("Close guard is independent of tab enumeration order", () => Guard([A, B], [B, A], [CopyB, CopyA])),
        Case("Close guard rejects an empty source", () => !Guard([], [], [])),
        Case("Close guard rejects a missing source tab", () => !Guard([A, B], [A], [CopyA, CopyB])),
        Case("Close guard rejects a newly added source tab", () => !Guard([A], [A, B], [CopyA])),
        Case("Close guard rejects unverified destination", () => !Guard([A, B], [A, B], [CopyA])),
        Case("Close guard rejects changed source folder", () => !Guard([A], [A with { Location = "elsewhere" }], [CopyA])),
        Case("Close guard rejects changed source selection", () => !Guard([B], [B with { Selection = "[]" }], [CopyB])),
        Case("Close guard rejects wrong destination folder", () => !Guard([A], [A], [CopyA with { Destination = CopyA.Destination with { Location = "elsewhere" } }])),
        Case("Close guard rejects lost destination selection", () => !Guard([B], [B], [CopyB with { Destination = CopyB.Destination with { Selection = "[]" } }])),
        Case("Close guard rejects duplicated source handles", () => !Guard([A, A], [A, A], [CopyA, CopyA])),
        Case("Close guard rejects duplicated destination handles", () => !Guard([A, B], [A, B], [CopyA, CopyB with { Destination = CopyB.Destination with { Tab = 110 } }])),
        Case("Close guard rejects a source handle masquerading as destination", () => !Guard([A], [A], [new(10, A)])),
        Case("Close guard rejects any overlap between source and destination tabs", () => !Guard([A, B], [A, B], [CopyA with { Destination = CopyA.Destination with { Tab = 20 } }, CopyB])),
        Case("Close guard rejects a null destination HWND", () => !Guard([A], [A], [CopyA with { Destination = CopyA.Destination with { Tab = 0 } }])),
        Case("Close guard rejects missing shell identity", () => !Guard([A with { Location = "" }], [A with { Location = "" }], [CopyA])),
        Case("Close guard does not fold case-sensitive identities", () => !Guard([A], [A], [CopyA with { Destination = CopyA.Destination with { Location = "FOLDER-A" } }]))
    ];

    private static bool Guard(TabTransferStamp[] original, TabTransferStamp[] current, VerifiedTabTransfer[] destinations) =>
        TransferSafety.CanCloseSource(original, current, destinations);
    private static (string, Func<Task>) Case(string name, Func<bool> predicate) => (name, () =>
    {
        if (!predicate()) throw new InvalidOperationException("Unsafe close decision");
        return Task.CompletedTask;
    });
}
