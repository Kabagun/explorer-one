using System.Diagnostics;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using ExplorerOne.Diagnostics;

namespace ExplorerOne;

internal static class Program
{
    private const string InstanceMutexName = @"Local\ExplorerOne.Kabaye";
    private const int CommandFailed = 2;

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 0)
        {
            Environment.ExitCode = RunCommand(args);
            return;
        }

        using var instance = new Mutex(false, InstanceMutexName);
        if (!WaitForInstance(instance, TimeSpan.Zero))
            return; // Opening the Start menu shortcut twice is harmless.

        var reason = "main_return";
        var exitCode = 0;
        try
        {
            AppDiagnostics.Start();
            // Route unexpected UI failures to the same durable fatal record as
            // startup failures, rather than continuing with unknown transfer state.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            reason = Run();
        }
        catch (Exception exception)
        {
            reason = "managed_exception";
            exitCode = 1;
            AppDiagnostics.Fatal("main_exception", exception);
            try
            {
                MessageBox.Show(AppDiagnostics.IsEnabled
                    ? "Произошла ошибка ExplorerOne. Подробности сохранены в журнале приложения."
                    : "Произошла ошибка ExplorerOne. Записать журнал приложения не удалось.",
                    "ExplorerOne", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }
        finally
        {
            Environment.ExitCode = exitCode;
            try { AppDiagnostics.Complete(reason, exitCode); }
            finally { instance.ReleaseMutex(); }
        }
    }

    private static int RunCommand(string[] args)
    {
        if (args.Length != 1)
            return CommandFailed;

        try
        {
            switch (args[0])
            {
                case "--runtime-check":
                    return 0;
                case "--shutdown":
                    return RequestShutdown();
                case "--startup-status":
                    return StartupRegistration.HasEnabledRegistration() ? 0 : 1;
                case "--enable-startup":
                case "--disable-startup":
                    using (var instance = new Mutex(false, InstanceMutexName))
                    {
                        if (!WaitForInstance(instance, TimeSpan.FromSeconds(5)))
                            return CommandFailed;
                        try { StartupRegistration.SetEnabled(args[0] == "--enable-startup"); }
                        finally { instance.ReleaseMutex(); }
                    }
                    return 0;
                default:
                    return CommandFailed;
            }
        }
        catch (Exception)
        {
            // Setup receives an exit status, with no tray, dialog, or journal session.
            return CommandFailed;
        }
    }

    private static int RequestShutdown()
    {
        using var instance = new Mutex(false, InstanceMutexName);
        var eventName = ShutdownEventName();
        var clock = Stopwatch.StartNew();
        // The current version may still be starting. A legacy instance has
        // no event and needs an explicit Exit from its tray menu.
        while (clock.Elapsed < TimeSpan.FromSeconds(2))
        {
            if (WaitForInstance(instance, TimeSpan.FromMilliseconds(100)))
            {
                instance.ReleaseMutex();
                return 0;
            }
            if (EventWaitHandle.TryOpenExisting(eventName, out var request))
            {
                using (request)
                    request.Set();
                if (!WaitForInstance(instance, TimeSpan.FromSeconds(15) - clock.Elapsed))
                    return CommandFailed;
                instance.ReleaseMutex();
                return 0;
            }

        }
        return CommandFailed;
    }

    private static bool WaitForInstance(Mutex instance, TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
            timeout = TimeSpan.Zero;
        try { return instance.WaitOne(timeout); }
        catch (AbandonedMutexException) { return true; }
    }

    private static string ShutdownEventName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("The current user identity is unavailable.");
        return $@"Local\ExplorerOne.Shutdown.{sid}";
    }

    private static string Run()
    {
        ApplicationConfiguration.Initialize();

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            AppDiagnostics.Event("startup_rejected", "unsupported_windows", flush: true);
            MessageBox.Show(
                "Для работы Explorer One требуется Windows 11 версии 22H2 или новее.",
                "Explorer One",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return "unsupported_windows";
        }

        if (IsExplorerTabUtilityRunning())
        {
            AppDiagnostics.Event("startup_rejected", "conflicting_utility", flush: true);
            MessageBox.Show(
                "Обнаружен запущенный Explorer Tab Utility. Закройте его перед запуском Explorer One.",
                "Explorer One",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return "conflicting_utility";
        }

        WindowsFormsSynchronizationContext.AutoInstall = false;
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        using var shutdown = new EventWaitHandle(false, EventResetMode.AutoReset, ShutdownEventName());
        using var context = new TrayApplicationContext(shutdown);
        AppDiagnostics.Ready();
        Application.Run(context);
        return AppDiagnostics.ShutdownReason ?? "message_loop_returned";
    }

    private static bool IsExplorerTabUtilityRunning()
    {
        var processes = Process.GetProcessesByName("ExplorerTabUtility");
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }
}
