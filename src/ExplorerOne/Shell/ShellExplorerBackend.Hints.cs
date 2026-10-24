using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using ExplorerOne.Core;

namespace ExplorerOne.Shell;

public sealed partial class ShellExplorerBackend
{
    internal long DiagnosticLastHintDiscoveryMilliseconds { get; private set; }
    internal long DiagnosticHintDiscoveryCount { get; private set; }
    internal long DiagnosticHintDiscoverySuccessCount { get; private set; }
    internal long DiagnosticHintNoNewSourceCount { get; private set; }
    internal int DiagnosticLastHintBrowsersVisited { get; private set; }
    internal int DiagnosticLastHintTabsAssociated { get; private set; }
    public bool TryEnumerateRoutingHint(WindowId main, out IReadOnlyList<ExplorerWindow> windows)
    {
        AssertContext();
        windows = Array.Empty<ExplorerWindow>();
        DiagnosticHintDiscoveryCount++;
        DiagnosticLastHintBrowsersVisited = 0;
        DiagnosticLastHintTabsAssociated = 0;
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            if (!_closeSourceEarly || !IsExplorerWindow(main))
                return false;
            var before = ReadNativeWindows();
            var mainWindow = before.SingleOrDefault(window => window.Id == main);
            if (mainWindow is null || mainWindow.Tabs.Length == 0)
                return false;
            var noNewSource = _backgroundJobs.Count > 0 &&
                before.All(window => window.Id == main || _backgroundJobs.ContainsKey(window.Id));
            var generation = _generation;
            if (!noNewSource)
            {
                // Any unowned extra window still needs a fresh supported-main
                // proof before it can be routed. No COM is needed when every
                // possible source is already exclusively owned by a live job.
                RefreshShell();
                generation = _generation;
                if (!HasSupportedMainTab(mainWindow, generation))
                    return false;
            }
            var after = ReadNativeWindows();
            if (generation != _generation || !IsExplorerWindow(main) || !NativeWindowSetsEqual(before, after))
                return false;

            // This is still the COMPLETE native inventory. Only existing-main
            // eligibility is scoped; each source's full capture/CP rejection
            // and close ownership remain mandatory in TransferAsync.
            windows = before.Select(window => new ExplorerWindow(window.Id, window.Tabs.Length)).ToArray();
            DiagnosticHintDiscoverySuccessCount++;
            if (noNewSource) DiagnosticHintNoNewSourceCount++;
            return true;
        }
        catch (Exception exception)
        {
            RecordDiagnosticFailure(exception);
            return false; // Normal discovery owns unknown/lost/disconnected-main fallback.
        }
        finally { DiagnosticLastHintDiscoveryMilliseconds = ElapsedMilliseconds(startedAt); }
    }

    private bool HasSupportedMainTab(NativeWindow main, int generation)
    {
        // One reverse lookup, rather than one full scan per native tab. A
        // pending background tab need not be registered to use another fresh,
        // ordinary tab in this already-selected Explorer frame.
        dynamic shellWindows = _shellWindows!;
        int count = shellWindows.Count;
        for (var index = count - 1; index >= 0; index--)
        {
            object? browser = null;
            IShellBrowser? shellBrowser = null;
            try
            {
                DiagnosticLastHintBrowsersVisited++;
                browser = shellWindows.Item(index);
                if (browser is null) continue;
                nint browserWindow = new(Convert.ToInt64(((dynamic)browser).HWND, CultureInfo.InvariantCulture));
                if (ShellNative.GetAncestor(browserWindow, ShellNative.RootAncestor) != main.Id.Handle ||
                    browser is not IShellServiceProvider services)
                    continue;
                var service = ShellBrowserInterface;
                var iid = ShellBrowserInterface;
                if (services.QueryService(ref service, ref iid, out shellBrowser) < 0 || shellBrowser is null ||
                    shellBrowser.GetWindow(out var tab) < 0 || !main.Tabs.Contains(tab) || !NativeTabBelongsTo(main.Id, tab))
                    continue;
                DiagnosticLastHintTabsAssociated++;
                var classification = BrowserIsControlPanel(browser);
                if (!classification.HasValue) continue;
                if (generation != _generation || !NativeTabBelongsTo(main.Id, tab))
                    return false;
                if (classification.Value)
                {
                    try { WindowExcluded?.Invoke(main.Id); }
                    catch { /* Native presentation restoration cannot affect eligibility. */ }
                    return false;
                }
                return true; // A fresh supported tab proves this frame is usable.
            }
            catch (Exception exception) when (exception is COMException or InvalidComObjectException or
                Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                RecordDiagnosticFailure(exception);
                if (exception is COMException disconnected && ResetDisconnectedShell(disconnected))
                    return false;
            }
            finally
            {
                ComReference.Release(shellBrowser);
                ComReference.Release(browser);
            }
        }
        return false;
    }
}
