#pragma once
#include "FolderTabRouting.h"

namespace explorerone {
using RouteQueueCompletion = void (*)(const RouteResult&) noexcept;

// Called on the desktop shell thread before interception is enabled. All COM
// browser work runs on this dedicated STA, outside the ingress input-sync call.
HRESULT StartNativeRouteQueue(DWORD shellThread, RouteQueueCompletion completion) noexcept;
RouteResult RoutePidlOnSta(const RoutingTarget& target, PCIDLIST_ABSOLUTE folder,
    IUnknown* handshake, IUnknown* site, HANDLE controllerProcess) noexcept;
// Reject new work, drain accepted jobs, and finish STA teardown without blocking
// the shell thread. Poll until true before removing the native interception.
bool StopNativeRouteQueue() noexcept;
DWORD NativeRouteQueuePending() noexcept;
bool IsNativeRouteQueueThread() noexcept;
} // namespace explorerone
