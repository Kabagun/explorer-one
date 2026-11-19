using System.Diagnostics;
using ExplorerOne.Diagnostics;
using ExplorerOne.Updating;

namespace ExplorerOne;

internal sealed partial class TrayApplicationContext
{
    private const string CheckUpdateText = "Проверить обновления";
    private static readonly HttpClient UpdateHttp = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
    private readonly ToolStripMenuItem _checkUpdateItem = new(CheckUpdateText);
    private Version? _installedVersion;
    private (ReleaseUpdate Update, string Path)? _readyUpdate;

    private void InitializeUpdates()
    {
        _menu.Items.Insert(_menu.Items.IndexOf(_logsItem), _checkUpdateItem);
        try
        {
            var version = typeof(Program).Assembly.GetName().Version;
            var executable = Environment.ProcessPath;
            if (version is not null && executable is not null)
            {
                var current = new Version(version.Major, version.Minor, version.Build);
                if (UpdateRegistration.MatchesInstallation(executable, current))
                    _installedVersion = current;
            }
        }
        catch (Exception exception)
        {
            AppDiagnostics.Error("update_init_failed", exception);
        }

        if (_installedVersion is null)
        {
            _checkUpdateItem.Text = "Обновления доступны после установки";
            _checkUpdateItem.Enabled = false;
            return;
        }
        _checkUpdateItem.Click += OnUpdateClick;
    }

    private async void OnUpdateClick(object? sender, EventArgs e)
    {
        if (_shuttingDown || !_checkUpdateItem.Enabled || _installedVersion is null)
            return;
        _checkUpdateItem.Enabled = false;
        var token = _lifetime.Token;
        try
        {
            if (_readyUpdate is { } pending)
                await InstallUpdateAsync(pending, token);
            else
                await CheckForUpdateAsync(_installedVersion, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            AppDiagnostics.Error("update_failed", exception);
            if (!_shuttingDown) ShowUpdateMessage("Обновить ExplorerOne не удалось.");
        }
        finally
        {
            if (!_shuttingDown)
            {
                _checkUpdateItem.Text = _readyUpdate is { } ready
                    ? $"Установить обновление {ready.Update.Version}"
                    : CheckUpdateText;
                _checkUpdateItem.Enabled = true;
            }
        }
    }

    private async Task CheckForUpdateAsync(Version version, CancellationToken token)
    {
        _checkUpdateItem.Text = "Проверка обновлений…";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var client = new GitHubUpdateClient(UpdateHttp);
        var update = await client.CheckAsync(version, timeout.Token);
        if (update is null)
        {
            if (!_shuttingDown) ShowUpdateMessage("Новых стабильных версий нет.");
            return;
        }

        _checkUpdateItem.Text = $"Загрузка ExplorerOne {update.Version}…";
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!Path.IsPathFullyQualified(localAppData))
            throw new IOException("The local application data directory is unavailable.");
        var directory = Path.Combine(localAppData, "ExplorerOne", "Updates");
        Directory.CreateDirectory(directory);
        CleanOldUpdateFiles(directory);
        var path = Path.Combine(directory, $"update-{Guid.NewGuid():N}.exe");
        var partial = path + ".part";
        try
        {
            await client.DownloadAsync(update, partial, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            File.Move(partial, path);
            _readyUpdate = (update, path);
            AppDiagnostics.Event("update_downloaded", update.Version.ToString(3));
            ShowUpdateMessage($"Обновление {update.Version} скачано. Чтобы установить его, открой меню ExplorerOne в трее.");
        }
        finally
        {
            DeleteUpdateFile(partial);
        }
    }

    private async Task InstallUpdateAsync((ReleaseUpdate Update, string Path) pending, CancellationToken token)
    {
        if (_routingTransition || _coordinator is { HasReadyPending: true } or { BackgroundPendingCount: > 0 })
        {
            ShowUpdateMessage("ExplorerOne ещё переносит вкладки. Попробуй установить обновление чуть позже.");
            return;
        }
        if (Environment.ProcessPath is not { } executable ||
            !UpdateRegistration.MatchesInstallation(executable, _installedVersion!))
            throw new InvalidOperationException("Installation changed before update.");

        _checkUpdateItem.Text = "Установка обновления…";
        // Setup owns the file after handoff. Shutdown must not delete it.
        _readyUpdate = null;
        Process? process = null;
        try
        {
            using (var installer = File.Open(pending.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                GitHubUpdateClient.VerifyBeforeLaunch(installer, pending.Update);
                var launch = new ProcessStartInfo(pending.Path) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
                foreach (var option in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/EXPLORERONEUPDATE=1" })
                    launch.ArgumentList.Add(option);
                process = Process.Start(launch) ?? throw new InvalidOperationException("Setup did not start.");
            }

            AppDiagnostics.Event("update_install_started", pending.Update.Version.ToString(3), flush: true);
            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Update setup returned exit code {process.ExitCode}.");
        }
        finally
        {
            if (process is null || process.HasExited) DeleteUpdateFile(pending.Path);
            process?.Dispose();
        }
    }

    private void ShowUpdateMessage(string message)
    {
        try { _notifyIcon.ShowBalloonTip(5000, "ExplorerOne", message, ToolTipIcon.Info); }
        catch { }
    }

    private void StopUpdates()
    {
        _checkUpdateItem.Enabled = false;
        if (_readyUpdate is { } pending)
            DeleteUpdateFile(pending.Path);
        _readyUpdate = null;
    }

    private static void CleanOldUpdateFiles(string directory)
    {
        var cutoff = DateTime.UtcNow.AddDays(-1);
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("update-*"))
        {
            var name = file.Name;
            if (name.Length > 39 && Guid.TryParseExact(name.AsSpan(7, 32), "N", out _) &&
                name[39..] is ".exe" or ".exe.part" && file.LastWriteTimeUtc < cutoff)
                DeleteUpdateFile(file.FullName);
        }
    }

    private static void DeleteUpdateFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
