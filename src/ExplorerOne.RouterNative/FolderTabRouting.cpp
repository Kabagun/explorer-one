#include "FolderTabRouting.h"
#include "TargetFolderBridge.h"

#include <bcrypt.h>
#include <exdisp.h>
#include <roapi.h>
#include <servprov.h>
#include <shlguid.h>
#include <shobjidl.h>
#include <tlhelp32.h>
#include <wrl/client.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cwchar>
#include <new>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace explorerone {
namespace {

constexpr size_t MaximumTabs = 512;
constexpr DWORD NativeCreationTimeout = 2000;
constexpr DWORD HandshakeTimeout = 6000;
constexpr DWORD KnownTimestamp = 0xFD64D9D7;
constexpr DWORD KnownImageSize = 0x00323000;
constexpr DWORD BrowseRva = 0x133BA0;
constexpr LONGLONG KnownFileSize = 3268608;
constexpr std::array<BYTE, 32> KnownHash = {
    0xb7, 0xbd, 0xa6, 0x01, 0x1c, 0x0c, 0x13, 0xa8,
    0x1a, 0x6e, 0xa9, 0x67, 0x07, 0x89, 0xc3, 0xec,
    0xb2, 0x41, 0xca, 0xf9, 0x94, 0xd3, 0x1e, 0x27,
    0x4a, 0xdd, 0x18, 0x70, 0x49, 0x29, 0xca, 0x6d
};

// These are the native interfaces used by Explorer's browser creation path.
// V2 must be queried independently; a base interface has only slots 0..7.
MIDL_INTERFACE("6aca2690-0de1-46c9-a1c5-9338a22bf12b")
IBrowserThreadHandshake : public IUnknown {
    virtual HRESULT STDMETHODCALLTYPE GetEventName(PWSTR*) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetStream(IStream**) = 0;
    virtual HRESULT STDMETHODCALLTYPE PutResult(HRESULT) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetResult() = 0;
    virtual HRESULT STDMETHODCALLTYPE SetInternalEvent() = 0;
};
MIDL_INTERFACE("f9b388a9-dd0f-480e-8c1c-6398324d8c2e")
IBrowserThreadHandshake2 : public IBrowserThreadHandshake {
    virtual HRESULT STDMETHODCALLTYPE GetAgileBrowserReference(IAgileReference**) = 0;
    virtual HRESULT STDMETHODCALLTYPE PutAgileBrowserReference(IAgileReference*) = 0;
};

struct Handle final {
    HANDLE value = nullptr;
    Handle() = default;
    explicit Handle(HANDLE handle) : value(handle) {}
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    explicit operator bool() const noexcept { return value && value != INVALID_HANDLE_VALUE; }
};
struct Pidl final {
    PIDLIST_ABSOLUTE value = nullptr;
    ~Pidl() { CoTaskMemFree(value); }
};

// Callers can enter from RPC or Explorer's UI apartment. Never block that
// apartment with an ordinary mutex while another routing call needs its COM.
struct RouteGate final {
    std::atomic<bool> busy{ false };
    Handle changed{ CreateEventW(nullptr, FALSE, FALSE, nullptr) };
};
RouteGate& Gate() {
    static RouteGate gate;
    return gate;
}
thread_local bool InRoute = false;
std::atomic<UINT> LastStage{ 0 };
std::atomic<UINT> LastSiteProbe{ 0 };
thread_local UINT CurrentStage = 0;
struct StageScope final {
    UINT previous;
    StageScope() noexcept : previous(CurrentStage) { CurrentStage = 100; }
    ~StageScope() {
        LastStage.store(CurrentStage, std::memory_order_release);
        CurrentStage = previous;
    }
};

HRESULT PumpWait(HANDLE event, DWORD milliseconds) noexcept {
    DWORD index = 0;
    return CoWaitForMultipleHandles(COWAIT_DISPATCH_CALLS | COWAIT_DISPATCH_WINDOW_MESSAGES,
        milliseconds, 1, &event, &index);
}

struct RoutingLease final {
    bool acquired = false;
    ~RoutingLease() {
        if (acquired) {
            InRoute = false;
            Gate().busy.store(false, std::memory_order_release);
            SetEvent(Gate().changed.value);
        }
    }
    bool Acquire() noexcept {
        if (InRoute || !Gate().changed) return false;
        // Mark before pumping: a nested call on this same thread must not wait
        // on the lease the interrupted outer call is trying to obtain.
        InRoute = true;
        const ULONGLONG started = GetTickCount64();
        do {
            bool idle = false;
            if (Gate().busy.compare_exchange_strong(idle, true, std::memory_order_acquire)) {
                acquired = true;
                return true;
            }
            const HRESULT waited = PumpWait(Gate().changed.value, 20);
            if (FAILED(waited) && waited != RPC_S_CALLPENDING) break;
        } while (GetTickCount64() - started < HandshakeTimeout);
        InRoute = false;
        return false;
    }
};

ULONGLONG ProcessStart(HANDLE process) noexcept {
    FILETIME creation{}, exit{}, kernel{}, user{};
    if (!GetProcessTimes(process, &creation, &exit, &kernel, &user)) return 0;
    ULARGE_INTEGER value{};
    value.LowPart = creation.dwLowDateTime;
    value.HighPart = creation.dwHighDateTime;
    return value.QuadPart;
}

bool ProcessCurrent(HANDLE process, const RoutingTarget& target) noexcept {
    DWORD exit = 0;
    return target.processStart && ProcessStart(process) == target.processStart &&
        GetExitCodeProcess(process, &exit) && exit == STILL_ACTIVE;
}

bool WindowIs(HWND window, const RoutingTarget& target, DWORD thread,
    const wchar_t* expectedClass) noexcept {
    DWORD pid = 0;
    wchar_t className[64]{};
    return window && IsWindow(window) && GetAncestor(window, GA_ROOT) == target.root &&
        GetWindowThreadProcessId(window, &pid) == thread && pid == target.pid &&
        GetClassNameW(window, className, _countof(className)) &&
        wcscmp(className, expectedClass) == 0;
}

bool TargetCurrent(const RoutingTarget& target, DWORD thread, HANDLE process) noexcept {
    return WindowIs(target.root, target, thread, L"CabinetWClass") &&
        ProcessCurrent(process, target);
}

bool ReadUser(HANDLE process, std::vector<BYTE>& bytes) {
    Handle token;
    if (!OpenProcessToken(process, TOKEN_QUERY, &token.value)) return false;
    DWORD needed = 0;
    GetTokenInformation(token.value, TokenUser, nullptr, 0, &needed);
    if (!needed || needed > 65536) return false;
    bytes.resize(needed);
    return GetTokenInformation(token.value, TokenUser, bytes.data(), needed, &needed) != FALSE;
}

bool SameUserAndSession(const RoutingTarget& target, HANDLE process) {
    DWORD ownSession = 0, targetSession = 0;
    if (!ProcessIdToSessionId(GetCurrentProcessId(), &ownSession) ||
        !ProcessIdToSessionId(target.pid, &targetSession) || ownSession != targetSession) return false;
    std::vector<BYTE> ownUser, targetUser;
    if (!ReadUser(GetCurrentProcess(), ownUser) || !ReadUser(process, targetUser)) return false;
    const auto own = reinterpret_cast<const TOKEN_USER*>(ownUser.data());
    const auto other = reinterpret_cast<const TOKEN_USER*>(targetUser.data());
    return IsValidSid(own->User.Sid) && IsValidSid(other->User.Sid) &&
        EqualSid(own->User.Sid, other->User.Sid);
}

bool ExplorerImage(HANDLE process) {
    wchar_t image[32768]{}, windows[32768]{};
    DWORD length = _countof(image);
    const UINT windowsLength = GetWindowsDirectoryW(windows, _countof(windows));
    if (!windowsLength || windowsLength >= _countof(windows) ||
        !QueryFullProcessImageNameW(process, 0, image, &length) || !length) return false;
    const std::wstring expected = std::wstring(windows, windowsLength) + L"\\explorer.exe";
    return _wcsicmp(image, expected.c_str()) == 0;
}

bool ReadAt(HANDLE file, LONGLONG offset, void* buffer, DWORD size) noexcept {
    LARGE_INTEGER position{};
    position.QuadPart = offset;
    DWORD read = 0;
    return SetFilePointerEx(file, position, nullptr, FILE_BEGIN) &&
        ReadFile(file, buffer, size, &read, nullptr) && read == size;
}

bool FileHashMatches(HANDLE file) {
    struct Algorithm final {
        BCRYPT_ALG_HANDLE value = nullptr;
        ~Algorithm() { if (value) BCryptCloseAlgorithmProvider(value, 0); }
    } algorithm;
    std::vector<BYTE> object;
    struct Hash final {
        BCRYPT_HASH_HANDLE value = nullptr;
        ~Hash() { if (value) BCryptDestroyHash(value); }
    } hash;
    if (BCryptOpenAlgorithmProvider(&algorithm.value, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0) return false;
    DWORD objectLength = 0, returned = 0;
    if (BCryptGetProperty(algorithm.value, BCRYPT_OBJECT_LENGTH,
        reinterpret_cast<PUCHAR>(&objectLength), sizeof(objectLength), &returned, 0) < 0 ||
        !objectLength || objectLength > 65536) return false;
    object.resize(objectLength);
    if (BCryptCreateHash(algorithm.value, &hash.value, object.data(), objectLength, nullptr, 0, 0) < 0) return false;
    LARGE_INTEGER zero{};
    if (!SetFilePointerEx(file, zero, nullptr, FILE_BEGIN)) return false;
    std::array<BYTE, 65536> buffer{};
    for (;;) {
        DWORD read = 0;
        if (!ReadFile(file, buffer.data(), static_cast<DWORD>(buffer.size()), &read, nullptr)) return false;
        if (!read) break;
        if (BCryptHashData(hash.value, buffer.data(), read, 0) < 0) return false;
    }
    std::array<BYTE, 32> actual{};
    return BCryptFinishHash(hash.value, actual.data(), static_cast<ULONG>(actual.size()), 0) >= 0 &&
        actual == KnownHash;
}

bool ReadRemote(HANDLE process, const BYTE* address, void* bytes, SIZE_T size) noexcept {
    SIZE_T read = 0;
    return ReadProcessMemory(process, address, bytes, size, &read) && read == size;
}

struct VerifiedModule final {
    DWORD pid = 0;
    ULONGLONG processStart = 0;
    BYTE* base = nullptr;
    std::array<BYTE, 32> code{};
    Handle file;
};

bool VerifiedMemoryCurrent(HANDLE process, const VerifiedModule& module) noexcept {
    MEMORY_BASIC_INFORMATION memory{};
    IMAGE_DOS_HEADER dos{};
    IMAGE_NT_HEADERS64 header{};
    std::array<BYTE, 32> code{};
    return VirtualQueryEx(process, module.base, &memory, sizeof(memory)) == sizeof(memory) &&
        memory.Type == MEM_IMAGE && memory.AllocationBase == module.base &&
        ReadRemote(process, module.base, &dos, sizeof(dos)) && dos.e_magic == IMAGE_DOS_SIGNATURE &&
        dos.e_lfanew >= 64 && dos.e_lfanew <= 4096 &&
        ReadRemote(process, module.base + dos.e_lfanew, &header, sizeof(header)) &&
        header.Signature == IMAGE_NT_SIGNATURE && header.FileHeader.Machine == IMAGE_FILE_MACHINE_AMD64 &&
        header.FileHeader.TimeDateStamp == KnownTimestamp && header.OptionalHeader.Magic == IMAGE_NT_OPTIONAL_HDR64_MAGIC &&
        header.OptionalHeader.SizeOfImage == KnownImageSize &&
        ReadRemote(process, module.base + BrowseRva, code.data(), code.size()) && code == module.code;
}

bool VerifyBrowserCode(const RoutingTarget& target, HANDLE process) {
    // The route lease serializes this one-entry cache. PID reuse is excluded by
    // the process creation time. Keep its verified file locked, and check the
    // actual mapped image and method prefix again on every request.
    static VerifiedModule verified;
    if (verified.pid == target.pid && verified.processStart == target.processStart && verified.file)
        return VerifiedMemoryCurrent(process, verified);
    if (verified.file) CloseHandle(verified.file.value);
    verified.file.value = nullptr;
    verified.pid = 0;
    verified.processStart = 0;
    verified.base = nullptr;
    Handle moduleFile;
    Handle modules(CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, target.pid));
    if (!modules) return false;
    MODULEENTRY32W row{};
    row.dwSize = sizeof(row);
    BYTE* base = nullptr;
    wchar_t modulePath[MAX_PATH]{};
    if (!Module32FirstW(modules.value, &row)) return false;
    do {
        if (_wcsicmp(row.szModule, L"ExplorerFrame.dll") != 0) continue;
        if (base || row.modBaseSize != KnownImageSize || !row.modBaseAddr) return false;
        base = row.modBaseAddr;
        wcscpy_s(modulePath, row.szExePath);
    } while (Module32NextW(modules.value, &row));
    if (!base) return false;
    wchar_t system[MAX_PATH]{};
    const UINT length = GetSystemDirectoryW(system, _countof(system));
    if (!length || length >= _countof(system)) return false;
    const std::wstring expected = std::wstring(system, length) + L"\\ExplorerFrame.dll";
    if (_wcsicmp(modulePath, expected.c_str()) != 0) return false;
    // Keep the exact verified disk image open without write/delete sharing until
    // dispatch has returned, as in the guarded managed browser implementation.
    moduleFile.value = CreateFileW(modulePath, GENERIC_READ, FILE_SHARE_READ,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    LARGE_INTEGER fileSize{};
    if (!moduleFile || !GetFileSizeEx(moduleFile.value, &fileSize) ||
        fileSize.QuadPart != KnownFileSize || !FileHashMatches(moduleFile.value)) return false;
    IMAGE_DOS_HEADER remoteDos{}, diskDos{};
    if (!ReadRemote(process, base, &remoteDos, sizeof(remoteDos)) ||
        remoteDos.e_magic != IMAGE_DOS_SIGNATURE || remoteDos.e_lfanew < 64 || remoteDos.e_lfanew > 4096 ||
        !ReadAt(moduleFile.value, 0, &diskDos, sizeof(diskDos)) ||
        diskDos.e_lfanew != remoteDos.e_lfanew) return false;
    IMAGE_NT_HEADERS64 remote{}, disk{};
    if (!ReadRemote(process, base + remoteDos.e_lfanew, &remote, sizeof(remote)) ||
        !ReadAt(moduleFile.value, diskDos.e_lfanew, &disk, sizeof(disk)) ||
        remote.Signature != IMAGE_NT_SIGNATURE || remote.FileHeader.Machine != IMAGE_FILE_MACHINE_AMD64 ||
        remote.FileHeader.TimeDateStamp != KnownTimestamp || remote.OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC ||
        remote.OptionalHeader.SizeOfImage != KnownImageSize ||
        disk.FileHeader.NumberOfSections == 0 || disk.FileHeader.NumberOfSections > 128 ||
        disk.FileHeader.SizeOfOptionalHeader < sizeof(IMAGE_OPTIONAL_HEADER64)) return false;
    LONGLONG methodOffset = -1;
    const LONGLONG sections = diskDos.e_lfanew + sizeof(DWORD) + sizeof(IMAGE_FILE_HEADER) +
        disk.FileHeader.SizeOfOptionalHeader;
    for (WORD index = 0; index < disk.FileHeader.NumberOfSections; ++index) {
        IMAGE_SECTION_HEADER section{};
        if (!ReadAt(moduleFile.value, sections + static_cast<LONGLONG>(index) * sizeof(section),
            &section, sizeof(section))) return false;
        if (BrowseRva >= section.VirtualAddress && BrowseRva - section.VirtualAddress <= section.SizeOfRawData &&
            section.SizeOfRawData - (BrowseRva - section.VirtualAddress) >= 32)
            methodOffset = static_cast<LONGLONG>(section.PointerToRawData) + BrowseRva - section.VirtualAddress;
    }
    std::array<BYTE, 32> expectedCode{}, actualCode{};
    if (!(methodOffset >= 0 && methodOffset + static_cast<LONGLONG>(expectedCode.size()) <= KnownFileSize &&
        ReadAt(moduleFile.value, methodOffset, expectedCode.data(), static_cast<DWORD>(expectedCode.size())) &&
        ReadRemote(process, base + BrowseRva, actualCode.data(), actualCode.size()) && expectedCode == actualCode)) return false;
    verified.pid = target.pid;
    verified.processStart = target.processStart;
    verified.base = base;
    verified.code = expectedCode;
    verified.file.value = moduleFile.value;
    moduleFile.value = nullptr;
    return true;
}

struct TabSnapshot final {
    const RoutingTarget& target;
    DWORD thread;
    std::vector<HWND> tabs;
    bool valid = true;
};

BOOL CALLBACK CollectTab(HWND window, LPARAM parameter) noexcept {
    auto& snapshot = *reinterpret_cast<TabSnapshot*>(parameter);
    wchar_t type[64]{};
    if (!GetClassNameW(window, type, _countof(type)) || wcscmp(type, L"ShellTabWindowClass") != 0) return TRUE;
    if (!WindowIs(window, snapshot.target, snapshot.thread, L"ShellTabWindowClass") ||
        snapshot.tabs.size() >= MaximumTabs) { snapshot.valid = false; return FALSE; }
    // Reserve before enumeration so exceptions cannot cross the Win32 callback.
    snapshot.tabs.push_back(window);
    return TRUE;
}

bool Snapshot(const RoutingTarget& target, DWORD thread, HANDLE process, std::vector<HWND>& tabs) {
    if (!TargetCurrent(target, thread, process)) return false;
    TabSnapshot snapshot{ target, thread, {}, true };
    snapshot.tabs.reserve(MaximumTabs);
    EnumChildWindows(target.root, CollectTab, reinterpret_cast<LPARAM>(&snapshot));
    if (!snapshot.valid || !TargetCurrent(target, thread, process)) return false;
    std::sort(snapshot.tabs.begin(), snapshot.tabs.end(), std::less<HWND>{});
    if (std::adjacent_find(snapshot.tabs.begin(), snapshot.tabs.end()) != snapshot.tabs.end()) return false;
    tabs = std::move(snapshot.tabs);
    return true;
}

bool AllowedFolder(PCIDLIST_ABSOLUTE folder) {
    if (!folder || ILIsEmpty(folder)) return false;
    ComPtr<IShellItem> item;
    SFGAOF attributes = 0;
    if (FAILED(SHCreateItemFromIDList(folder, IID_PPV_ARGS(&item))) ||
        FAILED(item->GetAttributes(SFGAO_FOLDER | SFGAO_FILESYSTEM, &attributes)) ||
        !(attributes & SFGAO_FOLDER)) return false;
    if (attributes & SFGAO_FILESYSTEM) return true;
    // Only these exact namespace roots participate. Control Panel and its
    // descendants, including unfamiliar namespace targets, retain native launch.
    constexpr const wchar_t* roots[] = {
        L"::{679F85CB-0220-4080-B29B-5540CC05AAB6}", // Home / Quick access
        L"::{F874310E-B6B7-47DC-BC84-B9E6B38F5903}", // Home
        L"::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", // This PC
        L"::{645FF040-5081-101B-9F08-00AA002F954E}"  // Recycle Bin
    };
    for (const wchar_t* root : roots) {
        Pidl allowed;
        if (SUCCEEDED(SHParseDisplayName(root, nullptr, &allowed.value, 0, nullptr)) &&
            allowed.value && ILIsEqual(folder, allowed.value)) return true;
    }
    return false;
}

struct BrowserBinding final {
    ComPtr<IWebBrowser2> automation;
    ComPtr<IShellBrowser> browser;
    HWND tab = nullptr;
};

struct BindingFailure final {
    UINT stage = 200;
    HRESULT result = HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    bool Record(UINT failedStage, HRESULT failedResult) noexcept {
        if (failedStage >= stage) {
            stage = failedStage;
            result = FAILED(failedResult) ? failedResult : E_UNEXPECTED;
        }
        return false;
    }
};

bool BindRow(IShellWindows* windows, long index, const RoutingTarget& target,
    DWORD thread, BrowserBinding& binding, BindingFailure& failure) noexcept {
    VARIANT item{};
    item.vt = VT_I4;
    item.lVal = index;
    ComPtr<IDispatch> dispatch;
    ComPtr<IWebBrowser2> automation;
    ComPtr<IServiceProvider> services;
    ComPtr<IShellBrowser> browser;
    SHANDLE_PTR raw = 0;
    HWND tab = nullptr;
    DWORD pid = 0;
    HRESULT result = windows->Item(item, &dispatch);
    if (FAILED(result) || !dispatch) return failure.Record(202, result);
    result = dispatch.As(&automation);
    if (FAILED(result)) return failure.Record(203, result);
    result = automation->get_HWND(&raw);
    if (FAILED(result)) return failure.Record(204, result);
    const HWND window = reinterpret_cast<HWND>(raw);
    if (!window || GetAncestor(window, GA_ROOT) != target.root)
        return failure.Record(205, HRESULT_FROM_WIN32(ERROR_NOT_FOUND));
    if (GetWindowThreadProcessId(window, &pid) != thread || pid != target.pid)
        return failure.Record(206, HRESULT_FROM_WIN32(ERROR_INVALID_WINDOW_HANDLE));
    result = dispatch.As(&services);
    if (FAILED(result)) return failure.Record(207, result);
    result = services->QueryService(__uuidof(IShellBrowser), IID_PPV_ARGS(&browser));
    if (FAILED(result)) {
        browser.Reset();
        result = services->QueryService(SID_STopLevelBrowser, IID_PPV_ARGS(&browser));
    }
    if (FAILED(result) || !browser) return failure.Record(208, result);
    result = browser->GetWindow(&tab);
    if (FAILED(result)) return failure.Record(209, result);
    if (!WindowIs(tab, target, thread, L"ShellTabWindowClass"))
        return failure.Record(210, HRESULT_FROM_WIN32(ERROR_INVALID_WINDOW_HANDLE));
    binding.automation = std::move(automation);
    binding.browser = std::move(browser);
    binding.tab = tab;
    return true;
}

HRESULT BindBrowser(IShellWindows* windows, const RoutingTarget& target, DWORD thread,
    const std::vector<HWND>& allowed, BrowserBinding& binding) noexcept {
    BindingFailure failure;
    long count = 0;
    HRESULT result = windows->get_Count(&count);
    if (FAILED(result)) { CurrentStage = 200; return result; }
    if (count < 0 || count > static_cast<long>(MaximumTabs)) { CurrentStage = 201; return E_BOUNDS; }
    for (long index = count - 1; index >= 0; --index) {
        BrowserBinding row;
        if (BindRow(windows, index, target, thread, row, failure)) {
            if (std::find(allowed.begin(), allowed.end(), row.tab) != allowed.end()) {
                binding = std::move(row);
                return S_OK;
            }
            failure.Record(211, HRESULT_FROM_WIN32(ERROR_NOT_FOUND));
        }
    }
    CurrentStage = failure.stage;
    return failure.result;
}

bool BrowserAtFolder(IShellBrowser* browser, PCIDLIST_ABSOLUTE folder) noexcept {
    ComPtr<IShellView> view;
    ComPtr<IFolderView> folderView;
    ComPtr<IPersistFolder2> persisted;
    Pidl current;
    return SUCCEEDED(browser->QueryActiveShellView(&view)) && view &&
        SUCCEEDED(view.As(&folderView)) &&
        SUCCEEDED(folderView->GetFolder(IID_PPV_ARGS(&persisted))) && persisted &&
        SUCCEEDED(persisted->GetCurFolder(&current.value)) && current.value && ILIsEqual(folder, current.value);
}

void ProbeLaunchSite(IUnknown* site) noexcept {
    UINT flags = 0;
    if (site) {
        ComPtr<IObjectWithSelection> selection;
        ComPtr<IShellItemArray> items;
        if (SUCCEEDED(site->QueryInterface(IID_PPV_ARGS(&selection)))) flags |= 1;
        if (selection && SUCCEEDED(selection->GetSelection(IID_PPV_ARGS(&items)))) flags |= 2;
        DWORD count = 0;
        if (items && SUCCEEDED(items->GetCount(&count)) && count) flags |= 4;
        ComPtr<IServiceProvider> services;
        if (SUCCEEDED(site->QueryInterface(IID_PPV_ARGS(&services)))) {
            flags |= 8;
            selection.Reset();
            if (SUCCEEDED(services->QueryService(__uuidof(IObjectWithSelection), IID_PPV_ARGS(&selection)))) flags |= 16;
            items.Reset();
            if (selection && SUCCEEDED(selection->GetSelection(IID_PPV_ARGS(&items))) && items &&
                SUCCEEDED(items->GetCount(&count)) && count) flags |= 32;
        }
        ComPtr<IExplorerBrowserEvents> events;
        if (SUCCEEDED(site->QueryInterface(IID_PPV_ARGS(&events)))) flags |= 64;
        ComPtr<IObjectWithSite> nested;
        if (SUCCEEDED(site->QueryInterface(IID_PPV_ARGS(&nested)))) flags |= 128;
    }
    LastSiteProbe.store(flags, std::memory_order_release);
}

HRESULT CompleteHandshake(IBrowserThreadHandshake* handshake, IStream* stream,
    IBrowserThreadHandshake2* handshake2, IWebBrowser2* browser, HRESULT result) noexcept {
    if (SUCCEEDED(result)) {
        ComPtr<IUnknown> automation;
        result = browser ? browser->QueryInterface(IID_PPV_ARGS(&automation)) : E_UNEXPECTED;
        if (SUCCEEDED(result)) {
            // Match CShellBrowser::_OnCreate exactly. Its consumer seeks to zero
            // and unmarshals IID_IWebBrowser2 from this IUnknown marshal packet.
            result = CoMarshalInterface(stream, IID_IUnknown, automation.Get(), MSHCTX_INPROC, nullptr, MSHLFLAGS_NORMAL);
            if (SUCCEEDED(result) && handshake2) {
                ComPtr<IAgileReference> agile;
                result = RoGetAgileReference(AGILEREFERENCE_DEFAULT, IID_IUnknown, automation.Get(), &agile);
                if (SUCCEEDED(result)) result = handshake2->PutAgileBrowserReference(agile.Get());
            }
        }
    }
    const HRESULT published = handshake->PutResult(result);
    // Even publishing failure must be followed by the event: an external
    // SHOpenFolderAndSelectItems caller must not wait forever after dispatch.
    const HRESULT signaled = handshake->SetInternalEvent();
    if (FAILED(result)) return result;
    if (FAILED(published)) return published;
    return signaled;
}

HRESULT WaitForNewTab(const RoutingTarget& target, DWORD thread, HANDLE process,
    IShellWindows* windows, const std::vector<HWND>& before, PCIDLIST_ABSOLUTE folder,
    HWND expectedTab, BrowserBinding* created) {
    if (!expectedTab || std::find(before.begin(), before.end(), expectedTab) != before.end()) return E_UNEXPECTED;
    Handle delay(CreateEventW(nullptr, FALSE, FALSE, nullptr));
    if (!delay) return HRESULT_FROM_WIN32(GetLastError());
    const ULONGLONG started = GetTickCount64();
    const DWORD timeout = created ? HandshakeTimeout : NativeCreationTimeout;
    do {
        std::vector<HWND> after;
        if (!Snapshot(target, thread, process, after)) return E_UNEXPECTED;
        for (HWND old : before)
            if (std::find(after.begin(), after.end(), old) == after.end()) return E_UNEXPECTED;
        std::vector<HWND> added;
        for (HWND tab : after)
            if (std::find(before.begin(), before.end(), tab) == before.end()) added.push_back(tab);
        if (added.size() > 1) return E_UNEXPECTED;
        if (added.size() == 1) {
            if (added.front() != expectedTab) return E_UNEXPECTED;
            // Keep burst completion attached to the exact tab returned by the
            // owner-thread create-and-select operation. Requests without a
            // completion consumer need no catalog registration or folder load.
            if (!created) return S_OK;
            BrowserBinding candidate;
            if (SUCCEEDED(BindBrowser(windows, target, thread, added, candidate)) &&
                BrowserAtFolder(candidate.browser.Get(), folder)) {
                HWND fresh = nullptr;
                if (!TargetCurrent(target, thread, process) ||
                    FAILED(candidate.browser->GetWindow(&fresh)) || fresh != added.front() ||
                    !WindowIs(fresh, target, thread, L"ShellTabWindowClass")) return E_UNEXPECTED;
                *created = std::move(candidate);
                return S_OK;
            }
        }
        const HRESULT waited = PumpWait(delay.value, 20);
        if (FAILED(waited) && waited != RPC_S_CALLPENDING) return waited;
    } while (GetTickCount64() - started < timeout);
    return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
}

} // namespace

RouteResult TryRoutePidlToTab(const RoutingTarget& target,
    PCIDLIST_ABSOLUTE folder, IUnknown* handshake, IUnknown* site) noexcept {
    StageScope diagnostic;
    bool attempted = false;
    bool completed = false;
    ComPtr<IBrowserThreadHandshake> completion;
    ComPtr<IBrowserThreadHandshake2> completion2;
    ComPtr<IStream> output;
    try {
#if !defined(_M_X64)
        return { false, E_NOTIMPL };
#else
        APTTYPE apartment{};
        APTTYPEQUALIFIER qualifier{};
        HRESULT result = CoGetApartmentType(&apartment, &qualifier);
        if (FAILED(result)) return { false, result };
        CurrentStage = 101;
        RoutingLease lease;
        if (!lease.Acquire()) return { false, HRESULT_FROM_WIN32(ERROR_BUSY) };
        ProbeLaunchSite(site);
        CurrentStage = 102;
        DWORD pid = 0;
        const DWORD thread = GetWindowThreadProcessId(target.root, &pid);
        if (!thread || pid != target.pid) return { false, E_INVALIDARG };
        CurrentStage = 103;
        Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, FALSE, target.pid));
        if (!process || !TargetCurrent(target, thread, process.value) ||
            !SameUserAndSession(target, process.value) || !ExplorerImage(process.value) ||
            !AllowedFolder(folder)) return { false, E_ACCESSDENIED };
        CurrentStage = 104;
        if (!VerifyBrowserCode(target, process.value)) return { false, E_NOTIMPL };
        // Reject an unsupported handshake before creating any tab. All later
        // failures publish a result and signal the retained native completion.
        if (handshake) {
            CurrentStage = 105;
            result = handshake->QueryInterface(IID_PPV_ARGS(&completion));
            if (FAILED(result) || !completion) return { false, FAILED(result) ? result : E_NOINTERFACE };
            result = completion->GetStream(&output);
            if (FAILED(result) || !output) return { false, FAILED(result) ? result : E_UNEXPECTED };
            handshake->QueryInterface(IID_PPV_ARGS(&completion2));
        }
        CurrentStage = 106;
        std::vector<HWND> before;
        if (!Snapshot(target, thread, process.value, before) || before.empty() || before.size() >= MaximumTabs)
            return { false, E_UNEXPECTED };
        ComPtr<IShellWindows> windows;
        CurrentStage = 107;
        result = CoCreateInstance(CLSID_ShellWindows, nullptr, CLSCTX_ALL, IID_PPV_ARGS(&windows));
        if (FAILED(result)) return { false, result };
        BrowserBinding original;
        result = BindBrowser(windows.Get(), target, thread, before, original);
        if (FAILED(result)) return { false, result };
        CurrentStage = 108;
        std::vector<HWND> current;
        HWND fresh = nullptr;
        if (!TargetCurrent(target, thread, process.value) ||
            FAILED(original.browser->GetWindow(&fresh)) || fresh != original.tab ||
            !WindowIs(fresh, target, thread, L"ShellTabWindowClass") ||
            !Snapshot(target, thread, process.value, current) || current != before ||
            !VerifyBrowserCode(target, process.value))
            return { false, E_UNEXPECTED };
        // The owner-thread bridge marks attempted before AddTab and returns the
        // exact new foreground tab. Any failure after that point suppresses a
        // fallback, including selection or completion failures after creation.
        CurrentStage = 109;
        const NativeTabOpenResult opened = OpenNativeForegroundTab(target, folder);
        attempted = opened.attempted;
        result = opened.result;
        if (!attempted) return { false, result };
        BrowserBinding created;
        if (SUCCEEDED(result)) {
            CurrentStage = 110;
            result = WaitForNewTab(target, thread, process.value,
                windows.Get(), before, folder, opened.createdTab, completion ? &created : nullptr);
        }
        if (!completion) {
            if (SUCCEEDED(result)) CurrentStage = 112;
            return { true, SUCCEEDED(result) ? S_OK : result };
        }
        completed = true;
        CurrentStage = 111;
        result = CompleteHandshake(completion.Get(), output.Get(), completion2.Get(), created.automation.Get(), result);
        if (SUCCEEDED(result)) CurrentStage = 112;
        return { true, result };
#endif
    } catch (const std::bad_alloc&) {
        if (attempted && completion && !completed)
            CompleteHandshake(completion.Get(), output.Get(), completion2.Get(), nullptr, E_OUTOFMEMORY);
        return { attempted, E_OUTOFMEMORY };
    } catch (...) {
        if (attempted && completion && !completed)
            CompleteHandshake(completion.Get(), output.Get(), completion2.Get(), nullptr, E_UNEXPECTED);
        return { attempted, E_UNEXPECTED };
    }
}

UINT LastRoutingStage() noexcept {
    return LastStage.load(std::memory_order_acquire);
}

UINT LastRoutingSiteProbe() noexcept {
    return LastSiteProbe.load(std::memory_order_acquire);
}

} // namespace explorerone
