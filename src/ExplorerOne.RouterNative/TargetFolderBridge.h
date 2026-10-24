#pragma once
#include "FolderTabRouting.h"

namespace explorerone {
struct NativeTabOpenResult final {
    bool attempted;
    HRESULT result;
    HWND createdTab;
};

// Runs the exact-profile AddTab/SelectTab pair on the target frame's owner STA.
// Once attempted is true, callers must never launch a duplicate fallback.
NativeTabOpenResult OpenNativeForegroundTab(const RoutingTarget& target,
    PCIDLIST_ABSOLUTE folder) noexcept;
} // namespace explorerone

struct NativeForegroundResult final {
    DWORD size;
    LONG attempted;
    HRESULT result;
    UINT stage;
    ULONGLONG createdTab;
};
static_assert(sizeof(NativeForegroundResult) == 24);
extern "C" __declspec(dllexport) HRESULT WINAPI NativeOpenForegroundFolder(HWND target, DWORD pid,
    ULONGLONG targetStart, PCWSTR folder, NativeForegroundResult* output) noexcept;
