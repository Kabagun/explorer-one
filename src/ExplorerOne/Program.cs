using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

namespace ExplorerOne;

internal static class Program
{
    private const string InstanceMutexName = @"Local\ExplorerOne.Kabaye";

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            MessageBox.Show(
                "Для работы Explorer One требуется Windows 11 версии 22H2 или новее.",
                "Explorer One",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        using var instanceMutex = new Mutex(true, InstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "Explorer One уже запущен.",
                "Explorer One",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            if (IsExplorerTabUtilityRunning())
            {
                MessageBox.Show(
                    "Обнаружен запущенный Explorer Tab Utility. Закройте его перед запуском Explorer One.",
                    "Explorer One",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            WindowsFormsSynchronizationContext.AutoInstall = false;
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

            using var context = new TrayApplicationContext();
            Application.Run(context);
        }
        catch
        {
            MessageBox.Show(
                "Не удалось запустить Explorer One.",
                "Explorer One",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
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
