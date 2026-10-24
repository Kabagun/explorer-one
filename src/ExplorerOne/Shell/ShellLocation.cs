namespace ExplorerOne.Shell;

internal static class ShellLocation
{
    internal static string NormalizeParsingName(string value)
    {
        // ShellFolder.Self.Path is a parsing name; IWebBrowser2.LocationURL is
        // a URI. Decode only an actual file URI, exactly once. In particular,
        // literal % sequences in ordinary filesystem paths must stay literal.
        return value.StartsWith("file:", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile
                ? uri.LocalPath
                : value;
    }
}
