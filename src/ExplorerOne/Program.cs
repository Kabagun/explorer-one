using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using ExplorerOne.Diagnostics;

namespace ExplorerOne;

internal static class Program
{
    private const string InstanceMutexName = @"Local\ExplorerOne.Kabaye";

    [STAThread]
    private static void Main(string[] args)
    {
        var reason = "main_return";
        var exitCode = 0;
        try
        {
            AppDiagnostics.Start();
            // Route unexpected UI failures to the same durable fatal record as
            // startup failures, rather than continuing with unknown transfer state.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            reason = Run(args.Contains("--enable-startup", StringComparer.Ordinal));
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
            AppDiagnostics.Complete(reason, exitCode);
        }
    }

    private static string Run(bool enableStartup)
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

        using var instanceMutex = new Mutex(true, InstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            AppDiagnostics.Event("startup_rejected", "already_running", flush: true);
            MessageBox.Show(
                "Explorer One уже запущен.",
                "Explorer One",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return "already_running";
        }

        try
        {
            if (IsExplorerTabUtilityRunning())
            {
                AppDiagnostics.Event("startup_rejected", "conflicting_utility", flush: true);
                MessageBox.Show(
                    "Обнаружен запущенный Explorer Tab Utility. Закройте его перед запуском Explorer One.",
                    "Explorer One",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return "conflicting_utility";
            }

            WindowsFormsSynchronizationContext.AutoInstall = false;
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

            using var context = new TrayApplicationContext(enableStartup);
            AppDiagnostics.Ready();
            Application.Run(context);
            return AppDiagnostics.ShutdownReason ?? "message_loop_returned";
        }
        finally
        {
            instanceMutex.ReleaseMutex();
        }
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
