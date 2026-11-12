using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ExplorerOne;

internal sealed class StartupTaskScheduler : IDisposable
{
    private const int TaskCreateOrUpdate = 6;
    private const int InteractiveToken = 3;
    private const int FileNotFound = unchecked((int)0x80070002);
    private readonly List<object> _comObjects = [];
    private readonly dynamic _folder;
    private readonly string _userSid;
    private readonly string _userName;
    private readonly string _taskName;

    public StartupTaskScheduler(string? taskName = null)
    {
        using var identity = WindowsIdentity.GetCurrent();
        _userSid = identity.User?.Value ?? throw new InvalidOperationException("The current user identity is unavailable.");
        _userName = identity.Name;
        _taskName = taskName ?? $"ExplorerOne-{_userSid}";
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service")
                ?? throw new InvalidOperationException("Windows Task Scheduler is unavailable.");
            dynamic service = Track(Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("Windows Task Scheduler is unavailable."));
            service.Connect();
            _folder = Track(service.GetFolder(@"\"));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool IsEnabled(string executablePath)
    {
        try
        {
            dynamic task = Track(_folder.GetTask(_taskName));
            return (bool)task.Enabled && StartupTaskDefinition.Matches((string)task.Xml, executablePath, _userSid, _userName);
        }
        catch (Exception exception) when (exception.HResult == FileNotFound)
        {
            return false;
        }
    }

    public void Register(string executablePath) => Track(_folder.RegisterTask(_taskName,
        StartupTaskDefinition.Create(executablePath, _userSid), TaskCreateOrUpdate, _userSid, null, InteractiveToken, null));

    public void Delete()
    {
        try { _folder.DeleteTask(_taskName, 0); }
        catch (Exception exception) when (exception.HResult == FileNotFound) { }
    }

    private object Track(object value)
    {
        if (!_comObjects.Contains(value, ReferenceEqualityComparer.Instance)) _comObjects.Add(value);
        return value;
    }

    public void Dispose()
    {
        for (var index = _comObjects.Count - 1; index >= 0; index--)
            if (Marshal.IsComObject(_comObjects[index]))
                Marshal.FinalReleaseComObject(_comObjects[index]);
        _comObjects.Clear();
    }
}
