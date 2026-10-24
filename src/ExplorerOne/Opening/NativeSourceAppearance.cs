using System.Runtime.InteropServices;
using ExplorerOne.Core;

namespace ExplorerOne.Opening;

internal enum NativeSourceAppearanceClaim { None, Claimed, Unavailable }

internal sealed record NativeSourceAppearanceOwner(uint ProcessId, ulong ProcessStart, ulong NonceLow, ulong NonceHigh);

internal readonly record struct NativeSourceAppearanceSnapshot(
    uint OwnerPid, ulong OwnerStart, ulong NonceLow, ulong NonceHigh,
    uint WindowPid, ulong WindowStart, ulong WindowCookie, ulong Deadline,
    uint Flags, uint OriginalStyle, LayeredWindowAttributes Attributes)
{
    internal TimeSpan RemainingAt(ulong now) => TimeSpan.FromMilliseconds(Deadline > now ? Math.Min(Deadline - now, 4000UL) : 0);
}

/// <summary>Claims the native pre-paint snapshot without recapturing its alpha zero.</summary>
internal sealed class NativeSourceAppearance
{
    internal const string Prefix = "ExplorerOne.NativeSource.V1.";
    private const uint Valid = 1, HadLayered = 2, HadAttributes = 4, Layered = 0x80000;
    private static NativeSourceAppearanceOwner? configuredOwner;
    private static volatile bool configuredEnabled;
    private readonly INativeSourceAppearanceProperties properties;
    private readonly Func<NativeSourceAppearanceOwner?> owner;
    private readonly Func<bool> enabled;

    internal NativeSourceAppearance(INativeSourceAppearanceProperties? properties = null,
        Func<NativeSourceAppearanceOwner?>? owner = null, Func<bool>? enabled = null)
    {
        this.properties = properties ?? new NativeSourceAppearanceProperties();
        this.owner = owner ?? (() => Volatile.Read(ref configuredOwner));
        this.enabled = enabled ?? (() => configuredEnabled);
    }

    // The router registers its trusted nonce before installing native hooks and
    // keeps it registered through menu Stop and appearance rollback at shutdown.
    internal static void ConfigureOwner(ulong nonceLow, ulong nonceHigh)
    {
        if (nonceLow == 0 || nonceHigh == 0 || !NativeSourceAppearanceProperties.TryProcessStart(Native.GetCurrentProcess(), out var start))
            throw new InvalidOperationException("Native appearance controller identity is unavailable.");
        Volatile.Write(ref configuredOwner, new(unchecked((uint)Environment.ProcessId), start, nonceLow, nonceHigh));
    }
    internal static void ClearOwner() => Volatile.Write(ref configuredOwner, null);
    internal static void ConfigureEnabled(bool enabled) => configuredEnabled = enabled;

    internal NativeSourceAppearanceClaim TryClaim(WindowId window, out NativeSourceAppearanceSnapshot snapshot)
    {
        snapshot = default;
        try
        {
            if (Get(window, "Flags") == 0)
                return enabled() ? NativeSourceAppearanceClaim.Unavailable : NativeSourceAppearanceClaim.None;
            var expected = owner();
            if (expected is null || !TryRead(window, out var candidate) || candidate.OwnerPid != expected.ProcessId ||
                candidate.OwnerStart != expected.ProcessStart || candidate.NonceLow != expected.NonceLow ||
                candidate.NonceHigh != expected.NonceHigh || !ControllerAndWindowCurrent(window, candidate) ||
                Get(window, "Claimed") != 0) return NativeSourceAppearanceClaim.Unavailable;
            if (!properties.Set(window, Prefix + "Claimed", 1) || !MatchesClaim(window, candidate, 1))
                return NativeSourceAppearanceClaim.Unavailable;
            snapshot = candidate;
            return NativeSourceAppearanceClaim.Claimed;
        }
        catch { return NativeSourceAppearanceClaim.Unavailable; }
    }

    internal bool Owns(WindowId window, NativeSourceAppearanceSnapshot snapshot)
    {
        try { return ControllerAndWindowCurrent(window, snapshot) && Matches(window, snapshot, requireClaim: true); }
        catch { return false; }
    }

    internal bool BeginRestore(WindowId window, NativeSourceAppearanceSnapshot snapshot)
    {
        try
        {
            if (!Owns(window, snapshot)) return false;
            // State 2 tells the native pre-show hook that subsequent style
            // changes are rollback, rather than Explorer's late initialization.
            if (Get(window, "Claimed") != 2 && !properties.Set(window, Prefix + "Claimed", 2)) return false;
            return ControllerAndWindowCurrent(window, snapshot) && MatchesClaim(window, snapshot, 2);
        }
        catch { return false; }
    }

