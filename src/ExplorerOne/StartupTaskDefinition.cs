using System.Xml.Linq;

namespace ExplorerOne;

/// <summary>The portable definition of the current user's interactive logon task.</summary>
internal static class StartupTaskDefinition
{
    private static readonly XNamespace Namespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static string Create(string executablePath, string userSid)
    {
        var fullPath = Path.GetFullPath(executablePath);
        XElement Element(string name, object? content = null) => new(Namespace + name, content);
        return new XDocument(
            new XElement(Namespace + "Task", new XAttribute("version", "1.2"),
                Element("RegistrationInfo", Element("Description", "Starts ExplorerOne at sign-in for the current user.")),
                Element("Triggers", Element("LogonTrigger", new object[]
                {
                    Element("Enabled", true), Element("UserId", userSid), Element("Delay", "PT20S")
                })),
                Element("Principals", new XElement(Namespace + "Principal", new XAttribute("id", "CurrentUser"),
                    Element("UserId", userSid), Element("LogonType", "InteractiveToken"), Element("RunLevel", "LeastPrivilege"))),
                Element("Settings", new object[]
                {
                    Element("MultipleInstancesPolicy", "IgnoreNew"),
                    Element("DisallowStartIfOnBatteries", false), Element("StopIfGoingOnBatteries", false),
                    Element("StartWhenAvailable", true), Element("AllowStartOnDemand", true),
                    Element("Enabled", true), Element("Hidden", false), Element("ExecutionTimeLimit", "PT0S"),
                    Element("Priority", 6), Element("RestartOnFailure", new object[]
                    {
                        Element("Interval", "PT1M"), Element("Count", 3)
                    })
                }),
                new XElement(Namespace + "Actions", new XAttribute("Context", "CurrentUser"),
                    Element("Exec", new object[]
                    {
                        Element("Command", fullPath), Element("WorkingDirectory", Path.GetDirectoryName(fullPath))
                    })))).ToString();
    }

    public static bool Matches(string xml, string executablePath, string userSid, string? userName = null)
    {
        try
        {
            bool IsCurrentUser(string? value) => value == userSid || (userName is not null && string.Equals(value, userName, StringComparison.OrdinalIgnoreCase));
            var root = XDocument.Parse(xml).Root;
            var principal = root?.Element(Namespace + "Principals")?.Elements(Namespace + "Principal").SingleOrDefault();
            var actions = root?.Element(Namespace + "Actions")?.Elements().ToArray();
            var trigger = root?.Element(Namespace + "Triggers")?.Elements(Namespace + "LogonTrigger")
                .Any(element => IsCurrentUser((string?)element.Element(Namespace + "UserId"))
                    && (string?)element.Element(Namespace + "Enabled") != "false") == true;
            var command = actions is { Length: 1 } && actions[0].Name == Namespace + "Exec"
                ? (string?)actions[0].Element(Namespace + "Command") : null;
            return trigger
                && IsCurrentUser((string?)principal?.Element(Namespace + "UserId"))
                && (string?)principal?.Element(Namespace + "LogonType") == "InteractiveToken"
                && ((string?)principal?.Element(Namespace + "RunLevel") is null or "LeastPrivilege")
                && command is not null && PathsEqual(command, executablePath)
                && string.IsNullOrEmpty((string?)actions![0].Element(Namespace + "Arguments"));
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public static bool PathsEqual(string first, string second) =>
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
}
