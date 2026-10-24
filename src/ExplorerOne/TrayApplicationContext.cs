using System.Drawing;
using System.Collections.Concurrent;
using System.Windows.Forms;
using ExplorerOne.Core;
using ExplorerOne.Shell;

namespace ExplorerOne;

internal sealed partial class TrayApplicationContext : ApplicationContext
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
    private readonly NativeTabReservations _nativeReservations = new();
    private readonly ConcurrentDictionary<(WindowId Source, bool Created), byte> _appearanceWork = new();
    private int _presentationPulseQueued;
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
    private volatile bool _shuttingDown;
    private int _reportedBackgroundFailures;
    private int _capturePrimeAttempts;
    private long _nextCapturePrimeAttemptAt;
    private bool _capturePrimed;

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
            InitializeFolderOpening();
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

        // Called on the native-only watcher thread; presentation owns its
        // synchronized main-window snapshot and makes no COM/UI-timer calls.
        PreopenNewFrame(source, created: false);
        QueueAppearance(source, created: false);
    }

    private void OnExplorerWindowCreated(WindowId source)
    {
        if (_shuttingDown) return;
        PreopenNewFrame(source, created: true);
        QueueAppearance(source, created: true);
    }

    private void PreopenNewFrame(WindowId source, bool created)
    {
        // The native command precedes appearance writes and every source COM
        // read. The backend later claims this exact reservation for copying.
        if (!_shuttingDown && !_directFolderTabsEnabled &&
            _presentation?.TryGetPreopenTarget(source, created, out var target) == true)
            _nativeReservations.TryPreopen(source, target);
    }

    private void QueueAppearance(WindowId source, bool created)
    {
        var key = (source, created);
        if (_shuttingDown || !_appearanceWork.TryAdd(key, 0)) return;
        _ = Task.Run(() =>
        {
            try
            {
                if (_shuttingDown) return;
                if (created) _presentation?.OnCreated(source);
                else _presentation?.OnShown(source);
            }
            catch { /* Appearance failure cannot stop native tab requests. */ }
            finally { _appearanceWork.TryRemove(key, out _); }
        });
    }

    private void OnExplorerWindowExcluded(WindowId source)
    {
        if (!_shuttingDown)
            _presentation?.OnTransfer(source, TransferResult.Unsafe);
    }

    private void OnPresentationPulse()
    {
        if (!_shuttingDown)
        {
            _nativeReservations.Pulse();
            if (Interlocked.Exchange(ref _presentationPulseQueued, 1) == 0)
                _ = Task.Run(() =>
                {
                    try { if (!_shuttingDown) _presentation?.ObserveCurrentMain(); }
                    catch { /* A stale appearance token is retried on the next pulse. */ }
                    finally { Volatile.Write(ref _presentationPulseQueued, 0); }
                });
        }
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

        // Both posted native hints and Shell registration can arrive while a
        // COM call pumps messages. Only request work; never enter the coordinator.
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
            backend = new ShellExplorerBackend(closeSourceEarly: true, _nativeReservations);
            backend.BackgroundTransferChanged += OnExplorerWindowLifecycle;
            backend.ShellWindowRegistered += OnExplorerWindowLifecycle;
            backend.WindowExcluded += OnExplorerWindowExcluded;
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
                _wakeupMonitor = new ExplorerWakeupMonitor(OnExplorerWindowLifecycle, OnExplorerWindowShown,
                    OnPresentationPulse, OnExplorerWindowCreated, () => _nativeReservations.HasPending);
            }
            if (_coordinator is null && !TryInitializeBackend())
                return;

            var coordinator = _coordinator!;
            for (var poll = 0; poll < MaximumPollsPerDrain; poll++)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                _pollRequested = false;
                _presentation?.Observe(coordinator.MainWindow);
                await coordinator.PollAsync(_lifetime.Token, preferFastDiscovery: RegistrationBurstActive);
                await RefreshNativeRoutingAsync();
                if (coordinator.LastTransfer is { } transfer)
                    _presentation?.OnTransfer(transfer.Source, transfer.Result);
                _presentation?.Observe(coordinator.MainWindow);
                await _backend!.ProcessPreopenCleanupAsync();
                if (!_capturePrimed && _capturePrimeAttempts < 3 && coordinator.PendingCount == 0 &&
                    coordinator.BackgroundPendingCount == 0 && coordinator.MainWindow is { } primeMain &&
                    Environment.TickCount64 >= _nextCapturePrimeAttemptAt)
                {
                    // Pay the first read-only COM/binder setup while idle.
                    // Native presentation already has the main, so a new
                    // frame can still be suppressed while this read pumps.
                    _capturePrimeAttempts++;
                    _nextCapturePrimeAttemptAt = Environment.TickCount64 + 1000;
                    _capturePrimed = _backend!.TryPrimeFastCapture(primeMain);
                }
                if (_shuttingDown)
                    return;

                _statusItem.Text = coordinator.Status;
                ReportBackgroundFailures();
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

    private void ReportBackgroundFailures()
    {
        var failures = _backend?.FailedTransferCount ?? 0;
        if (failures <= _reportedBackgroundFailures || _shuttingDown)
            return;
        _reportedBackgroundFailures = failures;
        _statusItem.Text = "Не удалось открыть вкладку.";
        try
        {
            _notifyIcon.ShowBalloonTip(5000, "ExplorerOne",
                "Не удалось открыть папку во вкладке. Повтори открытие папки.", ToolTipIcon.Warning);
        }
        catch { }
    }

    private void OnExitClick(object? sender, EventArgs e) => _ = ShutdownSafelyAsync();

    private async Task ShutdownSafelyAsync()
    {
        if (_shuttingDown)
            return;

        _shuttingDown = true;
        _nativeReservations.StopPreopening();
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
            try { await ShutdownFolderOpeningAsync(); } catch { }

            try
            {
                if (_activePoll is not null)
                    await _activePoll;
            }
            catch { }
            try
            {
                // Accepted early-close copies outlive the routing request.
                // Finish their bounded work before releasing shared COM objects.
                if (_backend is not null)
                    await _backend.DrainBackgroundTransfersAsync();
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
        if (_backend is not null)
        {
            _backend.BackgroundTransferChanged -= OnExplorerWindowLifecycle;
            _backend.ShellWindowRegistered -= OnExplorerWindowLifecycle;
            _backend.WindowExcluded -= OnExplorerWindowExcluded;
        }
        try { _backend?.Dispose(); } catch { }
        _backend = null;
        try { _nativeReservations.Dispose(); } catch { }
        _coordinator = null;
    }
}