    internal TimeSpan Remaining(NativeSourceAppearanceSnapshot snapshot) => snapshot.RemainingAt(properties.TickCount);

    internal bool Release(WindowId window, NativeSourceAppearanceSnapshot snapshot)
    {
        try
        {
            if (!Owns(window, snapshot)) return false;
            // Invalidate the snapshot before removing its data. The native
            // safeguard must never restore over a completed managed rollback.
            if (properties.Remove(window, Prefix + "Flags") != snapshot.Flags) return false;
            foreach (var name in new[] { "OriginalExStyle", "OriginalColorKey", "OriginalAlpha", "OriginalFlags",
                "Deadline", "WindowPid", "WindowStart", "NonceLow", "NonceHigh", "Claimed" })
            {
                if (!ReleaseIdentityMatches(window, snapshot)) return false;
                properties.Remove(window, Prefix + name);
            }
            if (!ReleaseIdentityMatches(window, snapshot)) return false;
            properties.Remove(window, Prefix + "OwnerStart");
            if (!ReleaseIdentityMatches(window, snapshot, startRemoved: true)) return false;
            properties.Remove(window, Prefix + "OwnerPid");
            if (!ReleaseIdentityMatches(window, snapshot, startRemoved: true, pidRemoved: true)) return false;
            properties.Remove(window, Prefix + "WindowCookie");
            return true;
        }
        catch { return false; }
    }

    private bool ReleaseIdentityMatches(WindowId window, NativeSourceAppearanceSnapshot snapshot, bool startRemoved = false, bool pidRemoved = false) =>
        properties.IsWindowCurrent(window, snapshot.WindowStart) && Get(window, "WindowCookie") == snapshot.WindowCookie &&
        Get(window, "OwnerPid") == (pidRemoved ? 0UL : snapshot.OwnerPid) && Get(window, "OwnerStart") == (startRemoved ? 0UL : snapshot.OwnerStart);

    private bool ControllerAndWindowCurrent(WindowId window, NativeSourceAppearanceSnapshot snapshot) =>
        snapshot.WindowPid == window.ProcessId && properties.IsControllerCurrent(snapshot.OwnerPid, snapshot.OwnerStart) &&
        properties.IsWindowCurrent(window, snapshot.WindowStart);

    private bool Matches(WindowId window, NativeSourceAppearanceSnapshot snapshot, bool requireClaim) =>
        TryRead(window, out var current) && current == snapshot && (!requireClaim || Get(window, "Claimed") is 1 or 2);

    private bool MatchesClaim(WindowId window, NativeSourceAppearanceSnapshot snapshot, ulong claim) =>
        Matches(window, snapshot, requireClaim: false) && Get(window, "Claimed") == claim;

    private bool TryRead(WindowId window, out NativeSourceAppearanceSnapshot snapshot)
    {
        snapshot = default;
        var flags = Get(window, "Flags");
        if (flags > uint.MaxValue || (flags & Valid) == 0 || (flags & ~(Valid | HadLayered | HadAttributes)) != 0 ||
            ((flags & HadAttributes) != 0 && (flags & HadLayered) == 0)) return false;
        var ownerPid = Get(window, "OwnerPid");
        var ownerStart = Get(window, "OwnerStart");
        var nonceLow = Get(window, "NonceLow");
        var nonceHigh = Get(window, "NonceHigh");
        var windowPid = Get(window, "WindowPid");
        var windowStart = Get(window, "WindowStart");
        var cookie = Get(window, "WindowCookie");
        var deadline = Get(window, "Deadline");
        var style = Get(window, "OriginalExStyle");
        var color = Get(window, "OriginalColorKey");
        var alpha = Get(window, "OriginalAlpha");
        var attributes = Get(window, "OriginalFlags");
        if (ownerPid is 0 or > uint.MaxValue || ownerStart == 0 || nonceLow == 0 || nonceHigh == 0 ||
            windowPid is 0 or > uint.MaxValue || windowStart == 0 || cookie == 0 || deadline == 0 ||
            style > uint.MaxValue || color > uint.MaxValue || alpha > byte.MaxValue || attributes > uint.MaxValue ||
            ((flags & HadLayered) != 0) != ((style & Layered) != 0) ||
            ((flags & HadLayered) != 0 && ((flags & HadAttributes) == 0 || attributes is 0 or > 3))) return false;
        snapshot = new((uint)ownerPid, ownerStart, nonceLow, nonceHigh, (uint)windowPid, windowStart, cookie, deadline,
            (uint)flags, (uint)style, (flags & HadAttributes) != 0 ? new((uint)color, (byte)alpha, (uint)attributes) : new(0, 255, 2));
        return Get(window, "Flags") == flags && Get(window, "OwnerPid") == ownerPid && Get(window, "OwnerStart") == ownerStart &&
            Get(window, "NonceLow") == nonceLow && Get(window, "NonceHigh") == nonceHigh && Get(window, "WindowCookie") == cookie;
    }
    private ulong Get(WindowId window, string name) => properties.Get(window, Prefix + name);

