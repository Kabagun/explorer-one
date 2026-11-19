using Microsoft.Win32;

namespace ExplorerOne.Updating;

internal static class UpdateRegistration
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{D47627FB-9A4B-49AD-8B52-444733D91F80}_is1";

    internal static bool MatchesInstallation(string executable, Version version)
    {
        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var registration = user.OpenSubKey(UninstallKey);
        var directory = registration?.GetValue("InstallLocation") as string;
        return directory is not null && Path.IsPathFullyQualified(directory) &&
            (registration?.GetValue("DisplayVersion") as string) == version.ToString(3) &&
            StartupTaskDefinition.PathsEqual(Path.Combine(directory, "ExplorerOne.exe"), executable);
    }
}
