using ExplorerOne.Shell;

internal static class LocationCases
{
    internal static IEnumerable<(string Name, Func<Task> Run)> All =>
    [
        Case("Decode file URL spaces/hash/literal percent exactly once", "file:///C:/a%20%23%2523", @"C:\a #%23"),
        Case("Keep literal percent sequences in filesystem paths", @"C:\a%20#", @"C:\a%20#"),
        Case("Keep shell namespace parsing names", "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"),
        Case("Decode UNC file URL", "file://server/share/a%20b", @"\\server\share\a b"),
        Case("Keep ordinary UNC path", @"\\server\share\a%20b", @"\\server\share\a%20b"),
        Case("Decode Unicode file URL", "file:///C:/%D0%A2%D0%B5%D1%81%D1%82", @"C:\Тест"),
        Case("Keep non-file shell URL", "search-ms:query=test", "search-ms:query=test"),
        Case("Decode case-insensitive FILE scheme", "FILE:///C:/folder", @"C:\folder")
    ];
    private static (string, Func<Task>) Case(string name, string source, string expected) => (name, () =>
    {
        if (!StringComparer.Ordinal.Equals(ShellLocation.NormalizeParsingName(source), expected))
            throw new InvalidOperationException("Shell parsing-name normalization failed");
        return Task.CompletedTask;
    });
}