    private static class Native
    {
        [DllImport("kernel32.dll")] internal static extern nint GetCurrentProcess();
    }
}

internal interface INativeSourceAppearanceProperties
{
    ulong TickCount { get; }
    ulong Get(WindowId window, string name);
    bool Set(WindowId window, string name, ulong value);
    ulong Remove(WindowId window, string name);
    bool IsControllerCurrent(uint process, ulong start);
    bool IsWindowCurrent(WindowId window, ulong start);
}

internal sealed class NativeSourceAppearanceProperties : INativeSourceAppearanceProperties
{
    public ulong TickCount => GetTickCount64();
    public ulong Get(WindowId window, string name) => unchecked((ulong)GetPropW(window.Handle, name).ToInt64());
    public bool Set(WindowId window, string name, ulong value) => SetPropW(window.Handle, name, unchecked((nint)value));
    public ulong Remove(WindowId window, string name) => unchecked((ulong)RemovePropW(window.Handle, name).ToInt64());
    public bool IsControllerCurrent(uint process, ulong start) => process == unchecked((uint)Environment.ProcessId) &&
        TryProcessStart(GetCurrentProcess(), out var actual) && actual == start;
    public bool IsWindowCurrent(WindowId window, ulong start)
    {
        if (window.Handle == 0 || window.ProcessId == 0 || !IsWindow(window.Handle) || GetAncestor(window.Handle, 2) != window.Handle ||
            GetWindowThreadProcessId(window.Handle, out var pid) == 0 || pid != window.ProcessId) return false;
        var name = new char[64];
        if (GetClassNameW(window.Handle, name, name.Length) != 13 || new string(name, 0, 13) != "CabinetWClass") return false;
        if (!ProcessIdToSessionId(pid, out var session) || !ProcessIdToSessionId(unchecked((uint)Environment.ProcessId), out var ownSession) || session != ownSession) return false;
        var process = OpenProcess(0x1000 | 0x100000, false, pid);
        if (process == 0) return false;
        try
        {
            if (!TryProcessStart(process, out var actual) || actual != start || WaitForSingleObject(process, 0) != 258) return false;
            var image = new char[32768]; uint length = (uint)image.Length;
            if (!QueryFullProcessImageNameW(process, 0, image, ref length) || !string.Equals(new string(image, 0, (int)length),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), StringComparison.OrdinalIgnoreCase)) return false;
            return SameUser(process);
        }
        finally { CloseHandle(process); }
    }
    private static bool SameUser(nint process)
    {
        nint token = 0, ownToken = 0, data = 0, ownData = 0;
        try
        {
            if (!OpenProcessToken(process, 8, out token) || !OpenProcessToken(GetCurrentProcess(), 8, out ownToken)) return false;
            _ = GetTokenInformation(token, 1, 0, 0, out var size);
            _ = GetTokenInformation(ownToken, 1, 0, 0, out var ownSize);
            if (size is 0 or > 65536 || ownSize is 0 or > 65536) return false;
            data = Marshal.AllocHGlobal((int)size); ownData = Marshal.AllocHGlobal((int)ownSize);
            return GetTokenInformation(token, 1, data, size, out _) && GetTokenInformation(ownToken, 1, ownData, ownSize, out _) &&
                EqualSid(Marshal.ReadIntPtr(data), Marshal.ReadIntPtr(ownData));
        }
        finally
        {
            if (data != 0) Marshal.FreeHGlobal(data);
            if (ownData != 0) Marshal.FreeHGlobal(ownData);
            if (token != 0) CloseHandle(token);
            if (ownToken != 0) CloseHandle(ownToken);
        }
    }
    internal static bool TryProcessStart(nint process, out ulong start) => GetProcessTimes(process, out start, out _, out _, out _);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint GetPropW(nint window, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetPropW(nint window, string name, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint RemovePropW(nint window, string name);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint window, [Out] char[] name, int size);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern ulong GetTickCount64();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint process);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(nint process, out ulong created, out ulong exited, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ProcessIdToSessionId(uint process, out uint session);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageNameW(nint process, uint flags, [Out] char[] image, ref uint length);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(nint token, int informationClass, nint information, uint length, out uint required);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EqualSid(nint left, nint right);
}
