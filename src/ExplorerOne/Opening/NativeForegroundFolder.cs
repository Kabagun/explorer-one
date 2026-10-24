using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Diagnostics;
using ExplorerOne.Core;

namespace ExplorerOne.Opening;

internal readonly record struct NativeForegroundOpenResult(bool Attempted, int HResult, nint CreatedTab, uint Stage,
    long WorkerQueueMilliseconds = 0, long NativeCallMilliseconds = 0);

internal static class NativeForegroundFolder
{
    private static readonly object LoaderGate = new();
    private static nint module;
    private static OpenFolder? entry;

    internal static void Prepare() => _ = LoadEntry();

    private static (OpenFolder? Entry, int Error, bool Unavailable) LoadEntry()
    {
        try
        {
            lock (LoaderGate)
            {
                if (entry is not null) return (entry, 0, false);
                var filename = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ExplorerOne.NativeLaunchRouter.current"));
                if (!Regex.IsMatch(filename, @"\AExplorerOne\.NativeLaunchRouter\.v1\.[a-f0-9]{16}\.dll\z", RegexOptions.CultureInvariant))
                    return (null, unchecked((int)0x80070005), false);
                var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, filename));
                if (!string.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
                    return (null, unchecked((int)0x80070005), false);
                if (module == 0) module = Native.LoadLibraryExW(path, 0, 0x100 | 0x800);
                if (module == 0)
                {
                    var error = Marshal.GetLastWin32Error();
                    return (null, Marshal.GetHRForLastWin32Error(), error is 2 or 3 or 126 or 127);
                }
                var address = Native.GetProcAddress(module, "NativeOpenForegroundFolder");
                if (address == 0) return (null, unchecked((int)0x8007007F), true);
                entry = Marshal.GetDelegateForFunctionPointer<OpenFolder>(address);
                return (entry, 0, false);
            }
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return (null, exception.HResult, true); }
        catch (Exception exception) { return (null, exception.HResult, false); }
    }

    // Null means the native component/export is absent before dispatch. Other
    // failures remain explicit results and never authorize a duplicate request.
    internal static Task<NativeForegroundOpenResult>? TryOpenAsync(WindowId target, string folder)
    {
        var requestedAt = Stopwatch.GetTimestamp();
        if (folder.Length is 0 or > 32767 || folder.Contains('\0'))
            return Task.FromResult(new NativeForegroundOpenResult(false, unchecked((int)0x80070057), 0, 0));
        var loaded = LoadEntry();
        if (loaded.Entry is null) return loaded.Unavailable ? null : Task.FromResult(new NativeForegroundOpenResult(false, loaded.Error, 0, 0));
        var invoke = loaded.Entry;
        var completion = new TaskCompletionSource<NativeForegroundOpenResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            var entered = false;
            nint process = 0;
            var initialized = false;
            try
            {
                var hr = Native.CoInitializeEx(0, 2);
                if (hr < 0) { completion.TrySetResult(new(false, hr, 0, 0)); return; }
                initialized = true;
                process = Native.OpenProcess(0x1000, false, target.ProcessId);
                if (process == 0 || Native.GetWindowThreadProcessId(target.Handle, out var pid) == 0 || pid != target.ProcessId ||
                    !Native.GetProcessTimes(process, out var start, out _, out _, out _) || start == 0 ||
                    !Native.GetExitCodeProcess(process, out var exit) || exit != 259)
                { completion.TrySetResult(new(false, unchecked((int)0x80070005), 0, 0)); return; }
                var output = new Output { Size = (uint)Marshal.SizeOf<Output>() };
                var nativeStartedAt = Stopwatch.GetTimestamp();
                entered = true;
                hr = invoke(target.Handle, target.ProcessId, start, folder, ref output);
                if (output.Size != Marshal.SizeOf<Output>()) completion.TrySetResult(new(true, unchecked((int)0x8000FFFF), 0, output.Stage));
                else completion.TrySetResult(new(output.Attempted != 0, hr < 0 ? hr : output.HResult,
                    unchecked((nint)output.CreatedTab), output.Stage,
                    (long)Stopwatch.GetElapsedTime(requestedAt, nativeStartedAt).TotalMilliseconds,
                    (long)Stopwatch.GetElapsedTime(nativeStartedAt).TotalMilliseconds));
            }
            catch (Exception exception) { completion.TrySetResult(new(entered, exception.HResult, 0, 0)); }
            finally
            {
                if (process != 0) Native.CloseHandle(process);
                if (initialized) Native.CoUninitialize();
            }
        }) { IsBackground = true, Name = "ExplorerOne native foreground folder" };
        try
        {
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }
        catch (Exception exception) { completion.TrySetResult(new(false, exception.HResult, 0, 0)); }
        return completion.Task;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Output { internal uint Size; internal int Attempted, HResult; internal uint Stage; internal ulong CreatedTab; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    private delegate int OpenFolder(nint target, uint pid, ulong start, [MarshalAs(UnmanagedType.LPWStr)] string folder, ref Output output);
    private static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint LoadLibraryExW(string path, nint file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] internal static extern nint GetProcAddress(nint module, string name);
        [DllImport("kernel32.dll")] internal static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(nint handle);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetProcessTimes(nint process, out ulong creation, out ulong exit, out ulong kernel, out ulong user);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetExitCodeProcess(nint process, out uint exit);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint pid);
        [DllImport("ole32.dll")] internal static extern int CoInitializeEx(nint reserved, uint flags);
        [DllImport("ole32.dll")] internal static extern void CoUninitialize();
    }
}
