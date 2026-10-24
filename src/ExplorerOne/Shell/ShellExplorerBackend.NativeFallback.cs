using System.Diagnostics;
using System.Runtime.InteropServices;
using ExplorerOne.Core;
using ExplorerOne.Opening;

namespace ExplorerOne.Shell;

public sealed partial class ShellExplorerBackend
{
    private bool _nativeFolderRoutingEnabled;
    public bool NativeFolderRoutingEnabled
    {
        get => _nativeFolderRoutingEnabled;
        set
        {
            if (_nativeFolderRoutingEnabled == value) return;
            _nativeFolderRoutingEnabled = value;
            if (value) NativeForegroundFolder.Prepare(); // Bind the installed export before a new source appears.
        }
    }
    private PartialTransfer? _nativeFallbackAllocation;
    internal int DiagnosticNativeFallbackEagerStarts { get; private set; }
    internal long DiagnosticNativeFallbackWorkerQueueMilliseconds { get; private set; }
    internal long DiagnosticNativeFallbackNativeCallMilliseconds { get; private set; }

    private void TryStartEagerNativeFallback(RegisteredWindow source, WindowId target, int generation,
        CancellationToken cancellationToken)
    {
        if (!NativeFolderRoutingEnabled || source.Tabs.Count != 1 || _nativeFallbackAllocation is not null ||
            _nativeReservations.HasPending || _partialTransfers.Any(transfer => transfer.Source == source.Id)) return;
        cancellationToken.ThrowIfCancellationRequested();
        string location;
        try
        {
            location = ReadEagerNativeFolder(source.Tabs[0], source.Id);
            if (!HasTransferShell(generation) || !NativeTabsMatch(source.Id, [source.Tabs[0].Handle])) return;
        }
        catch (Exception exception) when (exception is COMException or ShellSnapshotException or
            UnsupportedShellViewException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        { return; } // Folder proof may become available with the normal complete capture.
        var transfer = GetOrCreateTransfer(source.Id, target, new TabState(source.Tabs[0].Handle, location, []));
        transfer.SourceCapturePending = true;
        var started = TryStartNativeFallbackRequest(transfer);
        if (started == true) DiagnosticNativeFallbackEagerStarts++;
        else if (transfer.NativeFallback is null)
        {
            transfer.SourceCapturePending = false;
            _partialTransfers.Remove(transfer);
        }
    }

    private string ReadEagerNativeFolder(RegisteredTab reference, WindowId source)
    {
        ValidateReference(reference, source);
        string url = ((dynamic)reference.Browser).LocationURL;
        if (!IsAbsoluteFileUrl(url) || reference.Browser is not IShellServiceProvider services)
            throw new ShellSnapshotException();
        IShellBrowser? browser = null;
        object? view = null, folder = null;
        nint viewPointer = 0, folderPointer = 0, pidl = 0, name = 0;
        try
        {
            var service = ShellBrowserInterface; var iid = service;
            if (services.QueryService(ref service, ref iid, out browser) < 0 || browser is not EagerShellBrowser native ||
                native.QueryActiveShellView(out viewPointer) < 0 || viewPointer == 0) throw new ShellSnapshotException();
            view = Marshal.GetObjectForIUnknown(viewPointer);
            var persist = typeof(EagerPersistFolder).GUID;
            if (view is not EagerFolderView folderView || folderView.GetFolder(ref persist, out folderPointer) < 0 ||
                folderPointer == 0) throw new ShellSnapshotException();
            folder = Marshal.GetObjectForIUnknown(folderPointer);
            if (folder is not EagerPersistFolder persisted || persisted.GetCurFolder(out pidl) < 0 || pidl == 0 ||
                ShellNative.SHGetNameFromIDList(pidl, ShellNative.DesktopAbsoluteParsing, out name) < 0 || name == 0)
                throw new ShellSnapshotException();
            var location = Marshal.PtrToStringUni(name) ?? throw new ShellSnapshotException();
            if (!_paths!.Equivalent(location, url) || !StringComparer.Ordinal.Equals(url, (string)((dynamic)reference.Browser).LocationURL))
                throw new ShellSnapshotException();
            ValidateReference(reference, source);
            return location;
        }
        finally
        {
            Marshal.FreeCoTaskMem(name); Marshal.FreeCoTaskMem(pidl);
            ComReference.Release(folder); ComReference.Release(view); ComReference.Release(browser);
            if (folderPointer != 0) Marshal.Release(folderPointer);
            if (viewPointer != 0) Marshal.Release(viewPointer);
        }
    }

    private bool? TryStartNativeFallbackRequest(PartialTransfer transfer)
    {
        var before = ReadNativeTabs(transfer.Target);
        if (before.Length is 0 or >= 512 || !NativeFolderRoutingEnabled) return false;
        var request = new NativeFallbackRequest(before, transfer.SourceState.Location);
        transfer.NativeFallback = request;
        _nativeFallbackAllocation = transfer;
        DiagnosticPhase = "native-direct-folder";
        request.Attempted = true;
        request.Dispatch = NativeForegroundFolder.TryOpenAsync(transfer.Target, request.Location);
        if (request.Dispatch is not null) return true;
        transfer.NativeFallback = null;
        _nativeFallbackAllocation = null;
        return null;
    }

    private async Task<bool?> TryEnsureNativeFallbackTabAsync(PartialTransfer transfer, int generation,
        CancellationToken cancellationToken)
    {
        if (transfer.NativeFallback is null && _nativeFallbackAllocation is null &&
            (!NativeFolderRoutingEnabled || transfer.NativeReservation is not null)) return null;
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            if (_nativeFallbackAllocation is { } pending && !ReferenceEquals(pending, transfer))
            {
                if (pending.NativeFallback?.Dispatch is { IsCompletedSuccessfully: true } completed)
                {
                    RecordNativeFallbackResult(pending, completed.Result);
                    if (!IsExplorerWindow(pending.Target)) _nativeFallbackAllocation = null;
                }
                if (_nativeFallbackAllocation is not null) return false;
            }
            if (transfer.NativeFallback is null && (!NativeFolderRoutingEnabled || transfer.NativeReservation is not null)) return null;
            if (transfer.NativeFallback is null)
            {
                if (_nativeReservations.HasPending) return false;
                cancellationToken.ThrowIfCancellationRequested();
                if (!HasTransferShell(generation) || !IsExplorerWindow(transfer.Target) ||
                    !NativeTabBelongsTo(transfer.Source, transfer.SourceState.Handle)) throw new ShellSnapshotException();
                var started = TryStartNativeFallbackRequest(transfer);
                if (started != true) return started;
            }
            var direct = transfer.NativeFallback ?? throw new ShellSnapshotException();
            if (direct.Dispatch is not null)
            {
                // Do not abandon an in-flight native call on cancellation: its
                // definitive attempted/result/created-tab acknowledgement owns recovery.
                RecordNativeFallbackResult(transfer, await direct.Dispatch);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!direct.Accepted) return false;
            if (!HasTransferShell(generation) || !NativeTabBelongsTo(transfer.Target, direct.CreatedTab) ||
                direct.Before.Contains(direct.CreatedTab) || direct.Before.Any(tab => !NativeTabBelongsTo(transfer.Target, tab)))
                throw new ShellSnapshotException();
            transfer.TargetTab = direct.CreatedTab;
            transfer.NavigationIssued = true;
            if (transfer.BackgroundOwned) await Task.Yield();
            return true;
        }
        finally { DiagnosticAllocationMilliseconds += ElapsedMilliseconds(startedAt); }
    }

