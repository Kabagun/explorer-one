using System.Drawing;
using System.Windows.Forms;
using ExplorerOne.Core;
using ExplorerOne.Shell;

namespace ExplorerOne;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string GenericPollError = "Не удалось выполнить проверку.";
    private const int IdlePollMilliseconds = 1000;
    private const int FallbackPollMilliseconds = 150;
    private const int RegistrationPollMilliseconds = 60;
    private const int RegistrationBurstMilliseconds = 2000;
    private const int FailedPollMilliseconds = 100;
    private const int MinimumPendingPollMilliseconds = 25;
    private const int MaximumPollsPerDrain = 4;

    private ShellExplorerBackend? _backend;
    private WindowCoordinator? _coordinator;
    private ExplorerWakeupMonitor? _wakeupMonitor;
    private SourceWindowPresentation? _presentation;
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
    private readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = MinimumPendingPollMilliseconds };

    private Task? _activePoll;
    private long _registrationBurstUntil;
    private bool _wakeupMonitorInitialized;
    private bool _pollRequested;
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
        _pollTimer.Stop();
        if (_shuttingDown)
            return;

        _presentation?.Observe(_coordinator?.MainWindow);
        if (_activePoll is { IsCompleted: false })
        {
            _pollRequested = true;
            SchedulePresentationExpiry();
            return;
        }

        _activePoll = DrainPollsSafelyAsync();
        SchedulePresentationExpiry();
    }

    private void OnExplorerWindowShown(WindowId source)
    {
        if (_shuttingDown)
            return;

        _presentation?.OnShown(source, _coordinator?.MainWindow);
        if (_activePoll is { IsCompleted: false })
            SchedulePresentationExpiry();
    }

    private void SchedulePresentationExpiry()
    {
        if (_presentation?.NextExpiryDelay is { } delay)
            SchedulePoll((int)Math.Ceiling(Math.Clamp(delay.TotalMilliseconds, 1, int.MaxValue)));
    }

    private void OnExplorerWindowLifecycle()
    {
        if (_shuttingDown)
            return;

        // Out-of-context WinEvents arrive on this STA, including while a COM
        // call pumps messages. Only request work; never enter the coordinator.
        _pollRequested = true;
        _registrationBurstUntil = Environment.TickCount64 + RegistrationBurstMilliseconds;
        if (_activePoll is not { IsCompleted: false })
            SchedulePoll(1);
    }

    private void SchedulePoll(int milliseconds)
    {
        if (_shuttingDown)
            return;

        if (_presentation?.NextExpiryDelay is { } expiry)
            milliseconds = Math.Min(milliseconds, (int)Math.Ceiling(Math.Clamp(expiry.TotalMilliseconds, 1, int.MaxValue)));
        _pollTimer.Stop();
        _pollTimer.Interval = milliseconds;
        _pollTimer.Start();
    }

    private bool RegistrationBurstActive => Environment.TickCount64 < _registrationBurstUntil;

    private int NextPollMilliseconds(WindowCoordinator coordinator)
    {
        if (_pollRequested || coordinator.HasReadyPending)
            return 1;

        var delay = _wakeupMonitor is { IsActive: true } ? IdlePollMilliseconds : FallbackPollMilliseconds;
        if (coordinator.NextPendingDelay is { } pendingDelay)
        {
            // A source in backoff must not cause an immediate drain loop. The
            // safety poll still discovers vanished windows or missed events.
            var pendingMilliseconds = (int)Math.Ceiling(Math.Clamp(
                pendingDelay.TotalMilliseconds, MinimumPendingPollMilliseconds, int.MaxValue));
            delay = Math.Min(delay, pendingMilliseconds);
        }

        // Window creation can precede ShellWindows registration. Keep these
        // retries bounded to a short burst instead of polling COM rapidly idle.
        if (RegistrationBurstActive)
            delay = Math.Min(delay, RegistrationPollMilliseconds);
        return Math.Max(MinimumPendingPollMilliseconds, delay);
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

    private async Task DrainPollsSafelyAsync()
    {
        // Publish _activePoll before any native/COM operation can reenter the
        // message pump. Backend creation must also happen after the pump starts.
        await Task.Yield();
        var nextPoll = FailedPollMilliseconds;
        try
        {
            if (_shuttingDown)
                return;

            if (!_wakeupMonitorInitialized)
            {
                _presentation ??= new SourceWindowPresentation();
                _wakeupMonitorInitialized = true;
                _wakeupMonitor = new ExplorerWakeupMonitor(OnExplorerWindowLifecycle, OnExplorerWindowShown);
            }
            if (_coordinator is null && !TryInitializeBackend())
                return;

            var coordinator = _coordinator!;
            for (var poll = 0; poll < MaximumPollsPerDrain; poll++)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                _pollRequested = false;
                _presentation?.Observe(coordinator.MainWindow);
                await coordinator.PollAsync(_lifetime.Token);
                if (coordinator.LastTransfer is { } transfer)
                    _presentation?.OnTransfer(transfer.Source, transfer.Result);
                _presentation?.Observe(coordinator.MainWindow);
                if (_shuttingDown)
                    return;

                _statusItem.Text = coordinator.Status;
                if (!coordinator.HasReadyPending && !_pollRequested)
                    break;

                // TransferAsync may finish synchronously. Let tray/shutdown and
                // queued lifecycle events run between source windows anyway.
                await Task.Yield();
            }
            nextPoll = NextPollMilliseconds(coordinator);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            _presentation?.RestoreAll();
            // Shutdown cancels waits; the active coordinator call is awaited before disposal.
        }
        catch
        {
            if (!_shuttingDown)
                _statusItem.Text = GenericPollError;
            // An incomplete enumeration must end this drain. Give Explorer a
            // short chance to register before taking another complete snapshot.
            nextPoll = RegistrationBurstActive ? RegistrationPollMilliseconds : FailedPollMilliseconds;
        }
        finally
        {
            if (!_shuttingDown)
                SchedulePoll(nextPoll);
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
                _wakeupMonitor?.Dispose();
                _presentation?.RestoreAll();
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
        try { _wakeupMonitor?.Dispose(); } catch { }
        _wakeupMonitor = null;
        try { _presentation?.Dispose(); } catch { }
        _presentation = null;
        try { _lifetime.Dispose(); } catch { }
        try { _backend?.Dispose(); } catch { }
        _backend = null;
        _coordinator = null;
    }
}
