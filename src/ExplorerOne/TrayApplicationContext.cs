using System.Drawing;
using System.Windows.Forms;
using ExplorerOne.Core;
using ExplorerOne.Shell;

namespace ExplorerOne;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string GenericPollError = "Не удалось выполнить проверку.";

    private ShellExplorerBackend? _backend;
    private WindowCoordinator? _coordinator;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _statusItem = new("Ожидание Проводника") { Enabled = false };
    private readonly ToolStripMenuItem _autoStartItem = new("Запускать при входе в Windows");
    private readonly ToolStripMenuItem _exitItem = new("Выход");
    private readonly NotifyIcon _notifyIcon = new()
    {
        Icon = SystemIcons.Application,
        Text = "Explorer One",
        Visible = false
    };
    private readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 600 };

    private Task? _activePoll;
    private bool _autoStartEnabled;
    private bool _shuttingDown;

    public TrayApplicationContext()
    {
        try
        {
            _menu.Items.Add(_statusItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_autoStartItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_exitItem);

            _notifyIcon.ContextMenuStrip = _menu;
            _pollTimer.Tick += OnPollTimerTick;
            _autoStartItem.Click += OnAutoStartClick;
            _exitItem.Click += OnExitClick;

            try
            {
                _autoStartEnabled = StartupRegistration.IsEnabled();
                _autoStartItem.Checked = _autoStartEnabled;
            }
            catch
            {
                _autoStartItem.Enabled = false;
                _statusItem.Text = "Настройки автозапуска недоступны.";
            }

            _notifyIcon.Visible = true;
            _pollTimer.Start();
        }
        catch
        {
            _pollTimer.Dispose();
            _notifyIcon.Dispose();
            _menu.Dispose();
            _lifetime.Dispose();
            throw;
        }
    }

    private void OnPollTimerTick(object? sender, EventArgs e)
    {
        if (_shuttingDown || _activePoll is { IsCompleted: false })
            return;

        if (_coordinator is null && !TryInitializeBackend())
            return;

        _activePoll = PollSafelyAsync(_coordinator!);
    }

    private bool TryInitializeBackend()
    {
        ShellExplorerBackend? backend = null;
        try
        {
            backend = new ShellExplorerBackend();
            _coordinator = new WindowCoordinator(backend);
            _backend = backend;
            return true;
        }
        catch
        {
            try { backend?.Dispose(); } catch { }
            _coordinator = null;
            _backend = null;
            _statusItem.Text = GenericPollError;
            return false;
        }
    }

    private async Task PollSafelyAsync(WindowCoordinator coordinator)
    {
        try
        {
            await coordinator.PollAsync(_lifetime.Token);
            if (!_shuttingDown)
                _statusItem.Text = coordinator.Status;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Shutdown cancels waits; the active coordinator call is awaited before disposal.
        }
        catch
        {
            if (!_shuttingDown)
                _statusItem.Text = GenericPollError;
        }
    }

    private void OnAutoStartClick(object? sender, EventArgs e)
    {
        var enabled = !_autoStartEnabled;
        try
        {
            StartupRegistration.SetEnabled(enabled);
            _autoStartEnabled = StartupRegistration.IsEnabled();
            _autoStartItem.Checked = _autoStartEnabled;
            _statusItem.Text = _autoStartEnabled ? "Автозапуск включён." : "Автозапуск выключен.";
        }
        catch
        {
            _autoStartItem.Checked = _autoStartEnabled;
            _statusItem.Text = "Не удалось изменить автозапуск.";
        }
    }

    private void OnExitClick(object? sender, EventArgs e) => _ = ShutdownSafelyAsync();

    private async Task ShutdownSafelyAsync()
    {
        if (_shuttingDown)
            return;

        _shuttingDown = true;
        try
        {
            try
            {
                _exitItem.Enabled = false;
                _autoStartItem.Enabled = false;
                _pollTimer.Stop();
                _pollTimer.Tick -= OnPollTimerTick;
                _notifyIcon.Visible = false;
            }
            catch { }

            try { _lifetime.Cancel(); } catch { }

            try
            {
                if (_activePoll is not null)
                    await _activePoll;
            }
            catch { }
        }
        finally
        {
            DisposeResources();
            try { ExitThread(); } catch { }
        }
    }

    private void DisposeResources()
    {
        try { _notifyIcon.Visible = false; } catch { }
        try { _notifyIcon.Dispose(); } catch { }
        try { _menu.Dispose(); } catch { }
        try { _pollTimer.Dispose(); } catch { }
        try { _lifetime.Dispose(); } catch { }
        try { _backend?.Dispose(); } catch { }
        _backend = null;
        _coordinator = null;
    }
}
