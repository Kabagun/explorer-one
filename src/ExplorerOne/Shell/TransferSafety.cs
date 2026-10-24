namespace ExplorerOne.Shell;

// These contain canonical shell identities, never display names or URL text.
internal readonly record struct TabTransferStamp(nint Tab, string Location, string Selection);
internal readonly record struct VerifiedTabTransfer(nint SourceTab, TabTransferStamp Destination);

// Kept independent of COM so the final destructive-action gate can be tested.
internal static class TransferSafety
{
    internal static bool CanCloseSource(
        IReadOnlyList<TabTransferStamp> original,
        IReadOnlyList<TabTransferStamp> current,
        IReadOnlyList<VerifiedTabTransfer> destinations)
    {
        if (original.Count == 0 || original.Count != current.Count || original.Count != destinations.Count)
            return false;

        var sourceTabs = new HashSet<nint>();
        var targetTabs = new HashSet<nint>();
        foreach (var tab in original)
        {
            if (tab.Tab == 0 || string.IsNullOrEmpty(tab.Location) || string.IsNullOrEmpty(tab.Selection) ||
                !sourceTabs.Add(tab.Tab))
                return false;

            var unchanged = current.Where(candidate => candidate.Tab == tab.Tab).ToArray();
            var copied = destinations.Where(candidate => candidate.SourceTab == tab.Tab).ToArray();
            if (unchanged.Length != 1 || unchanged[0] != tab || copied.Length != 1)
                return false;

            var destination = copied[0].Destination;
            if (destination.Tab == 0 || original.Any(source => source.Tab == destination.Tab) ||
                !targetTabs.Add(destination.Tab) ||
                !StringComparer.Ordinal.Equals(tab.Location, destination.Location) ||
                !StringComparer.Ordinal.Equals(tab.Selection, destination.Selection))
                return false;
        }

        return true;
    }
}
