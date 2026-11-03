using ExplorerOne.Opening;
using ExplorerOne.Diagnostics;

namespace ExplorerOne;

internal sealed partial class TrayApplicationContext
{
    private readonly NativeLaunchRouter _launchRouter = new();
    private readonly ToolStripMenuItem _folderRoutingItem = new("Открывать папки сразу во вкладках");
    private Task? _nativeRoutingUpdate;
    private volatile bool _directFolderTabsEnabled = true;
    private volatile bool _nativeRoutingActive;
    private bool _routingTransition;

    private void InitializeFolderOpening()
    {
        _menu.Items.Insert(2, _folderRoutingItem);
        try { _directFolderTabsEnabled = FolderRoutingPreference.Read(); }
        catch { _directFolderTabsEnabled = true; }
        _folderRoutingItem.Checked = _directFolderTabsEnabled;
        _folderRoutingItem.Click += OnFolderRoutingClick;
    }

    private async void OnFolderRoutingClick(object? sender, EventArgs e)
    {
        if (_shuttingDown || _routingTransition) return;
        _routingTransition = true;
        _folderRoutingItem.Enabled = false;
        try
        {
            var enabled = !_directFolderTabsEnabled;
            FolderRoutingPreference.Write(enabled);
            _directFolderTabsEnabled = enabled;
            _folderRoutingItem.Checked = enabled;
            if (_nativeRoutingUpdate is not null) await _nativeRoutingUpdate;
            if (enabled) await RefreshNativeRoutingAsync();
            else
            {
                await _launchRouter.StopAsync(_lifetime.Token);
                _nativeRoutingActive = false;
                AppDiagnostics.PublishNativeStatus(_launchRouter.Status, false);
                if (_backend is not null) _backend.NativeFolderRoutingEnabled = false;
            }
        }
        catch (Exception exception)
        {
            AppDiagnostics.Error("folder_routing_setting_failed", exception);
            _statusItem.Text = "Не удалось изменить открытие папок.";
        }
        finally
        {
            _routingTransition = false;
            if (!_shuttingDown) _folderRoutingItem.Enabled = true;
        }
    }

    private Task RefreshNativeRoutingAsync()
    {
        if (_shuttingDown || !_directFolderTabsEnabled) return Task.CompletedTask;
        if (_nativeRoutingUpdate is { IsCompleted: false }) return _nativeRoutingUpdate;
        _nativeRoutingUpdate = UpdateNativeRoutingAsync();
        return _nativeRoutingUpdate;
    }

    private async Task UpdateNativeRoutingAsync()
    {
        try
        {
            await _launchRouter.UpdateAsync(_coordinator?.MainWindow, _lifetime.Token);
            _nativeRoutingActive = _launchRouter.IsActive;
            AppDiagnostics.PublishNativeStatus(_launchRouter.Status, _nativeRoutingActive);
            if (_backend is not null) _backend.NativeFolderRoutingEnabled = _nativeRoutingActive;
            _folderRoutingItem.ToolTipText = _nativeRoutingActive
                ? "Новые папки открываются сразу во вкладке."
                : "Прямое открытие пока недоступно; используется перенос окон.";
        }
        catch (Exception exception)
        {
            AppDiagnostics.Error("native_routing_update_failed", exception);
            _nativeRoutingActive = false;
            if (_backend is not null) _backend.NativeFolderRoutingEnabled = false;
        }
    }

    private async Task ShutdownFolderOpeningAsync()
    {
        _folderRoutingItem.Enabled = false;
        if (_nativeRoutingUpdate is not null) await _nativeRoutingUpdate;
        await _launchRouter.DisposeAsync();
        _nativeRoutingActive = false;
        AppDiagnostics.PublishNativeStatus(_launchRouter.Status, false);
    }
}
