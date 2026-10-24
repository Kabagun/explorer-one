#pragma once
#include "NativeLaunchProtocol.h"

namespace explorerone {
HRESULT StartSourcePresentation(const NativeLaunchRequest& request) noexcept;
HRESULT UpdateSourcePresentation(const NativeLaunchRequest& request) noexcept;
void StopSourcePresentation() noexcept;
bool SourcePresentationEnabled() noexcept;
HRESULT SourcePresentationLastResult() noexcept;
}

// Shared primitive HWND properties; Flags is the snapshot commit marker.
// No property contains a process-local pointer. Managed consumers must validate
// controller PID/start/nonces and source PID/start/window cookie before use.
inline constexpr wchar_t kNativeSourcePrefix[] = L"ExplorerOne.NativeSource.V1.";
inline constexpr DWORD kNativeSourceValid = 1, kNativeSourceWasLayered = 2, kNativeSourceHadAttributes = 4;
// Claiming adopts the original snapshot; native pre-show repair remains allowed.
// Managed rollback changes Claimed to Restoring before any appearance write.
inline constexpr ULONG_PTR kNativeSourceUnclaimed = 0, kNativeSourceClaimed = 1, kNativeSourceRestoring = 2;
extern "C" __declspec(dllexport) void CALLBACK NativeSourcePresentationEventProc(
    HWINEVENTHOOK hook, DWORD event, HWND window, LONG object, LONG child, DWORD thread, DWORD milliseconds);
// Runs synchronously before Explorer's creation/show window messages. Every
// callback first rejects executables other than the system Explorer image.
extern "C" __declspec(dllexport) LRESULT CALLBACK NativeSourcePresentationCallWndProc(
    int code, WPARAM parameter, LPARAM message);
