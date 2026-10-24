using Microsoft.Win32;

namespace ExplorerOne.Opening;

/// <summary>Only this application's routing preference; no Shell associations.</summary>
internal static class FolderRoutingPreference
{
    private const string KeyPath = @"Software\ExplorerOne";
    private const string ValueName = "DirectFolderTabs";

    internal static bool Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName) is not int enabled || enabled != 0;
    }

    internal static void Write(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)
            ?? throw new InvalidOperationException("The routing preference is unavailable.");
        key.SetValue(ValueName, enabled ? 1 : 0, RegistryValueKind.DWord);
    }
}
