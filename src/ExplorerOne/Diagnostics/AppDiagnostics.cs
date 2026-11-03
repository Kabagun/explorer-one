using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ExplorerOne.Opening;

namespace ExplorerOne.Diagnostics;

/// <summary>Lifecycle diagnostics only; folder locations and exception messages never enter this API.</summary>
internal static class AppDiagnostics
{
    private static readonly Stopwatch Uptime = new();
    private static readonly object ErrorGate = new();
    private static readonly Dictionary<(string Stage, Type Type, int Code), long> LastErrors = [];
    private static DiagnosticJournal? _journal;
    private static System.Threading.Timer? _heartbeat;
    private static NativeSnapshot? _native;
    private static ActivitySnapshot _activity = new("idle", 0, 0, 0, 0);
    private static string _phase = "starting";
    private static string? _shutdownReason;
    private static int _started, _completed, _sessionEnded;

    internal static string DirectoryPath => _journal?.DirectoryPath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExplorerOne", "Logs");
    internal static bool IsEnabled => _journal?.IsEnabled == true;
    internal static string? ShutdownReason => Volatile.Read(ref _shutdownReason);

    internal static void Start(DiagnosticJournal? journal = null)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        Uptime.Start();
        var version = typeof(AppDiagnostics).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(AppDiagnostics).Assembly.GetName().Version?.ToString() ?? "unknown";
        _journal = journal ?? DiagnosticJournal.OpenDefault(version);
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        try
        {
            SystemEvents.SessionEnding += OnSessionEnding;
            SystemEvents.SessionEnded += OnSessionEnded;
        }
        catch (Exception exception) { Error("system_events_subscription", exception); }
        var os = Environment.OSVersion.Version;
        var metrics = new Dictionary<string, long>
        {
            ["os_major"] = os.Major, ["os_minor"] = os.Minor,
            ["os_build"] = os.Build, ["os_revision"] = os.Revision,
            ["architecture"] = (int)RuntimeInformation.ProcessArchitecture
        };
        AddCurrentJob(metrics);
        Event("runtime_environment", metrics: metrics, flush: true);
        _heartbeat = new System.Threading.Timer(Heartbeat, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    internal static void Ready()
    {
        Volatile.Write(ref _phase, "running");
        Event("application_ready", flush: true);
    }

    internal static void ShutdownRequested(string reason)
    {
        Volatile.Write(ref _shutdownReason, reason);
        Volatile.Write(ref _phase, "shutdown_requested");
        Event("shutdown_requested", reason, flush: true);
    }

    internal static void Complete(string reason, int exitCode)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return;
        _heartbeat?.Dispose();
        _heartbeat = null;
        Event("main_returned", reason, new Dictionary<string, long>
        {
            ["exit_code"] = exitCode, ["uptime_ms"] = Uptime.ElapsedMilliseconds
        });
        _journal?.CompleteSession(reason, exitCode);
    }

    internal static void Fatal(string stage, Exception exception)
    {
        _journal?.Record("fault_context", Volatile.Read(ref _phase));
        _journal?.Record("last_operation", Volatile.Read(ref _activity).Stage);
        Volatile.Write(ref _phase, "faulted");
        _journal?.RecordException(stage, exception, fatal: true);
    }

    internal static void Error(string stage, Exception exception)
    {
        var key = (stage, exception.GetType(), exception.HResult);
        var now = Environment.TickCount64;
        lock (ErrorGate)
        {
            if (LastErrors.TryGetValue(key, out var last) && now - last < 30_000) return;
            if (LastErrors.Count >= 64) LastErrors.Clear();
            LastErrors[key] = now;
        }
        _journal?.RecordException(stage, exception);
        _journal?.Flush();
    }

    internal static void Event(string name, string? reason = null,
        IReadOnlyDictionary<string, long>? metrics = null, bool flush = false)
    {
        _journal?.Record(name, reason, metrics);
        if (flush) _journal?.Flush();
    }

    internal static void PublishActivity(string stage, int pending, int background, int failures) =>
        Volatile.Write(ref _activity, new(stage, pending, background, failures, Uptime.ElapsedMilliseconds));

    internal static void PublishNativeStatus(NativeLaunchRouterStatus status, bool active)
    {
        var key = new NativeKey(active, status.HResult, status.ModuleValidationResult, status.HookEnabled,
            status.HookRemoved, status.CleanupDeferred, status.Unsupported, status.ForeignPatch,
            status.SourcePresentationEnabled, status.SourcePresentationResult);
        var snapshot = new NativeSnapshot(key, status);
        var previous = Interlocked.Exchange(ref _native, snapshot);
        if (previous is null || previous.Key != key)
            Event("native_routing_state", active ? "active" : status.Unsupported ? "unsupported_profile" : "inactive",
                NativeMetrics(snapshot), flush: true);
    }

