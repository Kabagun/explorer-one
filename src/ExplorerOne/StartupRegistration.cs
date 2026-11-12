using Microsoft.Win32;

namespace ExplorerOne;

/// <summary>Controls this application's per-user interactive logon task and migrates its old Run value.</summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ExplorerOne";

    public static bool IsEnabled()
    {
        using var scheduler = new StartupTaskScheduler();
        var taskEnabled = scheduler.IsEnabled(ExecutablePath);
        // A present but disabled/mismatched task must not be mistaken for an active registration.
        return taskEnabled || IsLegacyEnabled();
    }

    /// <summary>Called at startup once the app is running; read-only status checks never change registration.</summary>
    public static void MigrateIfEnabled()
    {
        if (IsLegacyEnabled())
            SetEnabled(true);
    }

    public static void SetEnabled(bool enabled)
    {
        using var scheduler = new StartupTaskScheduler();
        if (enabled)
        {
            var executablePath = ExecutablePath;
            scheduler.Register(executablePath);
            if (!scheduler.IsEnabled(executablePath))
                throw new InvalidOperationException("The per-user startup task could not be verified.");
        }
        else
        {
            scheduler.Delete();
        }

        // Remove the old entry only after the replacement is successfully registered and verified.
        // Deleting a task does not stop the currently running application.
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        runKey?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static string ExecutablePath => !string.IsNullOrWhiteSpace(Environment.ProcessPath)
        ? Path.GetFullPath(Environment.ProcessPath)
        : throw new InvalidOperationException("The executable path is unavailable.");

    private static bool IsLegacyEnabled()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        if (runKey?.GetValue(ValueName) is not string { Length: > 0 })
            return false;

        // Respect a previous explicit disable in Windows Startup Apps during migration.
        foreach (var view in new[] { "Run", "Run32" })
        {
            using var approved = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\{view}");
            if (approved?.GetValue(ValueName) is byte[] { Length: >= 4 } state && (state[0] & 1) != 0)
                return false;
        }
        return true;
    }
}
