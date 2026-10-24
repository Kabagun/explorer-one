#pragma once
#include <windows.h>
#include <cstdint>

// Windows x64 only. Keep the managed Pack=8 layout in sync with this structure.
inline constexpr std::uint32_t kRouterMagic = 0x31524F45;
inline constexpr std::uint32_t kRouterVersion = 1;
inline constexpr wchar_t kRouterMappingPrefix[] = L"Local\\ExplorerOne.NativeLaunchRouter.V1.";
inline constexpr wchar_t kRouterMessageName[] = L"ExplorerOne.NativeLaunchRouter.V1.Command";
inline constexpr wchar_t kRouterWindowClass[] = L"ExplorerOne.NativeLaunchRouter.V1.Agent";
enum : LONG { kRouterPending = 0, kRouterRunning = 1, kRouterComplete = 2, kRouterCancelled = 3 };
enum : std::uint32_t { kRouterStart = 1, kRouterUpdate = 2, kRouterStop = 3, kRouterStatus = 4 };

#pragma pack(push, 8)
struct NativeLaunchRequest final {
    std::uint32_t magic, size, version, action;
    volatile LONG state;
    // reserved must be zero on input; completion writes the native source
    // presentation manager's HRESULT bits here (offset 40) for diagnostics.
    std::uint32_t controllerPid, controllerSession, shellPid, shellThread, targetPid, reserved;
    std::uint64_t controllerStart, shellStart, targetRoot, targetStart, nonceLow, nonceHigh;
    wchar_t controllerSid[184];

    HRESULT result, moduleValidationResult;
    std::uint64_t agentWindow, totalCalls, routedCalls, forwardedCalls, failedRoutes, recursiveCalls;
    std::uint32_t hookEnabled, hookRemoved, cleanupDeferred, activeCallbacks, unsupported, foreignPatch, modulePinned;
    std::int32_t mhInitialize, mhCreate, mhEnable, mhDisable, mhRemove, mhUninitialize;
    HRESULT lastRouteResult, lastOriginalResult;
    std::uint32_t lastFlags, stage;
};
#pragma pack(pop)
static_assert(sizeof(void*) == 8, "The native router supports Windows x64 only");
static_assert(sizeof(NativeLaunchRequest) == 592, "Controller/native protocol layout mismatch");

// Register the command name as kRouterMessageName + L"." + DLL basename, so
// a lingering bootstrap for another source-hashed DLL cannot claim a request.
// Bootstrap: SetWindowsHookEx(WH_GETMESSAGE, this export, module, shellThread),
// then PostMessage(GetShellWindow(), registered command, nonceLow, mapping atom).
// After an authenticated start, agentWindow accepts the same command directly.
// Use one GUID32-suffixed mapping per command and retain it until Complete or a
// successful Pending->Cancelled CAS. A running command owns no data after ack.
// Native pins itself before creating any persistent window/hook. Stop may return
// S_FALSE while a timer drains callbacks; Status exposes completion and counters.
extern "C" __declspec(dllexport) LRESULT CALLBACK NativeLaunchRouterGetMessageProc(int code, WPARAM first, LPARAM parameter);
extern "C" __declspec(dllexport) LONG WINAPI NativeLaunchRouterCancelPending(void* mappedRequest);
