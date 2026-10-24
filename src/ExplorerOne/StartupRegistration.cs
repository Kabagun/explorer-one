using Microsoft.Win32;

namespace ExplorerOne;

/// <summary>Controls this application's per-user Run value only.</summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ExplorerOne";

    // Windows may separately disable startup through StartupApproved. The menu reflects this Run value.
    public static bool IsEnabled()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return runKey?.GetValue(ValueName) is string { Length: > 0 };
    }

    public static void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            runKey?.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new InvalidOperationException("The executable path is unavailable.");

        using var writableRunKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("The per-user startup key is unavailable.");

        writableRunKey.SetValue(ValueName, $"\"{Path.GetFullPath(executablePath)}\"", RegistryValueKind.String);
    }
}