    private void RecordNativeFallbackResult(PartialTransfer transfer, NativeForegroundOpenResult result)
    {
        var request = transfer.NativeFallback ?? throw new ShellSnapshotException();
        request.Attempted = result.Attempted;
        DiagnosticNativeFallbackWorkerQueueMilliseconds = result.WorkerQueueMilliseconds;
        DiagnosticNativeFallbackNativeCallMilliseconds = result.NativeCallMilliseconds;
        request.CreatedTab = result.CreatedTab;
        request.Accepted = result.Attempted && result.HResult >= 0 && result.CreatedTab != 0;
        if (result.HResult < 0) RecordDiagnosticFailure(new System.Runtime.InteropServices.COMException("Native foreground folder operation failed.", result.HResult));
        if (ReferenceEquals(_nativeFallbackAllocation, transfer))
        {
            // A false attempted flag is a definitive pre-commit failure. A
            // committed operation releases allocation ownership only once its
            // exact returned native tab is known; unknown outcomes stay retained.
            if (!result.Attempted || request.Accepted) _nativeFallbackAllocation = null;
        }
    }

    private sealed class NativeFallbackRequest(nint[] before, string location)
    {
        internal nint[] Before { get; } = before;
        internal string Location { get; } = location;
        internal Task<NativeForegroundOpenResult>? Dispatch;
        internal bool Attempted, Accepted;
        internal nint CreatedTab;
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface EagerShellBrowser
    {
        [PreserveSig] int GetWindow(out nint window);
        void _VtblGap1_11();
        [PreserveSig] int QueryActiveShellView(out nint view);
    }
    [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface EagerFolderView
    {
        void _VtblGap1_2();
        [PreserveSig] int GetFolder(ref Guid iid, out nint folder);
    }
    [ComImport, Guid("1AC3D9F0-175C-11D1-95BE-00609797EA4F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface EagerPersistFolder
    {
        void _VtblGap1_2();
        [PreserveSig] int GetCurFolder(out nint folder);
    }
}
