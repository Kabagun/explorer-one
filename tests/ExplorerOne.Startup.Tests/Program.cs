using ExplorerOne;
using System.Runtime.InteropServices;
using System.Xml.Linq;

var userSid = "S-1-5-21-100-200-300-1000";
var executable = @"C:\Portable apps\ExplorerOne & tools\ExplorerOne.exe";
XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
var xml = StartupTaskDefinition.Create(executable, userSid);
Check(StartupTaskDefinition.Matches(xml, executable, userSid), "Valid task and XML-escaped executable match.");
Check(!StartupTaskDefinition.Matches(xml, @"C:\Other\ExplorerOne.exe", userSid), "A stale executable registration is rejected.");
Check(!StartupTaskDefinition.Matches(xml, executable, "S-1-5-21-100-200-300-1001"), "Another user is rejected.");
Check(!StartupTaskDefinition.Matches("broken xml", executable, userSid), "Corrupt registration is rejected.");
var normalized = XDocument.Parse(xml);
normalized.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!.Value = @"EXAMPLE\User";
normalized.Descendants(ns + "RunLevel").Single().Remove();
Check(StartupTaskDefinition.Matches(normalized.ToString(), executable, userSid, @"EXAMPLE\User"), "Windows-normalized account names and default least privilege match.");
var document = XDocument.Parse(xml);
Check((string?)document.Descendants(ns + "Delay").Single() == "PT20S", "Logon delay allows the shell and drives to initialize.");
Check((string?)document.Descendants(ns + "ExecutionTimeLimit").Single() == "PT0S", "Background process is not killed after the default 72 hours.");
Check((string?)document.Descendants(ns + "Count").Single() == "3", "Failure recovery is configured for three retries.");
Check((string?)document.Descendants(ns + "DisallowStartIfOnBatteries").Single() == "false"
    && (string?)document.Descendants(ns + "StopIfGoingOnBatteries").Single() == "false", "Battery transitions do not suppress startup or kill the process.");
document.Descendants(ns + "LogonTrigger").Single().Element(ns + "Enabled")!.Value = "false";
Check(!StartupTaskDefinition.Matches(document.ToString(), executable, userSid), "Disabled logon trigger is rejected.");
document = XDocument.Parse(xml);
document.Descendants(ns + "RunLevel").Single().Value = "HighestAvailable";
Check(!StartupTaskDefinition.Matches(document.ToString(), executable, userSid), "Elevated task is rejected.");
document = XDocument.Parse(xml);
document.Descendants(ns + "Exec").Single().Add(new XElement(ns + "Arguments", "--unexpected"));
Check(!StartupTaskDefinition.Matches(document.ToString(), executable, userSid), "Unexpected command arguments are rejected.");

if (args.Contains("--integration"))
{
    // Uses only a unique temporary task. Does not read or change the real app's startup state.
    var taskName = "ExplorerOne-startup-tests-" + Guid.NewGuid().ToString("N");
    using var scheduler = new StartupTaskScheduler(taskName);
    var safeExecutable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
    try
    {
        Check(!scheduler.IsEnabled(safeExecutable), "Missing temporary task is reported disabled.");
        scheduler.Register(safeExecutable);
        Check(scheduler.IsEnabled(safeExecutable), "Real scheduler accepts and verifies the least-privilege interactive logon task.");
        Check(!scheduler.IsEnabled(executable), "Scheduler status rejects a different executable.");
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        object? folder = null;
        object? task = null;
        try
        {
            service.Connect();
            folder = service.GetFolder(@"\");
            task = ((dynamic)folder).GetTask(taskName);
            ((dynamic)task).Enabled = false;
            Check(!scheduler.IsEnabled(safeExecutable), "Windows-disabled task is reported disabled.");
        }
        finally
        {
            foreach (var value in new[] { task, folder, (object)service })
                if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }
    }
    finally
    {
        scheduler.Delete();
        Check(!scheduler.IsEnabled(safeExecutable), "Temporary task deletion is verified.");
    }
}
Console.WriteLine("Startup registration checks passed.");

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine("PASS " + message);
}
