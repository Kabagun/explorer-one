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
    private const int DestinationTimeoutMilliseconds = 6000;
    private const int PollMilliseconds = 40;
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
    internal long DiagnosticTransferMilliseconds { get; private set; }
    internal long DiagnosticSourceStabilityMilliseconds { get; private set; }
    internal long DiagnosticRegistryMilliseconds { get; private set; }
    internal int DiagnosticRegistryReadCount { get; private set; }
    internal long DiagnosticTabReadyMilliseconds { get; private set; }
    internal long DiagnosticNavigateCallMilliseconds { get; private set; }
    internal long DiagnosticNavigationReadyMilliseconds { get; private set; }
    internal int DiagnosticDestinationPollCount { get; private set; }

    public ShellExplorerBackend()
    {
        AssertContext();
    }

    public IReadOnlyList<ExplorerWindow> EnumerateWindows()
    {
        AssertContext();
        try
        {
            // Discovery is a complete NATIVE inventory. A different window
            // registering its COM view must not prevent routing a ready source.
            var native = ReadNativeWindows();
            var excluded = FindKnownControlPanelWindows(native);
            if (!NativeWindowSetsEqual(native, ReadNativeWindows()))
                throw new ShellSnapshotException();
            return native.Where(window => !excluded.Contains(window.Id))
                .Select(window => new ExplorerWindow(window.Id, window.Tabs.Length)).ToArray();
        }
        catch
        {
            // Never expose a shell exception containing a location, or report
            // an incomplete native inventory as the entire set of windows.
            throw new ShellSnapshotException();
        }
    }

    public async Task<TransferResult> TransferAsync(WindowId source, WindowId target, CancellationToken cancellationToken)
    {
        AssertContext();
        DiagnosticPhase = "transfer-start";
        DiagnosticFailureType = null;
        DiagnosticFailureHResult = 0;
        DiagnosticTransferMilliseconds = 0;
        DiagnosticSourceStabilityMilliseconds = 0;
        DiagnosticRegistryMilliseconds = 0;
        DiagnosticRegistryReadCount = 0;
        DiagnosticTabReadyMilliseconds = 0;
        DiagnosticNavigateCallMilliseconds = 0;
        DiagnosticNavigationReadyMilliseconds = 0;
        DiagnosticDestinationPollCount = 0;
        if (_transferring)
            return TransferResult.Retry;
        if (source == target || source.Handle == target.Handle || !IsExplorerWindow(target))
            return TransferResult.Unsafe;
        if (!IsExplorerWindow(source))
            return TransferResult.SourceGone;

        _transferring = true;
        var transferStartedAt = Stopwatch.GetTimestamp();
        var copyingStarted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            PruneVanishedTransfers();
            // Retain a complete snapshot of BOTH endpoints for this transfer.
            // Unrelated windows may still be registering their COM views.
            using var initial = ReadScopedRegisteredTabs(source, target);
            var sourceWindow = initial.FindWindow(source);
            var generation = _generation;
            var observationStartedAt = Environment.TickCount64;
            var original = await CaptureStableSourceAsync(sourceWindow, generation, observationStartedAt, cancellationToken);
            if (original is null)
            {
                DiagnosticPhase = "source-not-settled";
                return IsExplorerWindow(source) ? TransferResult.NotReady : TransferResult.SourceGone;
            }
            var transfers = new List<PartialTransfer>();
            for (var index = 0; index < original.Tabs.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = CaptureRetainedWindow(sourceWindow, generation);
                if (generation != _generation)
                    return copyingStarted ? TransferResult.Retry : TransferResult.NotReady;
                if (!SameWindowState(original, current))
                {
                    if (copyingStarted)
                        return TransferResult.Retry;

                    // A late /select update before Ctrl+T is still preflight:
                    // stabilize the latest source within the same bounded
                    // observation period instead of taking a long retry.
                    original = await CaptureStableSourceAsync(sourceWindow, generation, observationStartedAt, cancellationToken);
                    if (original is null)
                        return IsExplorerWindow(source) ? TransferResult.NotReady : TransferResult.SourceGone;
                    index = -1;
                    continue;
                }
                var tab = original.Tabs[index];

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

                copyingStarted = true;
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

            // Re-read both endpoint windows in a fresh complete scoped COM/
            // native snapshot. All source AND target native tabs are covered;
            // no source mutation precedes this gate.
            using (var final = ReadScopedRegisteredTabs(source, target))
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
                    !NativeTabsMatch(source, original.Tabs.Select(tab => tab.Handle)) ||
                    !NativeTabsMatch(target, targetWindow.Tabs.Select(tab => tab.Handle)))
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
            ActivateTarget(target);
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
        catch (ShellSnapshotException exception) when (!copyingStarted)
        {
            RecordDiagnosticFailure(exception);
            DiagnosticPhase = "source-not-ready";
            return IsExplorerWindow(source) ? TransferResult.NotReady : TransferResult.SourceGone;
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
            DiagnosticTransferMilliseconds = ElapsedMilliseconds(transferStartedAt);
            _transferring = false;
        }
    }

    private async Task<WindowState?> CaptureStableSourceAsync(
        RegisteredWindow source, int generation, long observationStartedAt, CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var stability = new SourceStability(observationStartedAt);
        try
        {
            while (!stability.IsExpired(Environment.TickCount64))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsExplorerWindow(source.Id))
                    return null;

                // A busy or incomplete source asks for a cheap retry; no
                // destination request has been made at this point.
                var current = CaptureRetainedWindow(source, generation);
                if (stability.Observe(current.Stamps, current.Tabs.Any(tab => tab.Selection.Length == 0), Environment.TickCount64))
                    return current;

                DiagnosticPhase = "source-stabilizing";
                await Task.Delay(SourceStability.SampleIntervalMilliseconds, cancellationToken);
            }
            return null;
        }
        finally
        {
            DiagnosticSourceStabilityMilliseconds += ElapsedMilliseconds(startedAt);
        }
    }

    private WindowState CaptureRetainedWindow(RegisteredWindow window, int generation)
    {
        RefreshShell();
        if (generation != _generation || !NativeTabsMatch(window.Id, window.Tabs.Select(tab => tab.Handle)))
            throw new ShellSnapshotException();
        var state = CaptureWindow(window);
        if (generation != _generation)
            throw new ShellSnapshotException();
        return state;
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
            transfer.TargetBefore = ReadNativeTabs(transfer.Target).ToHashSet();
            var active = ShellNative.FindWindowEx(transfer.Target.Handle, 0, "ShellTabWindowClass", null);
            if (!transfer.TargetBefore.Contains(active))
                throw new ShellSnapshotException();

            transfer.NewTabRequested = true;
            DiagnosticPhase = "request-new-tab";
            ActivateTarget(transfer.Target);
            if (!ShellNative.PostMessage(active, ShellNative.WmCommand, ShellNative.NewTabCommand, 0))
                throw new ShellSnapshotException();
        }

        var deadline = Environment.TickCount64 + DestinationTimeoutMilliseconds;
        var selectionAppliedThisAttempt = false;
        var tabRequestedAt = Stopwatch.GetTimestamp();
        long? navigationStartedAt = null;
        RegisteredTab? reference = null;
        try
        {
            while (Environment.TickCount64 < deadline)
            {
                DiagnosticDestinationPollCount++;
                cancellationToken.ThrowIfCancellationRequested();
                RefreshShell();
                if (generation != _generation || !IsExplorerWindow(transfer.Source) || !IsExplorerWindow(transfer.Target))
                    throw new ShellSnapshotException();

                try
                {
                    if (transfer.TargetTab == 0)
                    {
                        DiagnosticPhase = "wait-native-tab";
                        var added = ReadNativeTabs(transfer.Target).Where(tab => !transfer.TargetBefore.Contains(tab)).ToArray();
                        if (added.Length > 1)
                            throw new UnsupportedShellViewException();
                        if (added.Length == 0)
                        {
                            await Task.Delay(PollMilliseconds, cancellationToken);
                            continue;
                        }
                        // Retain the exact handle even before COM registration;
                        // retrying this request must not send another Ctrl+T.
                        transfer.TargetTab = added[0];
                    }

                    if (!NativeTabBelongsTo(transfer.Target, transfer.TargetTab))
                        throw new UnsupportedShellViewException();
                    if (reference is null)
                    {
                        DiagnosticPhase = "wait-tab-registration";
                        reference = FindRegisteredTab(transfer.Target, transfer.TargetTab, generation);
                        if (reference is null)
                        {
                            await Task.Delay(PollMilliseconds, cancellationToken);
                            continue;
                        }
                        DiagnosticTabReadyMilliseconds += ElapsedMilliseconds(tabRequestedAt);
                    }
                    ValidateReference(reference, transfer.Target);
                    if (!transfer.NavigationIssued)
                    {
                        // The default Home view need not be portable. Navigate
                        // only our newly associated native+registered tab.
                        transfer.NavigationIssued = true;
                        ActivateTarget(transfer.Target);
                        navigationStartedAt = Stopwatch.GetTimestamp();
                        Navigate(reference.Browser, transfer.SourceState.Location);
                    }

                    TabState current;
                    try
                    {
                        current = CaptureTab(reference);
                    }
                    catch (UnsupportedShellViewException) when (!transfer.Verified)
                    {
                        await Task.Delay(PollMilliseconds, cancellationToken);
                        continue;
                    }
                    if (SameTabContents(transfer.SourceState, current))
                    {
                        if (navigationStartedAt.HasValue)
                            DiagnosticNavigationReadyMilliseconds += ElapsedMilliseconds(navigationStartedAt.Value);
                        transfer.Verified = true;
                        return true;
                    }
                    if (transfer.Verified)
                        throw new UnsupportedShellViewException(); // The user changed an already copied tab.

                    var sameLocation = _paths!.Equivalent(transfer.SourceState.Location, current.Location);
                    if (sameLocation && !selectionAppliedThisAttempt)
                    {
                        if (!current.Selection.All(selected => transfer.SourceState.Selection.Any(expected =>
                            _paths.Equivalent(expected.Identity, selected.Identity))))
                            throw new UnsupportedShellViewException();
                        selectionAppliedThisAttempt = true;
                        DiagnosticPhase = "apply-selection";
                        ValidateReference(reference, transfer.Target);
                        ApplySelection(reference.Browser, transfer.SourceState.Selection);
                        if (SameTabContents(transfer.SourceState, CaptureTab(reference)))
                        {
                            if (navigationStartedAt.HasValue)
                                DiagnosticNavigationReadyMilliseconds += ElapsedMilliseconds(navigationStartedAt.Value);
                            transfer.Verified = true;
                            return true;
                        }
                    }
                }
                catch (ShellSnapshotException)
                {
                    // Transient registration/busy views are allowed to settle.
                }

                await Task.Delay(PollMilliseconds, cancellationToken);
            }
            return false;
        }
        finally
        {
            reference?.Dispose();
        }
    }

    private WindowState CaptureWindow(WindowId id)
    {
        using var snapshot = ReadScopedRegisteredTabs(id);
        return CaptureWindow(snapshot.FindWindow(id));
    }

    // A targeted lookup proves only this tab association, never a complete
    // window snapshot. Final verification always uses ReadScopedRegisteredTabs.
    private RegisteredTab? FindRegisteredTab(WindowId window, nint handle, int generation)
    {
        RefreshShell();
        if (generation != _generation || !NativeTabBelongsTo(window, handle))
            throw new ShellSnapshotException();
        dynamic shellWindows = _shellWindows!;
        int count = shellWindows.Count;
        for (var index = count - 1; index >= 0; index--)
        {
            object? browser = null;
            IShellBrowser? shellBrowser = null;
            try
            {
                browser = shellWindows.Item(index);
                if (browser is null)
                    continue;
                nint browserWindow = new(Convert.ToInt64(((dynamic)browser).HWND, CultureInfo.InvariantCulture));
                if (ShellNative.GetAncestor(browserWindow, ShellNative.RootAncestor) != window.Handle ||
                    browser is not IShellServiceProvider services)
                    continue;
                var service = ShellBrowserInterface;
                var iid = ShellBrowserInterface;
                if (services.QueryService(ref service, ref iid, out shellBrowser) < 0 || shellBrowser is null ||
                    shellBrowser.GetWindow(out var actual) < 0 || actual != handle)
                    continue;
                if (generation != _generation || !NativeTabBelongsTo(window, handle))
                    throw new ShellSnapshotException();
                var reference = new RegisteredTab(handle, browser);
                browser = null;
                return reference;
            }
            catch (Exception exception) when (exception is COMException or InvalidComObjectException or
                Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
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
        return null;
    }

    private static void ValidateReference(RegisteredTab reference, WindowId window)
    {
        if (!NativeTabBelongsTo(window, reference.Handle))
            throw new ShellSnapshotException();
        IShellBrowser? shellBrowser = null;
        try
        {
            nint browserWindow = new(Convert.ToInt64(((dynamic)reference.Browser).HWND, CultureInfo.InvariantCulture));
            if (ShellNative.GetAncestor(browserWindow, ShellNative.RootAncestor) != window.Handle ||
                reference.Browser is not IShellServiceProvider services)
                throw new ShellSnapshotException();
            var service = ShellBrowserInterface;
            var iid = ShellBrowserInterface;
            if (services.QueryService(ref service, ref iid, out shellBrowser) < 0 || shellBrowser is null ||
                shellBrowser.GetWindow(out var actual) < 0 || actual != reference.Handle)
                throw new ShellSnapshotException();
        }
        finally
        {
            ComReference.Release(shellBrowser);
        }
    }

    private static bool NativeTabBelongsTo(WindowId window, nint tab)
    {
        if (!IsExplorerWindow(window) || !ShellNative.IsWindow(tab) ||
            ShellNative.ClassName(tab) != "ShellTabWindowClass" ||
            ShellNative.GetAncestor(tab, ShellNative.RootAncestor) != window.Handle)
            return false;
        ShellNative.GetWindowThreadProcessId(tab, out var process);
        return process == window.ProcessId;
    }

    private static void ActivateTarget(WindowId target)
    {
        if (!IsExplorerWindow(target))
            return;
        if (ShellNative.IsIconic(target.Handle))
            _ = ShellNative.ShowWindowAsync(target.Handle, 9); // SW_RESTORE.
        _ = ShellNative.SetForegroundWindow(target.Handle);
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
        var startedAt = Stopwatch.GetTimestamp();
        object? shell = null;
        object? folder = null;
        try
        {
            // Match the upstream fast path for ordinary filesystem parsing
            // names. Keep Folder objects for URI-sensitive literal #/% and
            // namespace locations whose existing behavior is already proven.
            if (Path.IsPathFullyQualified(location) && !location.Contains('#') && !location.Contains('%'))
            {
                ((dynamic)browser).Navigate2(location);
                return;
            }
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
            DiagnosticNavigateCallMilliseconds += ElapsedMilliseconds(startedAt);
        }
    }

    private HashSet<WindowId> FindKnownControlPanelWindows(IReadOnlyList<NativeWindow> native)
    {
        var excluded = new HashSet<WindowId>();
        if (native.Count == 0)
            return excluded;
        try
        {
            RefreshShell();
            dynamic shellWindows = _shellWindows!;
            int count = shellWindows.Count;
            for (var index = 0; index < count; index++)
            {
                object? browser = null;
                IShellBrowser? shellBrowser = null;
                try
                {
                    browser = shellWindows.Item(index);
                    if (browser is null)
                        continue;
                    nint browserWindow = new(Convert.ToInt64(((dynamic)browser).HWND, CultureInfo.InvariantCulture));
                    var root = ShellNative.GetAncestor(browserWindow, ShellNative.RootAncestor);
                    var window = native.FirstOrDefault(candidate => candidate.Id.Handle == root);
                    if (window is null || excluded.Contains(window.Id) || !BrowserIsControlPanel(browser) ||
                        browser is not IShellServiceProvider services)
                        continue;
                    var service = ShellBrowserInterface;
                    var iid = ShellBrowserInterface;
                    if (services.QueryService(ref service, ref iid, out shellBrowser) >= 0 && shellBrowser is not null &&
                        shellBrowser.GetWindow(out var tab) >= 0 && window.Tabs.Contains(tab) && NativeTabBelongsTo(window.Id, tab))
                        excluded.Add(window.Id);
                }
                catch (Exception exception)
                {
                    // Classification is optional. An unregistered/unreadable
                    // view stays in the complete native inventory until it can
                    // be positively recognized; it cannot block other windows.
                    RecordDiagnosticFailure(exception);
                    if (exception is COMException disconnected && ResetDisconnectedShell(disconnected))
                    {
                        excluded.Clear();
                        return excluded;
                    }
                }
                finally
                {
                    ComReference.Release(shellBrowser);
                    ComReference.Release(browser);
                }
            }
        }
        catch (Exception exception)
        {
            RecordDiagnosticFailure(exception);
            if (exception is COMException disconnected)
                ResetDisconnectedShell(disconnected);
            excluded.Clear();
        }
        return excluded;
    }

    private static bool BrowserIsControlPanel(object browser)
    {
        string url = ((dynamic)browser).LocationURL;
        if (IsControlPanelLocation(url))
            return true;
        if (!string.IsNullOrEmpty(url))
            return false; // Ordinary file tabs need no view capture.

        object? document = null;
        object? folder = null;
        object? self = null;
        try
        {
            document = ((dynamic)browser).Document;
            if (document is null)
                return false;
            folder = ((dynamic)document).Folder;
            self = ((dynamic)folder).Self;
            return IsControlPanelLocation((string)((dynamic)self).Path);
        }
        finally
        {
            ComReference.Release(self);
            ComReference.Release(folder);
            ComReference.Release(document);
        }
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

    private RegisteredSnapshot ReadRegisteredTabs() => ReadRegisteredTabsMeasured(null);

    private RegisteredSnapshot ReadScopedRegisteredTabs(params WindowId[] windows) =>
        ReadRegisteredTabsMeasured(windows);

    private RegisteredSnapshot ReadRegisteredTabsMeasured(IReadOnlyList<WindowId>? requiredWindows)
    {
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            return ReadRegisteredTabsCore(requiredWindows);
        }
        finally
        {
            DiagnosticRegistryReadCount++;
            DiagnosticRegistryMilliseconds += ElapsedMilliseconds(startedAt);
        }
    }

    private RegisteredSnapshot ReadRegisteredTabsCore(IReadOnlyList<WindowId>? requiredWindows)
    {
        DiagnosticPhase = "connect-shell";
        RefreshShell();
        DiagnosticPhase = "enumerate-native-windows";
        var native = requiredWindows is null ? ReadNativeWindows() : ReadNativeWindowScope(requiredWindows);
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
                    // still requires a registered object for EVERY native tab
                    // in the requested scope, including all target tabs.
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
            var after = requiredWindows is null ? ReadNativeWindows() : ReadNativeWindowScope(requiredWindows);
            if ((requiredWindows is null && (int)shellWindows.Count != count) || !NativeWindowSetsEqual(native, after) ||
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

    private static List<NativeWindow> ReadNativeWindowScope(IReadOnlyList<WindowId> requiredWindows)
    {
        if (requiredWindows.Count == 0 || requiredWindows.Distinct().Count() != requiredWindows.Count)
            throw new ShellSnapshotException();
        var result = new List<NativeWindow>(requiredWindows.Count);
        foreach (var id in requiredWindows)
        {
            var tabs = ReadNativeTabs(id);
            if (tabs.Length == 0)
                throw new ShellSnapshotException();
            result.Add(new NativeWindow(id, tabs));
        }
        return result;
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

    private static long ElapsedMilliseconds(long startedAt) =>
        (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

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
    private sealed record RegisteredTab(nint Handle, object Browser) : IDisposable
    {
        public void Dispose() => ComReference.Release(Browser);
    }

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
                    tab.Dispose();
            Windows.Clear();
        }
    }
}
