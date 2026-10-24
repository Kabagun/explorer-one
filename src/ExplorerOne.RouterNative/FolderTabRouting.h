#pragma once

#include <windows.h>
#include <shlobj.h>

namespace explorerone {

struct RoutingTarget final {
    HWND root;
    DWORD pid;
    ULONGLONG processStart;
};

struct RouteResult final {
    // Once attempted is true the caller must never launch a fallback window,
    // including when result is a failure: native tab creation may have committed.
    bool attempted;
    HRESULT result;
};

RouteResult TryRoutePidlToTab(const RoutingTarget& target,
    PCIDLIST_ABSOLUTE folder, IUnknown* handshake, IUnknown* site) noexcept;

// Last completed helper call's diagnostic stage; contains no window/path data.
UINT LastRoutingStage() noexcept;
UINT LastRoutingSiteProbe() noexcept;

} // namespace explorerone
