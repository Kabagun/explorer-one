// Explorer tab commands, QueryService and FolderView operations are adapted from
// ExplorerTabUtility (w4po, copyright 2025), MIT, commit fad7363. See THIRD-PARTY-NOTICES.md.
// Sources: Hooks/ExplorerWatcher.cs (RequestToOpenNewTab, GetTabHandle,
// GetLocation, GetSelectedItems, SelectItems, Navigate) and Helpers/Helper.cs
// (GetAllExplorerTabs). Transactional snapshots and close verification are new.
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ExplorerOne.Core;

namespace ExplorerOne.Shell;

/// <summary>
/// Accesses Explorer on the owning WinForms STA. A failed or uncertain copy never
/// closes its source. This class must be constructed and used inside the UI pump.
/// </summary>
public sealed class ShellExplorerBackend : IExplorerBackend, IDisposable
{
    private static readonly Guid ShellWindowsClass = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid ShellApplicationClass = new("13709620-C279-11CE-A49E-444553540000");
    private static readonly Guid ShellBrowserInterface = typeof(IShellBrowser).GUID;
    private const int SourceSettleMilliseconds = 1200;
    private const int DestinationTimeoutMilliseconds = 6000;
    private const int PollMilliseconds = 120;
    private const int MaximumRetainedTransfers = 256;

    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private readonly List<PartialTransfer> _partialTransfers = [];
    private object? _shellWindows;
    private ShellPathComparer? _paths;
    private uint _shellProcess;
    private int _generation;
    private bool _transferring;
    private bool _disposed;

    // Safe, in-memory diagnostics for support and the isolated integration
    // harness. These never contain exception messages, URLs, or item names.
    internal string DiagnosticPhase { get; private set; } = "idle";
    internal string? DiagnosticFailureType { get; private set; }
    internal int DiagnosticFailureHResult { get; private set; }

    public ShellExplorerBackend()
    {
        AssertContext();
    }

    public IReadOnlyList<ExplorerWindow> EnumerateWindows()
    {
        AssertContext();
        try
        {
            using var snapshot = ReadRegisteredTabs();
            // Filter only after full native/COM coverage was proved. A mixed
            // Control Panel/folder window stays visible and is not a routing
            // destination or a partially transferred source.
            return snapshot.Windows.Where(window => !ContainsControlPanel(window))
                .Select(window => new ExplorerWindow(window.Id, window.Tabs.Count)).ToArray();
        }
        catch
        {
            // Never expose a COM exception containing a location, or report an
            // incomplete collection as if it were the entire set of windows.
            throw new ShellSnapshotException();
        }
    }

    public async Task<TransferResult> TransferAsync(WindowId source, WindowId target, CancellationToken cancellationToken)
    {
        AssertContext();
        DiagnosticPhase = "transfer-start";
        DiagnosticFailureType = null;
        DiagnosticFailureHResult = 0;
        if (_transferring)
            return TransferResult.Retry;
        if (source == target || source.Handle == target.Handle || !IsExplorerWindow(target))
            return TransferResult.Unsafe;
        if (!IsExplorerWindow(source))
            return TransferResult.SourceGone;

        _transferring = true;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            PruneVanishedTransfers();
            var original = CaptureWindow(source);
            var generation = _generation;

            // Show-in-folder can register its folder before its file selection.
            // Never take the first registration as the final selection state.
            await Task.Delay(SourceSettleMilliseconds, cancellationToken);
            if (!IsExplorerWindow(source))
                return TransferResult.SourceGone;
            var settled = CaptureWindow(source);
            if (generation != _generation || !SameWindowState(original, settled))
            {
                DiagnosticPhase = "source-not-settled";
                return TransferResult.Retry;
            }

            var transfers = new List<PartialTransfer>();
            foreach (var tab in original.Tabs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!SameWindowState(original, CaptureWindow(source)) || generation != _generation)
                    return TransferResult.Retry;

                var transfer = _partialTransfers.FirstOrDefault(candidate =>
                    candidate.Source == source && candidate.SourceState.Stamp == tab.Stamp);
                if (transfer is not null && transfer.Target != target)
                    return TransferResult.Unsafe;
                if (transfer is null)
                {
                    // Keep unsuccessful requests too: a timed-out Ctrl+T may be
                    // delivered later, so retrying must never send another one.
                    if (_partialTransfers.Count >= MaximumRetainedTransfers)
                        return TransferResult.Unsafe;
                    transfer = new PartialTransfer(source, target, tab);
                    _partialTransfers.Add(transfer);
                }

                if (!await EnsureDestinationAsync(transfer, generation, cancellationToken))
                {
                    DiagnosticPhase = "destination-timeout";
                    return TransferResult.Retry;
                }
                transfers.Add(transfer);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _generation || !IsExplorerWindow(target))
                return TransferResult.Retry;
            if (!IsExplorerWindow(source))
                return TransferResult.SourceGone;

