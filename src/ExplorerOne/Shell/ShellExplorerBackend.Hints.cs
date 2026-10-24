using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using ExplorerOne.Core;

namespace ExplorerOne.Shell;

public sealed partial class ShellExplorerBackend
{
    private const int MaximumMainRouteAnchors = 32;
    private readonly Dictionary<WindowId, MainRouteAnchor> _mainRouteAnchors = [];

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
            {
                _mainRouteAnchors.Remove(main);
                return false;
            }
            var before = ReadNativeWindows();
            var mainWindow = before.SingleOrDefault(window => window.Id == main);
            if (mainWindow is null || mainWindow.Tabs.Length == 0)
            {
                _mainRouteAnchors.Remove(main);
                return false;
            }
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
        dynamic shellWindows = _shellWindows!;
        int count = shellWindows.Count;
        if (_mainRouteAnchors.TryGetValue(main.Id, out var anchor))
        {
            if (anchor.Generation != generation || anchor.RegistryIndex < 0 || anchor.RegistryIndex >= count ||
                !main.Tabs.Contains(anchor.Tab) || !NativeTabBelongsTo(main.Id, anchor.Tab))
                _mainRouteAnchors.Remove(main.Id);
            else
            {
                // Registrations normally append. Revalidate the already-known
                // tab before touching a newly appended, initializing Home row.
                // The index is only a hint; every COM/native proof is fresh.
                var cached = ProbeSupportedMainTab(main, generation, anchor.RegistryIndex, anchor.Tab);
                if (cached == true)
                    return true;
                _mainRouteAnchors.Remove(main.Id);
                if (cached == false)
                    return false;
            }
        }

        // A changed/missing hint falls back to the complete existing lookup
        // order. Never use cached content or classification as eligibility.
        for (var index = count - 1; index >= 0; index--)
        {
            var supported = ProbeSupportedMainTab(main, generation, index);
            if (supported.HasValue)
                return supported.Value;
        }
        return false;
    }

    private bool? ProbeSupportedMainTab(NativeWindow main, int generation, int index, nint expectedTab = 0)
    {
        object? browser = null;
        IShellBrowser? shellBrowser = null;
        try
        {
            DiagnosticLastHintBrowsersVisited++;
            browser = ((dynamic)_shellWindows!).Item(index);
            if (browser is null) return null;
            nint browserWindow = new(Convert.ToInt64(((dynamic)browser).HWND, CultureInfo.InvariantCulture));
            if (ShellNative.GetAncestor(browserWindow, ShellNative.RootAncestor) != main.Id.Handle ||
                browser is not IShellServiceProvider services)
                return null;
            var service = ShellBrowserInterface;
            var iid = ShellBrowserInterface;
            if (services.QueryService(ref service, ref iid, out shellBrowser) < 0 || shellBrowser is null ||
                shellBrowser.GetWindow(out var tab) < 0 || (expectedTab != 0 && tab != expectedTab) ||
                !main.Tabs.Contains(tab) || !NativeTabBelongsTo(main.Id, tab))
                return null;
            DiagnosticLastHintTabsAssociated++;
            var classification = BrowserIsControlPanel(browser);
            if (!classification.HasValue) return null;
            if (generation != _generation || !NativeTabBelongsTo(main.Id, tab))
                return false;
            if (classification.Value)
            {
                _nativeReservations.AbandonEarly(main.Id);
                try { WindowExcluded?.Invoke(main.Id); }
                catch { /* Native presentation restoration cannot affect eligibility. */ }
                return false;
            }
            RememberMainRouteAnchor(main.Id, tab, index, generation);
            return true; // A fresh supported tab proves this frame is usable.
        }
        catch (Exception exception) when (exception is COMException or InvalidComObjectException or
            Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            RecordDiagnosticFailure(exception);
            if (exception is COMException disconnected && ResetDisconnectedShell(disconnected))
                return false;
            return null;
        }
        finally
        {
            ComReference.Release(shellBrowser);
            ComReference.Release(browser);
        }
    }

    private void RememberMainRouteAnchor(WindowId window, nint tab, int index, int generation)
    {
        if (generation != _generation || !NativeTabBelongsTo(window, tab))
            return;
        if (!_mainRouteAnchors.ContainsKey(window) && _mainRouteAnchors.Count >= MaximumMainRouteAnchors)
            _mainRouteAnchors.Remove(_mainRouteAnchors.Keys.First());
        _mainRouteAnchors[window] = new MainRouteAnchor(generation, tab, index);
    }

    // No RCW, shell path, selection, or classification survives the lookup.
    private sealed record MainRouteAnchor(int Generation, nint Tab, int RegistryIndex);
}
