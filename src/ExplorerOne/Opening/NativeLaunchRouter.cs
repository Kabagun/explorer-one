using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.RegularExpressions;
using ExplorerOne.Core;

namespace ExplorerOne.Opening;

internal sealed record NativeLaunchRouterStatus(
    int HResult = 0, int ModuleValidationResult = 0, bool HookEnabled = false,
    bool HookRemoved = true, bool CleanupDeferred = false, bool Unsupported = false,
    bool ForeignPatch = false, uint ActiveCallbacks = 0, ulong TotalCalls = 0,
    ulong RoutedCalls = 0, ulong ForwardedCalls = 0, ulong FailedRoutes = 0,
    ulong RecursiveCalls = 0, int InitializeStatus = 0, int CreateStatus = 0,
    int EnableStatus = 0, int DisableStatus = 0, int RemoveStatus = 0,
    int UninitializeStatus = 0, int LastRouteResult = 0, int LastOriginalResult = 0,
    uint LastFlags = 0, uint Stage = 0, int SourcePresentationResult = 0)
{
    internal bool SourcePresentationEnabled => (Stage & 0x40000000u) != 0;
}

/// <summary>Controls the authenticated, persistent agent on desktop Explorer's STA.</summary>
internal sealed class NativeLaunchRouter : IAsyncDisposable
{
    private const int MappingSize = 592, StateOffset = 16;
    private const int Pending = 0, Running = 1, Complete = 2, Cancelled = 3;
    private const int Start = 1, Update = 2, Stop = 3, QueryStatus = 4;
    private const int PendingResult = unchecked((int)0x8000000A);
    private const int CancelledResult = unchecked((int)0x800704C7);
    private const int TimeoutResult = unchecked((int)0x800705B4);
    private const string MappingPrefix = "Local\\ExplorerOne.NativeLaunchRouter.V1.";
    private const string MessageName = "ExplorerOne.NativeLaunchRouter.V1.Command";
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource stopping = new();
    private readonly TimeSpan timeout;
    private readonly ulong nonceLow, nonceHigh;
    private NativeLaunchRouterStatus status = new();
    private ProcessWindow? shell;
    private ProcessWindow? configuredTarget;
    private RequestLease? pending;
    private Task pendingCompletion = Task.CompletedTask;
    private nint module, hookProcedure, agentWindow;
    private CancelPending? cancelPending;
    private uint commandMessage;
    private string? controllerSid;
    private ulong controllerStart;
    private uint controllerSession;
    private long lastStatusAt;
    private volatile bool active, stopRequested;
    private bool disposed;

    internal NativeLaunchRouter(TimeSpan? timeout = null)
    {
        this.timeout = timeout ?? TimeSpan.FromSeconds(3);
        Span<byte> random = stackalloc byte[16];
        RandomNumberGenerator.Fill(random);
        nonceLow = BitConverter.ToUInt64(random) | 1;
        nonceHigh = BitConverter.ToUInt64(random[8..]) | 1;
    }

    public bool IsActive => active;
    internal NativeLaunchRouterStatus Status => Volatile.Read(ref status);

