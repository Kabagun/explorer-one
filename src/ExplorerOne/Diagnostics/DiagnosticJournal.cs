using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExplorerOne.Diagnostics;

/// <summary>Bounded local diagnostics. Never pass user content as an event label or metric key.</summary>
internal sealed class DiagnosticJournal : IDisposable
{
    private const long DefaultFileBytes = 1_048_576;
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly string _version;
    private readonly int _processId;
    private readonly long _processStart;
    private readonly long _maxFileBytes;
    private readonly int _maxFiles;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private FileStream? _ownership;
    private FileStream? _log;
    private long _length;
    private int _part;
    private bool _enabled;
    private bool _disposed;
    private bool _completed;
    private string _status = "running";
    private string _lastEvent = "session_started";
    private DateTimeOffset _lastSeen;
    private int? _exitCode;

    public string DirectoryPath { get; }
    public string CurrentFilePath { get; private set; } = "";
    public bool IsEnabled { get { lock (_gate) return _enabled; } }

    public static DiagnosticJournal OpenDefault(string version)
    {
        // Retrieving process metadata can fail under restricted hosts; the constructor then disables itself.
        try
        {
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localData) || !Path.IsPathFullyQualified(localData)) return new("", version, 0, 0);
            using var process = Process.GetCurrentProcess();
            return new(Path.Combine(localData, "ExplorerOne", "Logs"),
                version, Environment.ProcessId, process.StartTime.ToUniversalTime().ToFileTimeUtc());
        }
        catch { return new("", version, 0, 0); }
    }

    public DiagnosticJournal(string directory, string version, int processId, long processStartUtcFileTime,
        TimeProvider? clock = null, long maxFileBytes = DefaultFileBytes, int maxFiles = 10)
    {
        DirectoryPath = directory;
        _version = IsVersion(version) ? version : "unknown";
        _processId = processId;
        _processStart = processStartUtcFileTime;
        _clock = clock ?? TimeProvider.System;
        _maxFileBytes = maxFileBytes;
        _maxFiles = maxFiles;
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) || processId <= 0 || processStartUtcFileTime <= 0 ||
                maxFileBytes is < 1024 or > DefaultFileBytes || maxFiles is < 1 or > 10) return;
            Directory.CreateDirectory(directory);
            _ownership = new(Path.Combine(directory, "journal.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var previous = ReadState();
            if (previous is not null && IsPreviousOwnerLiveOrUnknown(previous)) { Disable(); return; }
            _enabled = true;
            OpenLog();
            Write(new Entry { Event = "session_started" });
            if (previous is { Status: "running" or "faulted" })
                Write(new Entry { Event = "session_interrupted", Reason = previous.Status == "faulted" ? "previous_fault" : "previous_exit_unobserved",
                    Metrics = new Dictionary<string, long> { ["previous_pid"] = previous.ProcessId, ["previous_last_seen_utc_ticks"] = previous.LastSeenUtc.UtcTicks } });
            FlushCore();
        }
        catch { Disable(); }
    }

    public void Record(string eventName, string? reason = null, IReadOnlyDictionary<string, long>? metrics = null)
    {
        lock (_gate)
        {
            if (!_enabled || _completed || !IsLabel(eventName) || (reason is not null && !IsLabel(reason))) return;
            try
            {
                Dictionary<string, long>? safeMetrics = null;
                if (metrics is not null)
                {
                    if (metrics.Count > 16) return;
                    safeMetrics = new();
                    foreach (var pair in metrics)
                    {
                        if (!IsLabel(pair.Key)) return;
                        safeMetrics.Add(pair.Key, pair.Value);
                    }
                }
                Write(new Entry { Event = eventName, Reason = reason, Metrics = safeMetrics });
            }
            catch { Disable(); }
        }
    }

    public void RecordException(string eventName, Exception exception, bool fatal = false)
    {
        lock (_gate)
        {
            if (!_enabled || _completed || !IsLabel(eventName) || exception is null) return;
            try
            {
                var errors = new List<ErrorInfo>();
                for (Exception? current = exception; current is not null && errors.Count < 4; current = current.InnerException)
                {
                    var type = current.GetType().FullName;
                    var frames = new StackTrace(current, false).GetFrames()?.Take(8)
                        .Select(QualifiedMethodName).Where(name => name is not null).Cast<string>().ToArray();
                    errors.Add(new ErrorInfo { Type = IsCodeName(type) ? type! : "Exception", HResult = current.HResult, Methods = frames });
                }
                if (fatal) _status = "faulted";
                Write(new Entry { Event = eventName, Fatal = fatal, Errors = errors.ToArray() });
                if (fatal) FlushCore();
            }
            catch { Disable(); }
        }
    }

    public void CompleteSession(string reason, int exitCode)
    {
        lock (_gate)
        {
            if (!_enabled || _completed || !IsLabel(reason)) return;
            try
            {
                _exitCode = exitCode;
                if (_status != "faulted") _status = exitCode == 0 ? "clean" : "faulted";
                Write(new Entry { Event = "session_completed", Reason = reason, Metrics = new Dictionary<string, long> { ["exit_code"] = exitCode } });
                FlushCore();
                _completed = true;
            }
            catch { Disable(); }
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (!_enabled) return;
            try { FlushCore(); } catch { Disable(); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_enabled) { try { FlushCore(); } catch { } }
            _disposed = true;
            Disable();
        }
    }

    private void Write(Entry entry)
    {
        entry.Utc = _clock.GetUtcNow();
        entry.SessionId = _sessionId;
        entry.Version = _version;
        entry.ProcessId = _processId;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions);
        if (bytes.Length + 1 > _maxFileBytes)
        {
            // Small configured segments still preserve exception identity without unbounded frame or metric data.
            entry.Metrics = null;
            if (entry.Errors is { Length: > 0 })
                entry.Errors = [new ErrorInfo { Type = entry.Errors[0].Type, HResult = entry.Errors[0].HResult }];
            bytes = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions);
        }
        if (bytes.Length + 1 > _maxFileBytes) return;
        if (_length + bytes.Length + 1 > _maxFileBytes) { _log!.Flush(true); _log.Dispose(); OpenLog(); }
        _log!.Write(bytes);
        _log.WriteByte((byte)'\n');
        _length += bytes.Length + 1;
        _lastEvent = entry.Event;
        _lastSeen = entry.Utc;
    }

    private void OpenLog()
    {
        CurrentFilePath = Path.Combine(DirectoryPath, $"journal-{_clock.GetUtcNow():yyyyMMddTHHmmssfffffffZ}-{_sessionId}-{_part:D6}.jsonl");
        _part = (_part + 1) % 1_000_000;
        _log = new(CurrentFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        _length = 0;
        // Only delete files with our exact generated shape. Other content is never retention-managed.
        var files = Directory.EnumerateFiles(DirectoryPath, "journal-*.jsonl").Where(IsOwnLog).Where(path => path != CurrentFilePath)
            .OrderByDescending(File.GetLastWriteTimeUtc).ThenByDescending(Path.GetFileName, StringComparer.Ordinal).ToArray();
        foreach (var file in files.Skip(_maxFiles - 1)) File.Delete(file);
    }

    private static bool IsOwnLog(string path)
    {
        var name = Path.GetFileName(path);
        return name.Length == 77 && name.StartsWith("journal-", StringComparison.Ordinal) && name.EndsWith(".jsonl", StringComparison.Ordinal) &&
            DateTimeOffset.TryParseExact(name.AsSpan(8, 23), "yyyyMMddTHHmmssfffffffZ", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out _) && name[31] == '-' &&
            Guid.TryParseExact(name.AsSpan(32, 32), "N", out _) && name[64] == '-' && name.AsSpan(65, 6).ContainsAnyExceptInRange('0', '9') == false;
    }

    private void FlushCore()
    {
        _log!.Flush(true);
        var state = new SessionState { SessionId = _sessionId, ProcessId = _processId, ProcessStartUtcFileTime = _processStart,
            Status = _status, LastSeenUtc = _lastSeen, LastEvent = _lastEvent, ExitCode = _exitCode };
        var temporary = Path.Combine(DirectoryPath, "session-state.tmp");
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, state, JsonOptions);
            stream.Flush(true);
        }
        File.Move(temporary, Path.Combine(DirectoryPath, "session-state.json"), true);
    }

    private SessionState? ReadState()
    {
        var path = Path.Combine(DirectoryPath, "session-state.json");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 8192) return null;
        try
        {
            var state = JsonSerializer.Deserialize<SessionState>(File.ReadAllBytes(path));
            return state is { Schema: 1, ProcessId: > 0, ProcessStartUtcFileTime: > 0 } &&
                Guid.TryParseExact(state.SessionId, "N", out _) && state.Status is "running" or "faulted" or "clean" &&
                IsLabel(state.LastEvent) && state.LastSeenUtc != default ? state : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool IsPreviousOwnerLiveOrUnknown(SessionState previous)
    {
        try
        {
            using var process = Process.GetProcessById(previous.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().ToFileTimeUtc() == previous.ProcessStartUtcFileTime;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch { return true; }
    }

    private void Disable()
    {
        _enabled = false;
        try { _log?.Dispose(); } catch { }
        try { _ownership?.Dispose(); } catch { }
        _log = null;
        _ownership = null;
    }

    private static bool IsLabel(string? value) => value is { Length: > 0 and <= 64 } &&
        value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.');
    private static bool IsVersion(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '.' or '+');
    private static bool IsCodeName(string? value) => value is { Length: > 0 and <= 160 } &&
        value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '.' or '+' or '`' or '<' or '>' or '|');
    private static string? QualifiedMethodName(StackFrame frame)
    {
        var method = frame.GetMethod();
        var declaringType = method?.DeclaringType?.FullName;
        if (IsCodeName(declaringType) && IsCodeName(method?.Name))
        {
            var qualified = declaringType + "." + method!.Name;
            if (IsCodeName(qualified)) return qualified;
        }
        return IsCodeName(method?.Name) ? method!.Name : null;
    }

    private sealed class Entry
    {
        public DateTimeOffset Utc { get; set; }
        public string SessionId { get; set; } = "";
        public string Version { get; set; } = "";
        public int ProcessId { get; set; }
        public string Event { get; set; } = "";
        public string? Reason { get; set; }
        public Dictionary<string, long>? Metrics { get; set; }
        public bool? Fatal { get; set; }
        public ErrorInfo[]? Errors { get; set; }
    }
    private sealed class ErrorInfo
    {
        public string Type { get; set; } = "";
        public int HResult { get; set; }
        public string[]? Methods { get; set; }
    }
    private sealed class SessionState
    {
        public int Schema { get; set; } = 1;
        public string SessionId { get; set; } = "";
        public int ProcessId { get; set; }
        public long ProcessStartUtcFileTime { get; set; }
        public string Status { get; set; } = "";
        public DateTimeOffset LastSeenUtc { get; set; }
        public string LastEvent { get; set; } = "";
        public int? ExitCode { get; set; }
    }
}