    private static void Heartbeat(object? state)
    {
        try
        {
            if (Volatile.Read(ref _completed) != 0) return;
            var activity = Volatile.Read(ref _activity);
            var metrics = new Dictionary<string, long>
            {
                ["uptime_ms"] = Uptime.ElapsedMilliseconds,
                ["pending_windows"] = activity.Pending,
                ["pending_background"] = activity.Background,
                ["background_failures"] = activity.Failures,
                ["ui_idle_ms"] = Math.Max(0, Uptime.ElapsedMilliseconds - activity.SeenAtMilliseconds)
            };
            var native = Volatile.Read(ref _native);
            if (native is not null)
                foreach (var metric in NativeMetrics(native))
                    if (metric.Key != "hook_removed") metrics[metric.Key] = metric.Value;
            Event("heartbeat", Volatile.Read(ref _phase), metrics);
            Event("last_operation", activity.Stage);
            if (native is not null)
                Event("native_counters", metrics: new Dictionary<string, long>
                {
                    ["calls"] = Clamp(native.Status.TotalCalls),
                    ["routed"] = Clamp(native.Status.RoutedCalls),
                    ["forwarded"] = Clamp(native.Status.ForwardedCalls),
                    ["failures"] = Clamp(native.Status.FailedRoutes),
                    ["stage"] = native.Status.Stage
                });
            _journal?.Flush();
        }
        catch { /* Diagnostics must never terminate a callback thread. */ }
    }

    private static Dictionary<string, long> NativeMetrics(NativeSnapshot native) => new()
    {
        ["active"] = native.Key.Active ? 1 : 0,
        ["hresult"] = native.Key.Result,
        ["module_validation"] = native.Key.ModuleResult,
        ["hook_enabled"] = native.Key.HookEnabled ? 1 : 0,
        ["hook_removed"] = native.Key.HookRemoved ? 1 : 0,
        ["cleanup_deferred"] = native.Key.CleanupDeferred ? 1 : 0,
        ["unsupported"] = native.Key.Unsupported ? 1 : 0,
        ["foreign_patch"] = native.Key.ForeignPatch ? 1 : 0,
        ["source_enabled"] = native.Key.SourceEnabled ? 1 : 0,
        ["source_result"] = native.Key.SourceResult,
        ["native_callbacks"] = native.Status.ActiveCallbacks,
        ["last_route_result"] = native.Status.LastRouteResult
    };

    private static long Clamp(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        try
        {
            if (args.ExceptionObject is Exception exception) Fatal("unhandled_exception", exception);
            else Event("unhandled_non_exception", metrics: new Dictionary<string, long>
                { ["terminating"] = args.IsTerminating ? 1 : 0 }, flush: true);
        }
        catch { }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        try { Error("unobserved_task_exception", args.Exception); }
        catch { }
    }

    private static void OnSessionEnding(object sender, SessionEndingEventArgs args)
    {
        try { Event("windows_session_ending", SessionReason(args.Reason), flush: true); }
        catch { }
    }

    private static void OnSessionEnded(object sender, SessionEndedEventArgs args)
    {
        try
        {
            Volatile.Write(ref _sessionEnded, 1);
            ShutdownRequested(SessionReason(args.Reason));
        }
        catch { }
    }

    private static string SessionReason(SessionEndReasons reason) =>
        reason == SessionEndReasons.Logoff ? "windows_logoff" : "windows_shutdown";

    private static void OnProcessExit(object? sender, EventArgs args)
    {
        try
        {
            _heartbeat?.Dispose();
            if (Volatile.Read(ref _completed) == 0)
            {
                Event("process_exit_without_main_return", ShutdownReason,
                    new Dictionary<string, long> { ["exit_code"] = Environment.ExitCode }, flush: true);
                if (Volatile.Read(ref _sessionEnded) != 0)
                    _journal?.CompleteSession(ShutdownReason ?? "windows_session_end", Environment.ExitCode);
            }
            _journal?.Flush();
            _journal?.Dispose();
        }
        catch { }
    }

    private static void AddCurrentJob(Dictionary<string, long> metrics)
    {
        try
        {
            if (Native.IsProcessInJob(new nint(-1), 0, out var grouped)) metrics["in_job"] = grouped ? 1 : 0;
            var memory = Marshal.AllocHGlobal(144);
            try
            {
                if (Native.QueryInformationJobObject(0, 9, memory, 144, out _))
                {
                    var flags = unchecked((uint)Marshal.ReadInt32(memory, 16));
                    metrics["job_limits_read"] = 1;
                    metrics["kill_on_job_close"] = (flags & 0x2000) != 0 ? 1 : 0;
                }
                else metrics["job_limits_read"] = 0;
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        catch { metrics["job_limits_read"] = 0; }
    }

    private sealed record ActivitySnapshot(string Stage, int Pending, int Background, int Failures, long SeenAtMilliseconds);
    private sealed record NativeSnapshot(NativeKey Key, NativeLaunchRouterStatus Status);
    private readonly record struct NativeKey(bool Active, int Result, int ModuleResult, bool HookEnabled,
        bool HookRemoved, bool CleanupDeferred, bool Unsupported, bool ForeignPatch, bool SourceEnabled, int SourceResult);

    private static class Native
    {
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool grouped);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryInformationJobObject(nint job, int information, nint data, uint length, out uint returned);
    }
}
