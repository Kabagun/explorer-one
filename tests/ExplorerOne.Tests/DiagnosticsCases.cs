using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ExplorerOne.Diagnostics;

internal static class DiagnosticsCases
{
    private const string Sentinel = "private-message-sentinel";
    internal static IEnumerable<(string Name, Func<Task> Run)> All =>
    [
        Case("Diagnostic exceptions retain qualified method provenance without private content", directory =>
        {
            using var journal = Open(directory);
            var exception = CapturePrivateException();
            exception.Data["private-data-key"] = "private-data-value";
            journal.RecordException("poll_failed", exception, true);
            journal.Flush();
            var text = AllText(directory);
            Check(!text.Contains(Sentinel) && !text.Contains("private-data") && !text.Contains("private-source") && !text.Contains("private-stack"));
            using var entry = JsonDocument.Parse(ReadLines(journal.CurrentFilePath).Last());
            var errors = entry.RootElement.GetProperty("Errors");
            Check(errors.GetArrayLength() == 2 && errors[0].GetProperty("Type").GetString()!.Contains(nameof(PrivateException)));
            Check(errors[0].GetProperty("HResult").GetInt32() == exception.HResult);
            Check(errors[0].GetProperty("Methods").GetArrayLength() > 0);
            Check(errors[0].GetProperty("Methods").EnumerateArray().Any(method =>
                method.GetString() == typeof(DiagnosticsCases).FullName + "." + nameof(CapturePrivateException)));
            Check(errors[0].EnumerateObject().All(property => property.Name is "Type" or "HResult" or "Methods"));
        }),
        Case("Diagnostic informational version preserves its semantic version and commit suffix", directory =>
        {
            const string version = "0.2.2+0123456789abcdef0123456789abcdef01234567";
            using var journal = new DiagnosticJournal(directory, version, int.MaxValue, 1);
            journal.Record("heartbeat");
            journal.Flush();
            Check(journal.IsEnabled && AllLogEntries(directory).All(entry => entry.GetProperty("Version").GetString() == version));
        }),
        Case("Diagnostic nonzero exit remains a failed session without an exception callback", directory =>
        {
            using var journal = Open(directory);
            journal.CompleteSession("nonzero_exit", 9);
            Check(State(directory).GetProperty("Status").GetString() == "faulted");
            Check(State(directory).GetProperty("ExitCode").GetInt32() == 9);
        }),
        Case("Diagnostic version validation rejects private paths without relaxing event labels", directory =>
        {
            using var journal = new DiagnosticJournal(directory, "C:/private-version/0.2.2", int.MaxValue, 1);
            journal.Record("unsafe+event");
            journal.Record("safe_event", "unsafe+reason");
            journal.Record("safe_event", metrics: new Dictionary<string, long> { ["unsafe+key"] = 1 });
            journal.Flush();
            var entries = AllLogEntries(directory);
            Check(journal.IsEnabled && entries.Length == 1 && entries[0].GetProperty("Version").GetString() == "unknown");
            Check(!AllText(directory).Contains("private-version"));
        }),
        Case("Diagnostic relative directories stay disabled and preserve unrelated content", directory =>
        {
            var unrelated = Path.Combine(directory, "unrelated");
            Directory.CreateDirectory(unrelated);
            var preserved = Path.Combine(unrelated, "preserved.txt");
            File.WriteAllText(preserved, "unrelated");
            var originalDirectory = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = directory;
                using var journal = Open("unrelated");
                journal.Record("heartbeat");
                journal.RecordException("fatal_exception", new Exception(Sentinel), true);
                journal.CompleteSession("exit", 0);
                journal.Flush();
                Check(!journal.IsEnabled && journal.CurrentFilePath == "");
                Check(Directory.GetFileSystemEntries(unrelated).SequenceEqual([preserved]));
                Check(File.ReadAllText(preserved) == "unrelated");
                using var empty = Open("");
                Check(!empty.IsEnabled);
            }
            finally { Environment.CurrentDirectory = originalDirectory; }
        }),
        Case("Diagnostic labels reject paths, messages and unsafe metric keys", directory =>
        {
            using var journal = Open(directory);
            journal.Record("C:/private-folder");
            journal.Record("safe_event", "private reason");
            journal.Record("safe_event", metrics: new Dictionary<string, long> { ["C:/private-key"] = 3 });
            journal.Record("heartbeat", "timer", new Dictionary<string, long> { ["pending_count"] = 12 });
            journal.Flush();
            var lines = ReadLines(journal.CurrentFilePath);
            Check(lines.Length == 2 && !AllText(directory).Contains("private"));
            using var entry = JsonDocument.Parse(lines[1]);
            Check(entry.RootElement.GetProperty("Metrics").GetProperty("pending_count").GetInt64() == 12);
        }),
        Case("Diagnostic deep exceptions remain bounded in small log segments", directory =>
        {
            Exception exception = new InvalidOperationException(Sentinel);
            for (var i = 0; i < 30; i++) exception = new InvalidOperationException(Sentinel, exception);
            using var journal = Open(directory, maxBytes: 1024);
            journal.RecordException("deep_exception", exception, true);
            journal.Flush();
            var entry = AllLogEntries(directory).Last();
            Check(journal.IsEnabled && entry.GetProperty("Event").GetString() == "deep_exception");
            Check(entry.GetProperty("Errors").GetArrayLength() is > 0 and <= 4);
            Check(LogFiles(directory).All(path => new FileInfo(path).Length <= 1024));
            Check(!AllText(directory).Contains(Sentinel));
        }),
        Case("Diagnostic rotation bounds own files and preserves unrelated files", directory =>
        {
            File.WriteAllText(Path.Combine(directory, "keep.txt"), "unrelated");
            File.WriteAllText(Path.Combine(directory, "journal-unrelated.jsonl"), "unrelated");
            using var journal = Open(directory, maxBytes: 1024, maxFiles: 3);
            for (var i = 0; i < 120; i++) journal.Record("heartbeat", metrics: new Dictionary<string, long> { ["sequence"] = i });
            journal.Flush();
            var logs = LogFiles(directory);
            Check(journal.IsEnabled && logs.Length == 3 && logs.All(path => new FileInfo(path).Length <= 1024));
            Check(File.ReadAllText(Path.Combine(directory, "keep.txt")) == "unrelated");
            Check(File.ReadAllText(Path.Combine(directory, "journal-unrelated.jsonl")) == "unrelated");
            Check(AllLogEntries(directory).Last().GetProperty("Metrics").GetProperty("sequence").GetInt64() == 119);
        }),
        Case("Diagnostic normal exit does not report a previous interruption", directory =>
        {
            using (var journal = Open(directory)) journal.CompleteSession("tray_exit", 0);
            Check(State(directory).GetProperty("Status").GetString() == "clean");
            using var next = Open(directory);
            Check(next.IsEnabled && !Events(directory).Contains("session_interrupted"));
        }),
        Case("Diagnostic dispose without completion reports a dead previous session", directory =>
        {
            using (var journal = Open(directory)) journal.Record("heartbeat");
            Check(State(directory).GetProperty("Status").GetString() == "running");
            using var next = Open(directory);
            Check(next.IsEnabled);
            next.Flush();
            Check(Events(directory).Count(value => value == "session_interrupted") == 1);
        }),
        Case("Diagnostic clean exit preserves a prior fatal fault", directory =>
        {
            using (var journal = Open(directory))
            {
                journal.RecordException("fatal_exception", new InvalidOperationException(Sentinel), true);
                Check(State(directory).GetProperty("Status").GetString() == "faulted");
                journal.CompleteSession("process_exit", 1);
                Check(State(directory).GetProperty("Status").GetString() == "faulted");
                Check(State(directory).GetProperty("ExitCode").GetInt32() == 1);
            }
            using var next = Open(directory);
            next.Flush();
            Check(AllLogEntries(directory).Any(entry => entry.GetProperty("Event").GetString() == "session_interrupted" &&
                entry.GetProperty("Reason").GetString() == "previous_fault"));
        }),
        Case("Diagnostic exclusive ownership prevents concurrent journals", directory =>
        {
            using var first = Open(directory);
            var stateBefore = File.ReadAllText(Path.Combine(directory, "session-state.json"));
            using var second = Open(directory);
            Check(first.IsEnabled && !second.IsEnabled && File.ReadAllText(Path.Combine(directory, "session-state.json")) == stateBefore);
        }),
        Case("Diagnostic live PID and start-time owner is preserved after lock release", directory =>
        {
            using var process = Process.GetCurrentProcess();
            using (var first = new DiagnosticJournal(directory, "test", process.Id, process.StartTime.ToUniversalTime().ToFileTimeUtc()))
                Check(first.IsEnabled);
            var stateBefore = File.ReadAllText(Path.Combine(directory, "session-state.json"));
            using var second = Open(directory);
            Check(!second.IsEnabled && File.ReadAllText(Path.Combine(directory, "session-state.json")) == stateBefore);
        }),
        Case("Diagnostic reused PID with different start-time is treated as dead", directory =>
        {
            using (var first = new DiagnosticJournal(directory, "test", Environment.ProcessId, 1)) Check(first.IsEnabled);
            using var second = Open(directory);
            second.Flush();
            Check(second.IsEnabled && Events(directory).Contains("session_interrupted"));
        }),
        Case("Diagnostic invalid state is recovered without reading arbitrary fields into logs", directory =>
        {
            File.WriteAllText(Path.Combine(directory, "session-state.json"), "{private-corrupt-state");
            using var journal = Open(directory);
            Check(journal.IsEnabled && State(directory).GetProperty("Schema").GetInt32() == 1 && !AllText(directory).Contains("private-corrupt"));
        }),
        Case("Diagnostic inaccessible destinations and flush failures never escape", directory =>
        {
            var blocked = Path.Combine(directory, "blocked");
            File.WriteAllText(blocked, "unrelated");
            using var unavailable = Open(blocked);
            Check(!unavailable.IsEnabled);
            unavailable.Record("safe_event"); unavailable.RecordException("failed", new Exception(Sentinel), true);
            unavailable.CompleteSession("exit", 0); unavailable.Flush();
            using var journal = Open(directory);
            Directory.CreateDirectory(Path.Combine(directory, "session-state.tmp"));
            journal.Flush();
            Check(!journal.IsEnabled && File.ReadAllText(blocked) == "unrelated");
        }),
        Case("Diagnostic concurrent writes retain complete parseable events", directory =>
        {
            using var journal = Open(directory);
            Parallel.For(0, 1000, index => journal.Record("parallel_event", metrics: new Dictionary<string, long> { ["index"] = index }));
            journal.Flush();
            var entries = AllLogEntries(directory);
            Check(journal.IsEnabled && entries.Count(entry => entry.GetProperty("Event").GetString() == "parallel_event") == 1000);
            Check(entries.Where(entry => entry.GetProperty("Event").GetString() == "parallel_event")
                .Select(entry => entry.GetProperty("Metrics").GetProperty("index").GetInt64()).Distinct().Count() == 1000);
        })
    ];

    private static (string Name, Func<Task> Run) Case(string name, Action<string> run) => (name, () =>
    {
        var project = new DirectoryInfo(AppContext.BaseDirectory);
        while (project.Parent is not null && !File.Exists(Path.Combine(project.FullName, "Directory.Build.props"))) project = project.Parent;
        Check(File.Exists(Path.Combine(project.FullName, "Directory.Build.props")));
        var fixtureRoot = Path.GetFullPath(Path.Combine(project.FullName, "temp/diagnostics"));
        var directory = Path.Combine(fixtureRoot, "fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { run(directory); }
        finally
        {
            Check(Path.GetFullPath(directory).StartsWith(fixtureRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            Directory.Delete(directory, true);
        }
        return Task.CompletedTask;
    });
    private static DiagnosticJournal Open(string directory, long maxBytes = 1_048_576, int maxFiles = 10) =>
        new(directory, "0.0.0-test", int.MaxValue, 1, maxFileBytes: maxBytes, maxFiles: maxFiles);
    private static string[] LogFiles(string directory) => Directory.GetFiles(directory, "journal-*.jsonl")
        .Where(path => Path.GetFileName(path) != "journal-unrelated.jsonl").OrderBy(File.GetLastWriteTimeUtc).ThenBy(Path.GetFileName).ToArray();
    private static JsonElement[] AllLogEntries(string directory) => LogFiles(directory).SelectMany(ReadLines)
        .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); }).ToArray();
    private static IEnumerable<string?> Events(string directory) => AllLogEntries(directory).Select(entry => entry.GetProperty("Event").GetString());
    private static JsonElement State(string directory)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "session-state.json")));
        return document.RootElement.Clone();
    }
    private static string ReadText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    private static string[] ReadLines(string path) => ReadText(path).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    private static string AllText(string directory) => string.Join("\n", Directory.GetFiles(directory).Where(path => path.EndsWith(".jsonl") || path.EndsWith(".json")).Select(ReadText));
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception CapturePrivateException()
    {
        try
        {
#line 1 "C:/private-source/sensitive.cs"
            throw new PrivateException(new InvalidOperationException(Sentinel));
#line default
        }
        catch (Exception exception) { return exception; }
    }
    private sealed class PrivateException(Exception inner) : Exception(Sentinel, inner)
    {
        public override string Message => throw new InvalidOperationException("Message must not be read");
        public override string StackTrace => "private-stack-sentinel";
        public override string ToString() => throw new InvalidOperationException("ToString must not be read");
    }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed"); }
}