    // Enter the pool before validation/loader work: these entry points are called
    // by the tray's STA and must never synchronously wait for Explorer.
    public Task UpdateAsync(WindowId? main, CancellationToken token) => Task.Run(async () =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stopping.Token);
        try { await gate.WaitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        try
        {
            if (disposed || linked.IsCancellationRequested) return;
            stopRequested = false;
            await UpdateCoreAsync(main, linked.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { Fail(exception.HResult); }
        finally { gate.Release(); }
    }, CancellationToken.None);

    public Task StopAsync(CancellationToken token = default)
    {
        stopRequested = true;
        active = false;
        return Task.Run(async () =>
        {
            try { await gate.WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try { if (!disposed) await StopCoreAsync(token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { Fail(exception.HResult); }
            finally { gate.Release(); }
        }, CancellationToken.None);
    }

    private async Task UpdateCoreAsync(WindowId? main, CancellationToken token)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        { Fail(unchecked((int)0x80070032)); return; }
        InitializeController();
        var desktop = ReadDesktop();
        if (desktop is null) { active = false; return; }
        if (shell is null || !shell.SameProcess(desktop))
        {
            // A dead Explorer can never consume an old mapping or hook again.
            // A still-live previous shell is stopped before changing generations.
            if (shell is not null && IsAlive(shell) && agentWindow != 0)
                await StopCoreAsync(token).ConfigureAwait(false);
            if (pending is not null) return;
            shell = desktop;
            agentWindow = 0;
            configuredTarget = null;
            active = false;
            Volatile.Write(ref status, new());
        }
        else shell = desktop;
        if (pending is not null) return;
        var target = main is { } candidate ? ReadTarget(candidate) : null;
        if (main is not null && target is null) active = false;
        LoadModule();
        var targetChanged = !Equals(configuredTarget, target);
        var native = Status;
        var needsStart = agentWindow == 0 || (!native.HookEnabled && !native.CleanupDeferred && !native.Unsupported && !native.ForeignPatch);
        var action = needsStart ? Start : targetChanged && native.HookEnabled ? Update : QueryStatus;
        if (action == QueryStatus && Stopwatch.GetElapsedTime(lastStatusAt) < TimeSpan.FromMilliseconds(500)) return;
        await SendAsync(action, target, token).ConfigureAwait(false);
    }

    private async Task StopCoreAsync(CancellationToken token)
    {
        active = false;
        if (pending is not null) return;
        if (shell is null || !IsAlive(shell))
        { agentWindow = 0; configuredTarget = null; return; }
        if (agentWindow == 0) return;
        await SendAsync(Stop, null, token).ConfigureAwait(false);
        var watch = Stopwatch.StartNew();
        while (pending is null && agentWindow != 0 && (!Status.HookRemoved || Status.CleanupDeferred || Status.ActiveCallbacks != 0))
        {
            if (token.IsCancellationRequested || watch.Elapsed >= timeout)
            { Fail(token.IsCancellationRequested ? CancelledResult : TimeoutResult); return; }
            if (Status.ForeignPatch || Status.HResult < 0) return;
            await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
            await SendAsync(QueryStatus, null, token).ConfigureAwait(false);
        }
    }

    private async Task SendAsync(int action, ProcessWindow? target, CancellationToken token)
    {
        if (shell is null || token.IsCancellationRequested) return;
        var lease = RequestLease.Create(this, shell, target, action, cancelPending!);
        var retained = false;
        try
        {
            if (action == Start)
            {
                NativeSourceAppearance.ConfigureEnabled(true);
                lease.Hook = Native.SetWindowsHookExW(3, hookProcedure, module, shell.Thread);
                if (lease.Hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            var receiver = action == Start ? shell.Window : agentWindow;
            if (!MatchesReceiver(receiver, shell)) throw new Win32Exception(5);
            if (!Native.PostMessageW(receiver, commandMessage, unchecked((nuint)nonceLow), (nint)lease.Atom))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var watch = Stopwatch.StartNew();
            while (lease.State is Pending or Running)
            {
                if (!IsAlive(lease.Shell)) { Fail(unchecked((int)0x80070490)); return; }
                if (token.IsCancellationRequested || watch.Elapsed >= timeout)
                {
                    var previous = lease.Cancel();
                    Fail(token.IsCancellationRequested ? CancelledResult : TimeoutResult);
                    if (previous == Running)
                    { Retain(lease); retained = true; }
                    else if (previous == Complete) ApplyReply(lease);
                    return;
                }
                await Task.Delay(15, CancellationToken.None).ConfigureAwait(false);
            }
            if (lease.State == Complete) ApplyReply(lease);
        }
        catch
        {
            var previous = lease.Cancel();
            if (previous == Running) { Retain(lease); retained = true; }
            throw;
        }
        finally { if (!retained) lease.Dispose(); }
    }

    private void Retain(RequestLease lease)
    {
        pending = lease;
        pendingCompletion = CompletePendingAsync(lease);
    }

    private async Task CompletePendingAsync(RequestLease lease)
    {
        // A running native callback owns the mapping until completion. Neither
        // cancellation nor shutdown may unmap it or remove its bootstrap hook.
        while ((lease.State is Pending or Running) && IsAlive(lease.Shell))
            await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (pending != lease) return;
            if (lease.State == Complete && shell is not null && shell.SameProcess(lease.Shell)) ApplyReply(lease);
            lease.Dispose();
            pending = null;
            if (stopRequested) await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { Fail(exception.HResult); }
        finally { gate.Release(); }
    }

    private void ApplyReply(RequestLease lease)
    {
        var reply = lease.ReadStatus();
        var candidate = unchecked((nint)lease.Read64(472));
        if (candidate != 0 && MatchesReceiver(candidate, lease.Shell)) agentWindow = candidate;
        else if (!reply.HookEnabled) agentWindow = 0;
        Volatile.Write(ref status, reply);
        NativeSourceAppearance.ConfigureEnabled(reply.SourcePresentationEnabled);
        lastStatusAt = Stopwatch.GetTimestamp();
        if (reply.HResult >= 0 && lease.Action is Start or Update) configuredTarget = lease.Target;
        if (lease.Action == Stop && reply.HResult >= 0) configuredTarget = null;
        active = !stopRequested && reply.HResult >= 0 && reply.HookEnabled && configuredTarget is not null;
    }

    private void Fail(int result)
    { active = false; Volatile.Write(ref status, Status with { HResult = result }); }

    private void InitializeController()
    {
        if (controllerSid is not null) return;
        if (Marshal.SizeOf<ProtocolLayout>() != MappingSize || Marshal.OffsetOf<ProtocolLayout>(nameof(ProtocolLayout.Result)).ToInt32() != 464)
            throw new InvalidOperationException("Native launch protocol layout mismatch.");
        using var identity = WindowsIdentity.GetCurrent();
        controllerSid = identity.User?.Value ?? throw new InvalidOperationException("Current user SID unavailable.");
        controllerSession = unchecked((uint)Process.GetCurrentProcess().SessionId);
        controllerStart = ProcessStart(Native.GetCurrentProcess());
        if (controllerStart == 0 || controllerSid.Length >= 184) throw new Win32Exception(5);
        NativeSourceAppearance.ConfigureOwner(nonceLow, nonceHigh);
    }

    private void LoadModule()
    {
        if (module != 0)
        {
            if (hookProcedure == 0 || cancelPending is null) throw new EntryPointNotFoundException();
            if (commandMessage == 0) throw new Win32Exception(6);
            return;
        }
        var filename = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ExplorerOne.NativeLaunchRouter.current"));
        if (!Regex.IsMatch(filename, @"\AExplorerOne\.NativeLaunchRouter\.v1\.[a-f0-9]{16}\.dll\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Invalid native launch router manifest.");
        var candidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, filename));
        if (!string.Equals(Path.GetDirectoryName(candidate), Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid native launch router location.");
        var loaded = Native.LoadLibraryExW(candidate, 0, 0x100 | 0x800);
        if (loaded == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        // Retain the local loader reference for the controller's lifetime. A
        // bootstrap callback may still be running when an operation times out.
        module = loaded;
        hookProcedure = Native.GetProcAddress(loaded, "NativeLaunchRouterGetMessageProc");
        var cancel = Native.GetProcAddress(loaded, "NativeLaunchRouterCancelPending");
        if (hookProcedure == 0 || cancel == 0) throw new EntryPointNotFoundException();
        cancelPending = Marshal.GetDelegateForFunctionPointer<CancelPending>(cancel);
        // A source version has its own bootstrap message. A delayed hook from a
        // pinned older DLL must not claim a new version's shell-window command.
        commandMessage = Native.RegisterWindowMessageW(MessageName + "." + filename);
        if (commandMessage == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private ProcessWindow? ReadDesktop()
    {
        var window = Native.GetShellWindow();
        var thread = Native.GetWindowThreadProcessId(window, out var process);
        if (window == 0 || thread == 0 || process == 0) return null;
        var start = ValidateProcess(process);
        return start != 0 && Native.GetShellWindow() == window && Native.GetWindowThreadProcessId(window, out var current) == thread && current == process
            ? new(window, process, thread, start) : null;
    }

    private ProcessWindow? ReadTarget(WindowId target)
    {
        if (!Native.IsWindow(target.Handle) || Native.GetAncestor(target.Handle, 2) != target.Handle || ClassName(target.Handle) != "CabinetWClass") return null;
        var thread = Native.GetWindowThreadProcessId(target.Handle, out var process);
        if (thread == 0 || process != target.ProcessId) return null;
        var start = ValidateProcess(process);
        return start != 0 && Native.GetWindowThreadProcessId(target.Handle, out var current) == thread && current == process
            ? new(target.Handle, process, thread, start) : null;
    }

    private ulong ValidateProcess(uint process)
    {
        if (!Native.ProcessIdToSessionId(process, out var session) || session != controllerSession) return 0;
        var handle = Native.OpenProcess(0x1000, false, process);
        if (handle == 0) return 0;
        try
        {
            if (!Native.GetExitCodeProcess(handle, out var code) || code != 259 ||
                !Native.IsWow64Process2(handle, out var machine, out var nativeMachine) || machine != 0 || nativeMachine != 0x8664) return 0;
            var image = new char[32768]; uint length = (uint)image.Length;
            if (!Native.QueryFullProcessImageNameW(handle, 0, image, ref length) || !string.Equals(new string(image, 0, (int)length),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), StringComparison.OrdinalIgnoreCase)) return 0;
            if (!Native.OpenProcessToken(handle, 8, out var token)) return 0;
            try
            {
                Native.GetTokenInformation(token, 1, 0, 0, out var required);
                if (required is 0 or > 65536) return 0;
                var data = Marshal.AllocHGlobal(checked((int)required));
                try
                {
                    if (!Native.GetTokenInformation(token, 1, data, required, out _)) return 0;
                    if (!string.Equals(new SecurityIdentifier(Marshal.ReadIntPtr(data)).Value, controllerSid, StringComparison.Ordinal)) return 0;
                }
                finally { Marshal.FreeHGlobal(data); }
            }
            finally { Native.CloseHandle(token); }
            return ProcessStart(handle);
        }
        finally { Native.CloseHandle(handle); }
    }

    private static bool MatchesReceiver(nint window, ProcessWindow process) => window != 0 && Native.IsWindow(window) &&
        Native.GetWindowThreadProcessId(window, out var pid) == process.Thread && pid == process.Process && IsAlive(process);
    private static ulong ProcessStart(nint process) => Native.GetProcessTimes(process, out var start, out _, out _, out _) ? start : 0;
    private static bool IsAlive(ProcessWindow process)
    {
        var handle = Native.OpenProcess(0x1000, false, process.Process);
        if (handle == 0) return false;
        try { return ProcessStart(handle) == process.Start && Native.GetExitCodeProcess(handle, out var code) && code == 259; }
        finally { Native.CloseHandle(handle); }
    }
    private static string ClassName(nint window)
    {
        var name = new char[128]; var length = Native.GetClassNameW(window, name, name.Length);
        return length > 0 ? new string(name, 0, length) : string.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        await StopAsync().ConfigureAwait(false);
        await gate.WaitAsync().ConfigureAwait(false);
        try { disposed = true; }
        finally { gate.Release(); }
        // A hung Explorer cannot block tray shutdown. The pending task retains
        // its mapping/hook until its native callback completes or Explorer exits.
        await Task.WhenAny(pendingCompletion, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        NativeSourceAppearance.ConfigureEnabled(false);
        NativeSourceAppearance.ClearOwner();
    }

    private sealed record ProcessWindow(nint Window, uint Process, uint Thread, ulong Start)
    { internal bool SameProcess(ProcessWindow other) => Process == other.Process && Start == other.Start; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int CancelPending(nint mapping);

    // A checked marshal layout documents the native ABI without unsafe code.
    [StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Unicode)]
    private struct ProtocolLayout
    {
        public uint Magic, Size, Version, Action;
        public int State;
        public uint ControllerPid, ControllerSession, ShellPid, ShellThread, TargetPid, Reserved;
        public ulong ControllerStart, ShellStart, TargetRoot, TargetStart, NonceLow, NonceHigh;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 184)] public string Sid;
        public int Result, ModuleValidation;
        public ulong Agent, Total, Routed, Forwarded, Failed, Recursive;
        public uint Enabled, Removed, Deferred, Callbacks, Unsupported, Foreign, Pinned;
        public int Initialize, Create, Enable, Disable, Remove, Uninitialize, LastRoute, LastOriginal;
        public uint Flags, Stage;
    }

    private sealed class RequestLease : IDisposable
    {
        internal readonly ProcessWindow Shell;
        internal readonly ProcessWindow? Target;
        internal readonly int Action;
        internal nint Hook;
        internal ushort Atom;
        private nint mapping, view;
        private readonly CancelPending cancel;
        private RequestLease(ProcessWindow shell, ProcessWindow? target, int action, CancelPending cancel)
        { Shell = shell; Target = target; Action = action; this.cancel = cancel; }
        internal int State => Read32(StateOffset);
        internal int Read32(int offset) { Thread.MemoryBarrier(); return Marshal.ReadInt32(view, offset); }
        internal ulong Read64(int offset) { Thread.MemoryBarrier(); return unchecked((ulong)Marshal.ReadInt64(view, offset)); }
        internal int Cancel() => cancel(view);
        internal NativeLaunchRouterStatus ReadStatus() => new(Read32(464), Read32(468), Read32(520) != 0, Read32(524) != 0,
            Read32(528) != 0, Read32(536) != 0, Read32(540) != 0, unchecked((uint)Read32(532)), Read64(480), Read64(488),
            Read64(496), Read64(504), Read64(512), Read32(548), Read32(552), Read32(556), Read32(560), Read32(564),
            Read32(568), Read32(572), Read32(576), unchecked((uint)Read32(580)), unchecked((uint)Read32(584)), Read32(40));
        internal static RequestLease Create(NativeLaunchRouter owner, ProcessWindow shell, ProcessWindow? target, int action, CancelPending cancel)
        {
            var lease = new RequestLease(shell, target, action, cancel);
            nint descriptor = 0;
            try
            {
                var sid = owner.controllerSid!;
                if (!Native.ConvertStringSecurityDescriptorToSecurityDescriptorW($"O:{sid}D:P(A;;GA;;;{sid})", 1, out descriptor, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
                var name = MappingPrefix + Guid.NewGuid().ToString("N");
                lease.mapping = Native.CreateFileMappingW((nint)(-1), ref security, 4, 0, MappingSize, name);
                var error = Marshal.GetLastWin32Error();
                if (lease.mapping == 0 || error == 183) throw new Win32Exception(error);
                lease.view = Native.MapViewOfFile(lease.mapping, 2 | 4, 0, 0, MappingSize);
                if (lease.view == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                var request = new ProtocolLayout
                {
                    Magic = 0x31524F45, Size = MappingSize, Version = 1, Action = unchecked((uint)action), State = Pending,
                    ControllerPid = unchecked((uint)Environment.ProcessId), ControllerSession = owner.controllerSession,
                    ShellPid = shell.Process, ShellThread = shell.Thread, TargetPid = target?.Process ?? 0,
                    ControllerStart = owner.controllerStart, ShellStart = shell.Start,
                    TargetRoot = unchecked((ulong)(target?.Window ?? 0)), TargetStart = target?.Start ?? 0,
                    NonceLow = owner.nonceLow, NonceHigh = owner.nonceHigh, Sid = sid, Result = PendingResult
                };
                Marshal.StructureToPtr(request, lease.view, false);
                Thread.MemoryBarrier();
                lease.Atom = Native.GlobalAddAtomW(name);
                if (lease.Atom == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                return lease;
            }
            catch { lease.Dispose(); throw; }
            finally { if (descriptor != 0) Native.LocalFree(descriptor); }
        }
        public void Dispose()
        {
            if (Hook != 0) { Native.UnhookWindowsHookEx(Hook); Hook = 0; }
            if (Atom != 0) { Native.GlobalDeleteAtom(Atom); Atom = 0; }
            if (view != 0) { Native.UnmapViewOfFile(view); view = 0; }
            if (mapping != 0) { Native.CloseHandle(mapping); mapping = 0; }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public nint Descriptor; public int Inherit; }
    private static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint LoadLibraryExW(string path, nint file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] internal static extern nint GetProcAddress(nint module, string name);
        [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWindowsHookExW(int type, nint procedure, nint module, uint thread);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWindowsHookEx(nint hook);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessageW(nint window, uint message, nuint first, nint second);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern uint RegisterWindowMessageW(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ushort GlobalAddAtomW(string value);
        [DllImport("kernel32.dll")] internal static extern ushort GlobalDeleteAtom(ushort atom);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateFileMappingW(nint file, ref SecurityAttributes security, uint protection, uint high, uint low, string name);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint MapViewOfFile(nint mapping, uint access, uint high, uint low, nuint size);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnmapViewOfFile(nint view);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(nint handle);
        [DllImport("kernel32.dll")] internal static extern nint LocalFree(nint value);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string value, uint revision, out nint descriptor, out uint size);
        [DllImport("user32.dll")] internal static extern nint GetShellWindow();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint window);
        [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window, uint flags);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint process);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassNameW(nint window, [Out] char[] name, int size);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ProcessIdToSessionId(uint process, out uint session);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint process);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryFullProcessImageNameW(nint process, uint flags, [Out] char[] image, ref uint length);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetProcessTimes(nint process, out ulong created, out ulong exited, out ulong kernel, out ulong user);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetExitCodeProcess(nint process, out uint code);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWow64Process2(nint process, out ushort machine, out ushort nativeMachine);
        [DllImport("kernel32.dll")] internal static extern nint GetCurrentProcess();
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool OpenProcessToken(nint process, uint access, out nint token);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetTokenInformation(nint token, int informationClass, nint information, uint length, out uint required);
    }
}