            // Re-read every destination, then the source, in a fresh complete
            // registry/native snapshot. No source mutation precedes this gate.
            using (var final = ReadRegisteredTabs())
            {
                var targetWindow = final.FindWindow(target);
                var destinations = new List<VerifiedTabTransfer>();
                foreach (var transfer in transfers)
                {
                    var reference = targetWindow.Tabs.SingleOrDefault(tab => tab.Handle == transfer.TargetTab)
                        ?? throw new ShellSnapshotException();
                    var copied = CaptureTab(reference);
                    if (!SameTabContents(transfer.SourceState, copied))
                        return TransferResult.Unsafe;
                    destinations.Add(new VerifiedTabTransfer(transfer.SourceState.Handle, copied.Stamp));
                }

                var current = CaptureWindow(final.FindWindow(source));
                if (!TransferSafety.CanCloseSource(original.Stamps, current.Stamps, destinations) ||
                    generation != _generation || !IsExplorerWindow(source) || !IsExplorerWindow(target) ||
                    !NativeTabsMatch(source, original.Tabs.Select(tab => tab.Handle)))
                {
                    DiagnosticPhase = "close-guard-rejected";
                    return TransferResult.Retry;
                }

                cancellationToken.ThrowIfCancellationRequested();
                // Quit() can close a different scope on tabbed Explorer. Send a
                // bounded close only to this exact HWND+PID after verification.
                _ = ShellNative.SendMessageTimeout(source.Handle, ShellNative.WmClose, 0, 0,
                    ShellNative.AbortIfHung | ShellNative.ErrorOnExit, 1000, out _);
            }

            var deadline = Environment.TickCount64 + 1500;
            while (IsExplorerWindow(source) && Environment.TickCount64 < deadline)
                await Task.Delay(PollMilliseconds, cancellationToken);
            if (IsExplorerWindow(source))
            {
                DiagnosticPhase = "source-close-timeout";
                return TransferResult.Retry;
            }

