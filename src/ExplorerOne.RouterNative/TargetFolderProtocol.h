#pragma once
#include <windows.h>
#include <cstddef>
#include <cstdint>

inline constexpr std::uint32_t kTargetFolderMagic = 0x31544645;
inline constexpr std::uint32_t kTargetFolderVersion = 1;
inline constexpr std::size_t kTargetFolderPidlCapacity = 65536;
inline constexpr std::size_t kTargetFolderTabCapacity = 512;
inline constexpr wchar_t kTargetFolderMappingPrefix[] = L"Local\\ExplorerOne.TargetFolder.V1.";
inline constexpr wchar_t kTargetFolderMessagePrefix[] = L"ExplorerOne.TargetFolder.V1.Command.";
enum : LONG { kTargetFolderPending = 0, kTargetFolderRunning = 1, kTargetFolderComplete = 2, kTargetFolderCancelled = 3 };

#pragma pack(push, 8)
struct TargetFolderRequest final {
    std::uint32_t magic, size, version, pidlBytes;
    volatile LONG state;
    std::uint32_t controllerPid, controllerSession, targetPid, targetThread, beforeCount;
    std::uint64_t controllerStart, targetStart, targetRoot, cookie, nonceLow, nonceHigh;
    wchar_t controllerSid[184];
    volatile LONG attempted;
    HRESULT result;
    std::uint64_t createdTab;
    std::uint32_t modulePinned, stage;
    std::uint64_t beforeTabs[kTargetFolderTabCapacity];
    BYTE pidl[kTargetFolderPidlCapacity];
};
#pragma pack(pop)
static_assert(sizeof(void*) == 8);
static_assert(offsetof(TargetFolderRequest, state) == 16);
static_assert(offsetof(TargetFolderRequest, attempted) == 456);
static_assert(offsetof(TargetFolderRequest, pidl) == 4576);
static_assert(sizeof(TargetFolderRequest) == 70112);

extern "C" __declspec(dllexport) LRESULT CALLBACK TargetFolderGetMessageProc(int code, WPARAM first, LPARAM parameter);