            _partialTransfers.RemoveAll(transfer => transfer.Source == source);
            if (IsExplorerWindow(target))
            {
                if (ShellNative.IsIconic(target.Handle))
                    _ = ShellNative.ShowWindowAsync(target.Handle, 9); // SW_RESTORE.
                _ = ShellNative.SetForegroundWindow(target.Handle);
            }
            DiagnosticPhase = "completed";
            DiagnosticFailureType = null;
            DiagnosticFailureHResult = 0;
            return TransferResult.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return TransferResult.Retry;
        }
        catch (UnsupportedShellViewException exception)
        {
            RecordDiagnosticFailure(exception);
            return TransferResult.Unsafe;
        }
        catch (Exception exception)
        {
            RecordDiagnosticFailure(exception);
            // The host reports only this classification, never private shell
            // paths or raw automation exception messages.
            return IsExplorerWindow(source) ? TransferResult.Retry : TransferResult.SourceGone;
        }
        finally
        {
            _transferring = false;
        }
    }

    private async Task<bool> EnsureDestinationAsync(PartialTransfer transfer, int generation, CancellationToken cancellationToken)
    {
        RefreshShell();
        if (generation != _generation)
            throw new ShellSnapshotException();
        if (!IsExplorerWindow(transfer.Target))
            throw new ShellSnapshotException();

        if (!transfer.NewTabRequested)
        {
            using var before = ReadRegisteredTabs();
            if (generation != _generation)
                throw new ShellSnapshotException();
            var window = before.FindWindow(transfer.Target);
            transfer.TargetBefore = window.Tabs.Select(tab => tab.Handle).ToHashSet();
            var active = ShellNative.FindWindowEx(transfer.Target.Handle, 0, "ShellTabWindowClass", null);
            if (!transfer.TargetBefore.Contains(active))
                throw new ShellSnapshotException();

            transfer.NewTabRequested = true;
            DiagnosticPhase = "request-new-tab";
            if (!ShellNative.PostMessage(active, ShellNative.WmCommand, ShellNative.NewTabCommand, 0))
                throw new ShellSnapshotException();
        }

        var deadline = Environment.TickCount64 + DestinationTimeoutMilliseconds;
        var selectionAppliedThisAttempt = false;
        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshShell();
            if (generation != _generation || !IsExplorerWindow(transfer.Source) || !IsExplorerWindow(transfer.Target))
                throw new ShellSnapshotException();

            try
            {
                using var snapshot = ReadRegisteredTabs();
                var destinationWindow = snapshot.FindWindow(transfer.Target);
                if (transfer.TargetTab == 0)
                {
                    var added = destinationWindow.Tabs.Where(tab => !transfer.TargetBefore.Contains(tab.Handle)).ToArray();
                    if (added.Length > 1)
                        throw new UnsupportedShellViewException();
                    if (added.Length == 0)
                    {
                        await Task.Delay(PollMilliseconds, cancellationToken);
                        continue;
                    }
                    // Remember before reading its view, even if registration or
                    // navigation is still incomplete. Do not create a second tab.
                    transfer.TargetTab = added[0].Handle;
                }

                var reference = destinationWindow.Tabs.SingleOrDefault(tab => tab.Handle == transfer.TargetTab)
                    ?? throw new UnsupportedShellViewException();
                if (!transfer.NavigationIssued)
                {
                    // The newly created tab may start at Home, whose initial
                    // automation view need not expose a portable folder. We
                    // own this fresh handle, so navigate before reading it.
                    transfer.NavigationIssued = true;
                    Navigate(reference.Browser, transfer.SourceState.Location);
                    await Task.Delay(PollMilliseconds, cancellationToken);
                    continue;
                }

                TabState current;
                try
                {
                    current = CaptureTab(reference);
                }
                catch (UnsupportedShellViewException) when (!transfer.Verified)
                {
                    // The default new-tab view may still be visible while the
                    // requested folder is loading. It is never a verified copy.
                    await Task.Delay(PollMilliseconds, cancellationToken);
                    continue;
                }
                if (SameTabContents(transfer.SourceState, current))
                {
                    transfer.Verified = true;
                    return true;
                }
                if (transfer.Verified)
                    throw new UnsupportedShellViewException(); // The user changed an already copied tab.

                var sameLocation = _paths!.Equivalent(transfer.SourceState.Location, current.Location);
                if (sameLocation && !selectionAppliedThisAttempt)
                {
                    // A partial previous SelectItem call can be completed, but
                    // never overwrite a selection containing an unexpected item.
                    if (!current.Selection.All(selected => transfer.SourceState.Selection.Any(expected =>
                        _paths.Equivalent(expected.Identity, selected.Identity))))
                        throw new UnsupportedShellViewException();
                    selectionAppliedThisAttempt = true;
                    DiagnosticPhase = "apply-selection";
                    ApplySelection(reference.Browser, transfer.SourceState.Selection);
                }
            }
            catch (ShellSnapshotException)
            {
                // Transient tab registration/busy views are allowed to settle.
            }

            await Task.Delay(PollMilliseconds, cancellationToken);
        }

        return false;
    }

    private WindowState CaptureWindow(WindowId id)
    {
        using var snapshot = ReadRegisteredTabs();
        return CaptureWindow(snapshot.FindWindow(id));
    }

    private WindowState CaptureWindow(RegisteredWindow window)
    {
        var tabs = window.Tabs.Select(CaptureTab).ToArray();
        if (!NativeTabsMatch(window.Id, tabs.Select(tab => tab.Handle)))
            throw new ShellSnapshotException();
        return new WindowState(window.Id, tabs);
    }

    private TabState CaptureTab(RegisteredTab reference)
    {
        try
        {
            dynamic browser = reference.Browser;
            DiagnosticPhase = "tab-ready-state";
            if ((bool)browser.Busy || Convert.ToInt32(browser.ReadyState, CultureInfo.InvariantCulture) != 4)
                throw new ShellSnapshotException();

            // A Document/SelectedItems COM object may remain readable after
            // Explorer has replaced its view. Obtain both afresh on each read.
            var first = CaptureDocument(reference);
            var second = CaptureDocument(reference);
            if (first.Stamp != second.Stamp || (bool)browser.Busy ||
                Convert.ToInt32(browser.ReadyState, CultureInfo.InvariantCulture) != 4)
                throw new ShellSnapshotException();
            return second;
        }
        catch (COMException exception)
        {
            RecordDiagnosticFailure(exception);
            ResetDisconnectedShell(exception);
            throw new ShellSnapshotException();
        }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException exception)
        {
            RecordDiagnosticFailure(exception);
            throw new UnsupportedShellViewException();
        }
    }

    private TabState CaptureDocument(RegisteredTab reference)
    {
        object? document = null;
        object? folder = null;
        object? self = null;
        object? selected = null;
        try
        {
            dynamic browser = reference.Browser;
            DiagnosticPhase = "read-document-folder";
            document = browser.Document;
            if (document is null)
                throw new ShellSnapshotException();
            folder = ((dynamic)document).Folder;
            self = ((dynamic)folder).Self;
            if (!(bool)((dynamic)self).IsFolder)
                throw new UnsupportedShellViewException();
            string path = ((dynamic)self).Path;
            DiagnosticPhase = "canonicalize-folder";
            var location = _paths!.Canonicalize(path);

            // Control Panel views are not portable Explorer folder tabs.
            if (IsControlPanelLocation(location))
                throw new UnsupportedShellViewException();

            string url = browser.LocationURL;
            DiagnosticPhase = "compare-folder-url";
            if (!string.IsNullOrEmpty(url) && !_paths.Equivalent(location, url))
                throw new ShellSnapshotException();

            selected = ((dynamic)document).SelectedItems();
            DiagnosticPhase = "read-selected-items";
            int count = ((dynamic)selected).Count;
            var selection = new List<SelectedItem>(count);
            for (var index = 0; index < count; index++)
            {
                object? item = null;
                try
                {
                    item = ((dynamic)selected).Item(index);
                    string itemPath = ((dynamic)item).Path;
                    string itemName = ((dynamic)item).Name;
                    DiagnosticPhase = "canonicalize-selected-item";
                    var identity = _paths.Canonicalize(itemPath);
                    if (selection.Any(existing => _paths.Equivalent(existing.Identity, identity)))
                        throw new UnsupportedShellViewException();
                    selection.Add(new SelectedItem(identity, itemName));
                }
                finally
                {
                    ComReference.Release(item);
                }
            }

            string finalUrl = browser.LocationURL;
            DiagnosticPhase = "validate-document-state";
            if ((bool)browser.Busy || (int)((dynamic)selected).Count != count ||
                !_paths.Equivalent(location, (string)((dynamic)self).Path) ||
                (!string.IsNullOrEmpty(finalUrl) && !_paths.Equivalent(location, finalUrl)))
                throw new ShellSnapshotException();
            selection.Sort((first, second) => StringComparer.Ordinal.Compare(first.Identity, second.Identity));
            return new TabState(reference.Handle, location, selection.ToArray());
        }
        catch (COMException exception)
        {
            RecordDiagnosticFailure(exception);
            ResetDisconnectedShell(exception);
            throw new ShellSnapshotException();
        }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException exception)
        {
            RecordDiagnosticFailure(exception);
            throw new UnsupportedShellViewException();
        }
        finally
        {
            ComReference.Release(selected);
            ComReference.Release(self);
            ComReference.Release(folder);
            ComReference.Release(document);
        }
    }

    private void Navigate(object browser, string location)
    {
        DiagnosticPhase = "navigate-destination";
        object? shell = null;
        object? folder = null;
        try
        {
            // A Folder automation object preserves literal # and % in paths,
            // instead of letting Navigate2 interpret them as URL components.
            shell = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellApplicationClass, true)!);
            folder = ((dynamic)shell!).NameSpace(location);
            if (folder is null)
                throw new UnsupportedShellViewException();
            ((dynamic)browser).Navigate2(folder);
        }
        finally
        {
            ComReference.Release(folder);
            ComReference.Release(shell);
        }
    }

    private bool ContainsControlPanel(RegisteredWindow window)
    {
        foreach (var tab in window.Tabs)
        {
            object? document = null;
            object? folder = null;
            object? self = null;
            try
            {
                string url = ((dynamic)tab.Browser).LocationURL;
                if (IsControlPanelLocation(url))
                    return true;
                if (!string.IsNullOrEmpty(url))
                    continue; // Ordinary filesystem tabs need no view capture.

                document = ((dynamic)tab.Browser).Document;
                if (document is null)
                    throw new ShellSnapshotException();
                folder = ((dynamic)document).Folder;
                self = ((dynamic)folder).Self;
                if (IsControlPanelLocation((string)((dynamic)self).Path))
                    return true;
            }
            catch (COMException exception)
            {
                RecordDiagnosticFailure(exception);
                ResetDisconnectedShell(exception);
                throw new ShellSnapshotException();
            }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                // Unknown views are rejected by source capture if transferred;
                // they are not assumed to be the known Control Panel namespace.
            }
            finally
            {
                ComReference.Release(self);
                ComReference.Release(folder);
                ComReference.Release(document);
            }
        }
        return false;
    }

    private static bool IsControlPanelLocation(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        if (value.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            value = value[6..];
        return IsNamespace(value, "::{26EE0668-A00A-44D7-9371-BEB064C98683}") ||
            IsNamespace(value, "::{21EC2020-3AEA-1069-A2DD-08002B30309D}");

        static bool IsNamespace(string location, string root) =>
            location.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
            (location.Length == root.Length || location[root.Length] is '\\' or '/');
    }

    private void ApplySelection(object browser, IReadOnlyList<SelectedItem> selection)
    {
        if (selection.Count == 0)
            return; // Verification requires the new tab to have no selection.

        object? document = null;
        object? folder = null;
        var parsed = new List<object>();
        try
        {
            document = ((dynamic)browser).Document;
            folder = ((dynamic)document).Folder;
            foreach (var expected in selection)
            {
                object? item = null;
                try
                {
                    DiagnosticPhase = "selection-parse-relative";
                    var parsingName = _paths!.RelativeParsingName(expected.Identity);
                    item = ((dynamic)folder).ParseName(parsingName);
                    if (item is null)
                    {
                        DiagnosticPhase = "selection-parse-absolute";
                        item = ((dynamic)folder).ParseName(expected.Identity);
                    }
                    if (item is null)
                    {
                        DiagnosticPhase = "selection-parse-display";
                        item = ((dynamic)folder).ParseName(expected.Name);
                    }
                    DiagnosticPhase = "selection-validate-identity";
                    if (item is null || !_paths!.Equivalent(expected.Identity, (string)((dynamic)item).Path))
                        throw new UnsupportedShellViewException();
                    parsed.Add(item);
                    item = null;
                }
                finally
                {
                    ComReference.Release(item);
                }
            }

            for (var index = 0; index < parsed.Count; index++)
            {
                DiagnosticPhase = "selection-select-item";
                object item = parsed[index];
                // SELECT; first item also DESELECTOTHERS and ENSUREVISIBLE.
                ((dynamic)document).SelectItem(ref item, index == 0 ? 1 | 4 | 8 : 1);
            }
        }
        finally
        {
            foreach (var item in parsed)
                ComReference.Release(item);
            ComReference.Release(folder);
            ComReference.Release(document);
        }
    }

    private RegisteredSnapshot ReadRegisteredTabs()
    {
        DiagnosticPhase = "connect-shell";
        RefreshShell();
        DiagnosticPhase = "enumerate-native-windows";
        var native = ReadNativeWindows();
        var snapshot = new RegisteredSnapshot();
        try
        {
            dynamic shellWindows = _shellWindows!;
            DiagnosticPhase = "registered-tab-count";
            int count = shellWindows.Count;
            var seenTabs = new HashSet<nint>();
            for (var index = 0; index < count; index++)
            {
                object? browser = null;
                IShellBrowser? shellBrowser = null;
                try
                {
                    browser = shellWindows.Item(index);
                    DiagnosticPhase = "registered-browser-window";
                    if (browser is null)
                        continue; // A closed tab can leave a stale collection slot.
                    nint browserWindow = new(Convert.ToInt64(((dynamic)browser).HWND, CultureInfo.InvariantCulture));
                    var root = ShellNative.GetAncestor(browserWindow, ShellNative.RootAncestor);
                    var window = native.FirstOrDefault(candidate => candidate.Id.Handle == root);
                    if (window is null)
                        continue; // Desktop, browser, dialog, or another shell window.

                    if (browser is not IShellServiceProvider services)
                        continue;
                    var serviceId = ShellBrowserInterface;
                    var interfaceId = ShellBrowserInterface;
                    DiagnosticPhase = "registered-query-service";
                    if (services.QueryService(ref serviceId, ref interfaceId, out shellBrowser) < 0 || shellBrowser is null ||
                        shellBrowser.GetWindow(out var tab) < 0 || !window.Tabs.Contains(tab) || !seenTabs.Add(tab))
                        continue;

                    var registered = snapshot.Windows.FirstOrDefault(candidate => candidate.Id == window.Id);
                    if (registered is null)
                    {
                        registered = new RegisteredWindow(window.Id);
                        snapshot.Windows.Add(registered);
                    }
                    registered.Tabs.Add(new RegisteredTab(tab, browser));
                    browser = null; // The snapshot now owns this COM reference.
                }
                catch (Exception exception) when (exception is COMException or InvalidComObjectException or
                    Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                {
                    // Closed/orphan registrations may be unreadable. They may
                    // be skipped only because the final native cross-check
                    // still requires a registered object for EVERY live tab.
                    RecordDiagnosticFailure(exception);
                    if (exception is COMException disconnected && ResetDisconnectedShell(disconnected))
                        throw new ShellSnapshotException();
                }
                finally
                {
                    ComReference.Release(shellBrowser);
                    ComReference.Release(browser);
                }
            }

            DiagnosticPhase = "registered-native-cross-check";
            if ((int)shellWindows.Count != count || !NativeWindowSetsEqual(native, ReadNativeWindows()) ||
                native.Count != snapshot.Windows.Count || native.Any(window =>
                    snapshot.FindWindow(window.Id).Tabs.Count != window.Tabs.Length))
                throw new ShellSnapshotException();
            return snapshot;
        }
        catch (Exception exception)
        {
            RecordDiagnosticFailure(exception);
            if (exception is COMException disconnected)
                ResetDisconnectedShell(disconnected);
            snapshot.Dispose();
            throw new ShellSnapshotException();
        }
    }

    private void RefreshShell()
    {
        var shellWindow = ShellNative.GetShellWindow();
        ShellNative.GetWindowThreadProcessId(shellWindow, out var process);
        if (process != _shellProcess)
        {
            ResetShell(retainTransfers: true);
            _shellProcess = process;
        }
        if (process == 0)
            throw new ShellSnapshotException();
        if (_shellWindows is not null)
            return;

        try
        {
            _paths = new ShellPathComparer();
            _shellWindows = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellWindowsClass, true)!);
            if (_shellWindows is null)
                throw new ShellSnapshotException();
        }
        catch
        {
            ResetShell(retainTransfers: true);
            throw new ShellSnapshotException();
        }
    }

    private static List<NativeWindow> ReadNativeWindows()
    {
        var windows = new List<NativeWindow>();
        var processes = new Dictionary<uint, bool>();
        Exception? failure = null;
        if (!ShellNative.EnumWindows((handle, _) =>
            {
                try
                {
                    if (ShellNative.ClassName(handle) != "CabinetWClass" || !ShellNative.IsWindowVisible(handle))
                        return true;
                    ShellNative.GetWindowThreadProcessId(handle, out var processId);
                    if (!processes.TryGetValue(processId, out var explorer))
                    {
                        using var process = Process.GetProcessById(checked((int)processId));
                        explorer = StringComparer.OrdinalIgnoreCase.Equals(process.ProcessName, "explorer");
                        processes.Add(processId, explorer);
                    }
                    if (!explorer)
                        return true;
                    var id = new WindowId(handle, processId);
                    var tabs = ReadNativeTabs(id);
                    if (tabs.Length == 0)
                        throw new ShellSnapshotException();
                    windows.Add(new NativeWindow(id, tabs));
                    return true;
                }
                catch (Exception exception)
                {
                    failure = exception;
                    return false;
                }
            }, 0) || failure is not null)
            throw new ShellSnapshotException();
        return windows;
    }

    private static nint[] ReadNativeTabs(WindowId window)
    {
        if (!IsExplorerWindow(window))
            throw new ShellSnapshotException();
        var result = new List<nint>();
        nint tab = 0;
        while ((tab = ShellNative.FindWindowEx(window.Handle, tab, "ShellTabWindowClass", null)) != 0)
        {
            ShellNative.GetWindowThreadProcessId(tab, out var processId);
            if (processId != window.ProcessId || result.Contains(tab))
                throw new ShellSnapshotException();
            result.Add(tab);
        }
        return result.ToArray();
    }

    private static bool NativeTabsMatch(WindowId window, IEnumerable<nint> expected) =>
        ReadNativeTabs(window).ToHashSet().SetEquals(expected);

    private static bool NativeWindowSetsEqual(IReadOnlyList<NativeWindow> first, IReadOnlyList<NativeWindow> second) =>
        first.Count == second.Count && first.All(window => second.Any(candidate =>
            candidate.Id == window.Id && candidate.Tabs.ToHashSet().SetEquals(window.Tabs)));

    private static bool IsExplorerWindow(WindowId window)
    {
        if (window.Handle == 0 || window.ProcessId == 0 || !ShellNative.IsWindow(window.Handle) ||
            ShellNative.ClassName(window.Handle) != "CabinetWClass")
            return false;
        ShellNative.GetWindowThreadProcessId(window.Handle, out var process);
        return process == window.ProcessId;
    }

    private bool SameTabContents(TabState first, TabState second) =>
        _paths!.Equivalent(first.Location, second.Location) && first.Selection.Length == second.Selection.Length &&
        first.Selection.All(selected => second.Selection.Any(candidate => _paths.Equivalent(selected.Identity, candidate.Identity)));

    private static bool SameWindowState(WindowState first, WindowState second) =>
        first.Id == second.Id && first.Stamps.Length == second.Stamps.Length &&
        first.Stamps.ToHashSet().SetEquals(second.Stamps);

    private void AssertContext()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Environment.CurrentManagedThreadId != _ownerThread || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA ||
            !Application.MessageLoop || SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
            throw new InvalidOperationException("The Explorer backend requires its owning WinForms STA message pump.");
    }

    private void PruneVanishedTransfers() =>
        _partialTransfers.RemoveAll(transfer => !IsExplorerWindow(transfer.Source) || !IsExplorerWindow(transfer.Target));

    private void RecordDiagnosticFailure(Exception exception)
    {
        if (DiagnosticFailureType is not null)
            return;
        DiagnosticFailureType = exception.GetType().FullName;
        DiagnosticFailureHResult = exception.HResult;
    }

    private bool ResetDisconnectedShell(COMException exception)
    {
        if (exception.HResult is unchecked((int)0x80010108) or unchecked((int)0x800706BA) or
            unchecked((int)0x80010007) or unchecked((int)0x80010012) or unchecked((int)0x800401FD))
        {
            ResetShell(retainTransfers: true);
            return true;
        }
        return false;
    }

    private void ResetShell(bool retainTransfers = false)
    {
        ComReference.Release(_shellWindows);
        _shellWindows = null;
        _paths?.Dispose();
        _paths = null;
        if (retainTransfers)
            PruneVanishedTransfers();
        else
            _partialTransfers.Clear();
        _generation++;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        AssertContext();
        ResetShell();
        _disposed = true;
    }

    private sealed record SelectedItem(string Identity, string Name);

    private sealed class TabState(nint handle, string location, SelectedItem[] selection)
    {
        internal nint Handle { get; } = handle;
        internal string Location { get; } = location;
        internal SelectedItem[] Selection { get; } = selection;
        internal TabTransferStamp Stamp { get; } = new(handle, location, SelectionIdentity(selection));

        private static string SelectionIdentity(IEnumerable<SelectedItem> selected)
        {
            var value = new StringBuilder();
            foreach (var item in selected.OrderBy(item => item.Identity, StringComparer.Ordinal))
                value.Append(item.Identity.Length).Append(':').Append(item.Identity);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString())));
        }
    }

    private sealed class WindowState(WindowId id, TabState[] tabs)
    {
        internal WindowId Id { get; } = id;
        internal TabState[] Tabs { get; } = tabs;
        internal TabTransferStamp[] Stamps { get; } = tabs.Select(tab => tab.Stamp).ToArray();
    }

    private sealed class PartialTransfer(WindowId source, WindowId target, TabState sourceState)
    {
        internal WindowId Source { get; } = source;
        internal WindowId Target { get; } = target;
        internal TabState SourceState { get; } = sourceState;
        internal HashSet<nint> TargetBefore { get; set; } = [];
        internal nint TargetTab { get; set; }
        internal bool NewTabRequested { get; set; }
        internal bool NavigationIssued { get; set; }
        internal bool Verified { get; set; }
    }

    private sealed record NativeWindow(WindowId Id, nint[] Tabs);
    private sealed record RegisteredTab(nint Handle, object Browser);

    private sealed class RegisteredWindow(WindowId id)
    {
        internal WindowId Id { get; } = id;
        internal List<RegisteredTab> Tabs { get; } = [];
    }

    private sealed class RegisteredSnapshot : IDisposable
    {
        internal List<RegisteredWindow> Windows { get; } = [];

        internal RegisteredWindow FindWindow(WindowId id) =>
            Windows.SingleOrDefault(window => window.Id == id) ?? throw new ShellSnapshotException();

        public void Dispose()
        {
            foreach (var window in Windows)
                foreach (var tab in window.Tabs)
                    ComReference.Release(tab.Browser);
            Windows.Clear();
        }
    }
}
