#include "TargetFolderBridge.h"
#include "TargetFolderProtocol.h"
#include <aclapi.h>
#include <bcrypt.h>
#include <exdisp.h>
#include <sddl.h>
#include <shlguid.h>
#include <wrl/client.h>
#include <algorithm>
#include <array>
#include <cstring>
#include <cwchar>
#include <memory>
#include <new>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace {
constexpr DWORD kExpectedTimestamp = 0xFD64D9D7;
constexpr DWORD kExpectedImageSize = 0x00323000;
constexpr LONGLONG kExpectedFileSize = 3268608;
constexpr std::uintptr_t kTabActionsVtableRva = 0x27B090;
constexpr std::uintptr_t kBrowserServiceVtableRva = 0x27E450;
constexpr GUID kBrowserServiceIid = { 0xdfbc7e30, 0xf9e5, 0x455f, { 0x88, 0xf8, 0xfa, 0x98, 0xc1, 0xe4, 0x94, 0xca } };
constexpr GUID kTabActionsIids[] = {
    { 0x5bfad855, 0xec86, 0x44d9, { 0x9e, 0xb5, 0x4e, 0x71, 0x4e, 0x07, 0x3b, 0xb5 } },
    { 0x2e3a3c22, 0x051c, 0x4aa0, { 0x80, 0xa5, 0xcd, 0xf1, 0xc2, 0xf1, 0x90, 0x33 } }
};
constexpr GUID kDelayedVisibility = { 0xd7f81f62, 0x491f, 0x49bc, { 0x89, 0x1d, 0x56, 0x65, 0x08, 0x5d, 0xf9, 0x69 } };

struct Handle final {
    HANDLE value = nullptr;
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
};
struct LocalMemory final { void* value = nullptr; ~LocalMemory() { if (value) LocalFree(value); } };
struct Pidl final { PIDLIST_ABSOLUTE value = nullptr; ~Pidl() { CoTaskMemFree(value); } };
struct Hash final {
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    std::vector<BYTE> object;
    ~Hash() { if (hash) BCryptDestroyHash(hash); if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0); }
};
HRESULT LastErrorResult() noexcept {
    const DWORD error = GetLastError();
    return HRESULT_FROM_WIN32(error ? error : ERROR_GEN_FAILURE);
}
bool ProcessStart(HANDLE process, ULONGLONG& result) noexcept {
    FILETIME created{}, exited{}, kernel{}, user{};
    if (!GetProcessTimes(process, &created, &exited, &kernel, &user)) return false;
    result = (static_cast<ULONGLONG>(created.dwHighDateTime) << 32) | created.dwLowDateTime;
    return true;
}
bool TokenSid(HANDLE process, std::wstring& result) {
    Handle token;
    if (!OpenProcessToken(process, TOKEN_QUERY, &token.value)) return false;
    alignas(TOKEN_USER) BYTE buffer[sizeof(TOKEN_USER) + SECURITY_MAX_SID_SIZE]{};
    DWORD length = 0;
    if (!GetTokenInformation(token.value, TokenUser, buffer, sizeof(buffer), &length)) return false;
    LocalMemory text;
    if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(buffer)->User.Sid,
        reinterpret_cast<LPWSTR*>(&text.value))) return false;
    result.assign(static_cast<LPCWSTR>(text.value));
    return true;
}
bool MappingOwnedByCurrentUser(HANDLE mapping) {
    PSID owner = nullptr;
    LocalMemory descriptor;
    if (GetSecurityInfo(mapping, SE_KERNEL_OBJECT, OWNER_SECURITY_INFORMATION, &owner, nullptr, nullptr, nullptr,
        reinterpret_cast<PSECURITY_DESCRIPTOR*>(&descriptor.value)) != ERROR_SUCCESS || !owner) return false;
    Handle token;
    alignas(TOKEN_USER) BYTE buffer[sizeof(TOKEN_USER) + SECURITY_MAX_SID_SIZE]{};
    DWORD length = 0;
    return OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token.value) &&
        GetTokenInformation(token.value, TokenUser, buffer, sizeof(buffer), &length) &&
        EqualSid(owner, reinterpret_cast<TOKEN_USER*>(buffer)->User.Sid) != FALSE;
}
bool Readable(const void* pointer, size_t length) noexcept {
    auto address = reinterpret_cast<std::uintptr_t>(pointer);
    if (!address || length > UINTPTR_MAX - address) return false;
    const auto end = address + length;
    while (address < end) {
        MEMORY_BASIC_INFORMATION memory{};
        if (!VirtualQuery(reinterpret_cast<const void*>(address), &memory, sizeof(memory)) ||
            memory.State != MEM_COMMIT || (memory.Protect & (PAGE_NOACCESS | PAGE_GUARD))) return false;
        const DWORD access = memory.Protect & 0xFF;
        if (access != PAGE_READONLY && access != PAGE_READWRITE && access != PAGE_WRITECOPY &&
            access != PAGE_EXECUTE_READ && access != PAGE_EXECUTE_READWRITE && access != PAGE_EXECUTE_WRITECOPY) return false;
        const auto next = reinterpret_cast<std::uintptr_t>(memory.BaseAddress) + memory.RegionSize;
        if (next <= address) return false;
        address = next;
    }
    return true;
}
bool ValidPidl(const BYTE* bytes, size_t size) noexcept {
    if (size < sizeof(USHORT) || size > kTargetFolderPidlCapacity) return false;
    size_t offset = 0;
    while (offset <= size - sizeof(USHORT)) {
        USHORT length = 0;
        memcpy(&length, bytes + offset, sizeof(length));
        if (!length) return offset + sizeof(length) == size;
        if (length < sizeof(length) || length > size - offset) return false;
        offset += length;
    }
    return false;
}
bool CopyPidl(PCIDLIST_ABSOLUTE folder, std::vector<BYTE>& result) {
    const auto* bytes = reinterpret_cast<const BYTE*>(folder);
    size_t size = 0;
    while (size <= kTargetFolderPidlCapacity - sizeof(USHORT)) {
        USHORT length = 0;
        if (!Readable(bytes + size, sizeof(length))) return false;
        memcpy(&length, bytes + size, sizeof(length));
        if (!length) { size += sizeof(length); break; }
        if (length < sizeof(length) || length > kTargetFolderPidlCapacity - size || !Readable(bytes + size, length)) return false;
        size += length;
    }
    if (size > kTargetFolderPidlCapacity || !Readable(bytes, size) || !ValidPidl(bytes, size)) return false;
    result.assign(bytes, bytes + size);
    return true;
}
bool ClassIs(HWND window, const wchar_t* expected) noexcept {
    wchar_t name[64]{};
    return GetClassNameW(window, name, _countof(name)) && wcscmp(name, expected) == 0;
}
bool OwnWindow(HWND window, HWND root, DWORD pid, DWORD thread) noexcept {
    DWORD actual = 0;
    return IsWindow(window) && GetAncestor(window, GA_ROOT) == root &&
        GetWindowThreadProcessId(window, &actual) == thread && actual == pid;
}
struct Tabs final {
    HWND root = nullptr;
    DWORD pid = 0, thread = 0;
    std::array<std::uint64_t, kTargetFolderTabCapacity> values{};
    size_t count = 0;
    bool valid = true;
};
BOOL CALLBACK CollectTab(HWND window, LPARAM parameter) noexcept {
    auto& tabs = *reinterpret_cast<Tabs*>(parameter);
    if (!ClassIs(window, L"ShellTabWindowClass")) return TRUE;
    if (!OwnWindow(window, tabs.root, tabs.pid, tabs.thread) || tabs.count == tabs.values.size()) {
        tabs.valid = false;
        return FALSE;
    }
    tabs.values[tabs.count++] = reinterpret_cast<std::uint64_t>(window);
    return TRUE;
}
Tabs ReadTabs(HWND root, DWORD pid, DWORD thread) noexcept {
    Tabs tabs; tabs.root = root; tabs.pid = pid; tabs.thread = thread;
    EnumChildWindows(root, CollectTab, reinterpret_cast<LPARAM>(&tabs));
    std::sort(tabs.values.begin(), tabs.values.begin() + tabs.count);
    if (std::adjacent_find(tabs.values.begin(), tabs.values.begin() + tabs.count) != tabs.values.begin() + tabs.count) tabs.valid = false;
    return tabs;
}
bool SameTabs(const Tabs& tabs, const TargetFolderRequest& request) noexcept {
    return tabs.valid && tabs.count == request.beforeCount &&
        std::equal(tabs.values.begin(), tabs.values.begin() + tabs.count, request.beforeTabs);
}
HWND FindCreatedTab(const TargetFolderRequest& request) noexcept {
    const auto tabs = ReadTabs(reinterpret_cast<HWND>(request.targetRoot), request.targetPid, request.targetThread);
    if (!tabs.valid || tabs.count != static_cast<size_t>(request.beforeCount) + 1) return nullptr;
    HWND created = nullptr;
    size_t originals = 0;
    for (size_t index = 0; index < tabs.count; ++index) {
        const auto item = tabs.values[index];
        if (std::binary_search(request.beforeTabs, request.beforeTabs + request.beforeCount, item)) ++originals;
        else if (!created) created = reinterpret_cast<HWND>(item);
        else return nullptr;
    }
    return originals == request.beforeCount ? created : nullptr;
}
HMODULE OwnModule() noexcept {
    HMODULE module = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(&TargetFolderGetMessageProc), &module);
    return module;
}
UINT CommandMessage() noexcept {
    static const UINT message = []() noexcept -> UINT {
        try {
            wchar_t path[32768]{};
            const auto module = OwnModule();
            const DWORD length = module ? GetModuleFileNameW(module, path, _countof(path)) : 0;
            if (!length || length >= _countof(path)) return 0;
            const auto* basename = wcsrchr(path, L'\\');
            return RegisterWindowMessageW((std::wstring(kTargetFolderMessagePrefix) + (basename ? basename + 1 : path)).c_str());
        } catch (...) { return 0; }
    }();
    return message;
}
bool ExpectedTargetProcess(HANDLE process, DWORD pid, ULONGLONG start, DWORD session, const std::wstring& sid) {
    ULONGLONG actualStart = 0;
    DWORD actualSession = 0;
    USHORT machine = 0, nativeMachine = 0;
    if (!process || !ProcessStart(process, actualStart) || actualStart != start || WaitForSingleObject(process, 0) != WAIT_TIMEOUT ||
        !ProcessIdToSessionId(pid, &actualSession) || actualSession != session ||
        !IsWow64Process2(process, &machine, &nativeMachine) || machine || nativeMachine != IMAGE_FILE_MACHINE_AMD64) return false;
    wchar_t image[32768]{}, windows[MAX_PATH]{};
    DWORD imageLength = _countof(image);
    const UINT windowsLength = GetWindowsDirectoryW(windows, _countof(windows));
    if (!windowsLength || windowsLength >= _countof(windows) || !QueryFullProcessImageNameW(process, 0, image, &imageLength) ||
        _wcsicmp(image, (std::wstring(windows) + L"\\explorer.exe").c_str()) != 0) return false;
    std::wstring actualSid;
    return TokenSid(process, actualSid) && actualSid == sid;
}

HRESULT ValidateModule(Handle& file, HMODULE& module) {
    module = GetModuleHandleW(L"ExplorerFrame.dll");
    if (!module) return HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND);
    wchar_t path[MAX_PATH]{}, system[MAX_PATH]{};
    const DWORD length = GetModuleFileNameW(module, path, _countof(path));
    const UINT systemLength = GetSystemDirectoryW(system, _countof(system));
    if (!length || length >= _countof(path) || !systemLength || systemLength >= _countof(system) ||
        _wcsicmp(path, (std::wstring(system) + L"\\ExplorerFrame.dll").c_str()) != 0) return E_ACCESSDENIED;
    file.value = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file.value == INVALID_HANDLE_VALUE) return LastErrorResult();
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(file.value, &size) || size.QuadPart != kExpectedFileSize) return E_UNEXPECTED;
    IMAGE_DOS_HEADER dos{};
    DWORD read = 0;
    if (!ReadFile(file.value, &dos, sizeof(dos), &read, nullptr) || read != sizeof(dos) ||
        dos.e_magic != IMAGE_DOS_SIGNATURE || dos.e_lfanew < sizeof(dos)) return E_UNEXPECTED;
    LARGE_INTEGER position{}; position.QuadPart = dos.e_lfanew;
    if (!SetFilePointerEx(file.value, position, nullptr, FILE_BEGIN)) return LastErrorResult();
    IMAGE_NT_HEADERS64 nt{};
    if (!ReadFile(file.value, &nt, sizeof(nt), &read, nullptr) || read != sizeof(nt) || nt.Signature != IMAGE_NT_SIGNATURE ||
        nt.FileHeader.Machine != IMAGE_FILE_MACHINE_AMD64 || nt.OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC ||
        nt.FileHeader.TimeDateStamp != kExpectedTimestamp || nt.OptionalHeader.SizeOfImage != kExpectedImageSize) return E_UNEXPECTED;
    const auto* loadedDos = reinterpret_cast<const IMAGE_DOS_HEADER*>(module);
    if (!Readable(loadedDos, sizeof(*loadedDos)) || loadedDos->e_magic != IMAGE_DOS_SIGNATURE || loadedDos->e_lfanew != dos.e_lfanew) return E_UNEXPECTED;
    const auto* loadedNt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(reinterpret_cast<const BYTE*>(module) + dos.e_lfanew);
    if (!Readable(loadedNt, sizeof(*loadedNt)) || loadedNt->Signature != IMAGE_NT_SIGNATURE ||
        loadedNt->FileHeader.TimeDateStamp != kExpectedTimestamp || loadedNt->OptionalHeader.SizeOfImage != kExpectedImageSize) return E_UNEXPECTED;
    Hash hash;
    NTSTATUS status = BCryptOpenAlgorithmProvider(&hash.algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0);
    if (status < 0) return HRESULT_FROM_NT(status);
    DWORD objectLength = 0, received = 0;
    status = BCryptGetProperty(hash.algorithm, BCRYPT_OBJECT_LENGTH, reinterpret_cast<PUCHAR>(&objectLength), sizeof(objectLength), &received, 0);
    if (status < 0) return HRESULT_FROM_NT(status);
    hash.object.resize(objectLength);
    status = BCryptCreateHash(hash.algorithm, &hash.hash, hash.object.data(), objectLength, nullptr, 0, 0);
    if (status < 0) return HRESULT_FROM_NT(status);
    position.QuadPart = 0;
    if (!SetFilePointerEx(file.value, position, nullptr, FILE_BEGIN)) return LastErrorResult();
    BYTE buffer[16384]{};
    for (;;) {
        if (!ReadFile(file.value, buffer, sizeof(buffer), &read, nullptr)) return LastErrorResult();
        if (!read) break;
        status = BCryptHashData(hash.hash, buffer, read, 0);
        if (status < 0) return HRESULT_FROM_NT(status);
    }
    BYTE digest[32]{};
    status = BCryptFinishHash(hash.hash, digest, sizeof(digest), 0);
    if (status < 0) return HRESULT_FROM_NT(status);
    constexpr BYTE expected[] = { 0xb7,0xbd,0xa6,0x01,0x1c,0x0c,0x13,0xa8,0x1a,0x6e,0xa9,0x67,0x07,0x89,0xc3,0xec,
        0xb2,0x41,0xca,0xf9,0x94,0xd3,0x1e,0x27,0x4a,0xdd,0x18,0x70,0x49,0x29,0xca,0x6d };
    if (memcmp(digest, expected, sizeof(expected))) return E_UNEXPECTED;
    // This profile's .text raw offsets equal its RVAs. Compare the invoked
    // entry points too, rejecting an independently patched private method.
    for (const DWORD rva : { 0x1BA950u, 0x1BA980u, 0x12E640u, 0xF1DD0u, 0xBB870u }) {
        BYTE prologue[32]{};
        position.QuadPart = rva;
        const auto* loaded = reinterpret_cast<const BYTE*>(module) + rva;
        if (!Readable(loaded, sizeof(prologue)) || !SetFilePointerEx(file.value, position, nullptr, FILE_BEGIN) ||
            !ReadFile(file.value, prologue, sizeof(prologue), &read, nullptr) || read != sizeof(prologue) ||
            memcmp(loaded, prologue, sizeof(prologue))) return E_UNEXPECTED;
    }
    return S_OK;
}

bool OwnTarget(const TargetFolderRequest& request) noexcept {
    const auto root = reinterpret_cast<HWND>(request.targetRoot);
    ULONGLONG start = 0;
    return request.targetPid == GetCurrentProcessId() && request.targetThread == GetCurrentThreadId() &&
        ProcessStart(GetCurrentProcess(), start) && start == request.targetStart &&
        OwnWindow(root, root, request.targetPid, request.targetThread) && ClassIs(root, L"CabinetWClass");
}
bool Authenticate(const TargetFolderRequest& request, Handle& controller) {
    if (!request.controllerPid || !request.controllerStart || !request.cookie || !request.nonceLow || !request.nonceHigh ||
        !wmemchr(request.controllerSid, L'\0', _countof(request.controllerSid)) || !request.controllerSid[0]) return false;
    DWORD session = 0, controllerSession = 0;
    if (!ProcessIdToSessionId(GetCurrentProcessId(), &session) || !session ||
        !ProcessIdToSessionId(request.controllerPid, &controllerSession) || session != controllerSession || session != request.controllerSession) return false;
    controller.value = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, request.controllerPid);
    ULONGLONG start = 0;
    std::wstring sid, ownSid;
    return controller.value && ProcessStart(controller.value, start) && start == request.controllerStart &&
        WaitForSingleObject(controller.value, 0) == WAIT_TIMEOUT && TokenSid(controller.value, sid) &&
        TokenSid(GetCurrentProcess(), ownSid) && sid == ownSid && sid == request.controllerSid;
}

struct BrowserRegistration final {
    HWND tab = nullptr;
    ComPtr<IShellBrowser> browser;
    ComPtr<IUnknown> service;
    std::vector<BYTE> folder;
};
bool SamePidl(const std::vector<BYTE>& left, const std::vector<BYTE>& right) noexcept {
    return !left.empty() && !right.empty() &&
        ILIsEqual(reinterpret_cast<PCIDLIST_ABSOLUTE>(left.data()), reinterpret_cast<PCIDLIST_ABSOLUTE>(right.data()));
}
bool ValidBrowserService(IUnknown* service, HMODULE module) noexcept {
    if (!service || !Readable(service, sizeof(void*))) return false;
    const auto* vtable = *reinterpret_cast<void***>(service);
    const auto* expected = reinterpret_cast<const void*>(reinterpret_cast<std::uintptr_t>(module) + kBrowserServiceVtableRva);
    return vtable == expected &&
        Readable(vtable, 15 * sizeof(void*)) &&
        vtable[10] == reinterpret_cast<const BYTE*>(module) + 0xF1DD0 &&
        vtable[14] == reinterpret_cast<const BYTE*>(module) + 0xBB870;
}
HRESULT ReadRegistrationFolder(const TargetFolderRequest& request, HMODULE module,
    const BrowserRegistration& registration, std::vector<BYTE>& folder) {
    if (!OwnTarget(request) || !ValidBrowserService(registration.service.Get(), module)) return E_UNEXPECTED;
    HWND browserWindow = nullptr;
    HRESULT result = registration.browser->GetWindow(&browserWindow);
    if (FAILED(result)) return result;
    const auto root = reinterpret_cast<HWND>(request.targetRoot);
    if (browserWindow != registration.tab || !ClassIs(browserWindow, L"ShellTabWindowClass") ||
        !OwnWindow(browserWindow, root, request.targetPid, request.targetThread)) return E_UNEXPECTED;
    ComPtr<IShellView> view;
    ComPtr<IFolderView> folderView;
    ComPtr<IPersistFolder2> persisted;
    Pidl actual;
    HWND viewWindow = nullptr;
    if (FAILED(result = registration.browser->QueryActiveShellView(&view)) || !view) return FAILED(result) ? result : E_PENDING;
    if (FAILED(result = view->GetWindow(&viewWindow))) return result;
    if (!OwnWindow(viewWindow, root, request.targetPid, request.targetThread) ||
        (viewWindow != browserWindow && !IsChild(browserWindow, viewWindow))) return E_UNEXPECTED;
    if (FAILED(result = view.As(&folderView)) || FAILED(result = folderView->GetFolder(IID_PPV_ARGS(&persisted))) ||
        FAILED(result = persisted->GetCurFolder(&actual.value))) return result;
    if (!actual.value || !CopyPidl(actual.value, folder)) return E_PENDING;
    using GetPidl = HRESULT (STDMETHODCALLTYPE*)(IUnknown*, PIDLIST_ABSOLUTE*);
    const auto* vtable = *reinterpret_cast<void***>(registration.service.Get());
    const auto getPidl = reinterpret_cast<GetPidl>(vtable[14]);
    Pidl native;
    if (FAILED(result = getPidl(registration.service.Get(), &native.value))) return result;
    std::vector<BYTE> ownFolder;
    if (!native.value || !CopyPidl(native.value, ownFolder) || !SamePidl(folder, ownFolder)) return E_UNEXPECTED;
    return S_OK;
}
HRESULT AcquireRegistrations(const TargetFolderRequest& request, HMODULE module, HWND created,
    std::vector<BrowserRegistration>& result) {
    ComPtr<IShellWindows> windows;
    HRESULT status = CoCreateInstance(CLSID_ShellWindows, nullptr, CLSCTX_ALL, IID_PPV_ARGS(&windows));
    if (FAILED(status)) return status;
    long count = 0;
    if (FAILED(status = windows->get_Count(&count))) return status;
    if (count < 0 || count > 4096) return E_UNEXPECTED;
    result.clear();
    result.reserve(static_cast<size_t>(request.beforeCount) + (created ? 1 : 0));
    const auto root = reinterpret_cast<HWND>(request.targetRoot);
    for (long index = 0; index < count; ++index) {
        VARIANT item{}; item.vt = VT_I4; item.lVal = index;
        ComPtr<IDispatch> dispatch;
        ComPtr<IWebBrowser2> automation;
        SHANDLE_PTR raw = 0;
        if (FAILED(windows->Item(item, &dispatch)) || !dispatch || FAILED(dispatch.As(&automation)) ||
            FAILED(automation->get_HWND(&raw)) || !OwnWindow(reinterpret_cast<HWND>(raw), root, request.targetPid, request.targetThread)) continue;
        ComPtr<IServiceProvider> services;
        BrowserRegistration candidate;
        if (FAILED(dispatch.As(&services))) continue;
        status = services->QueryService(__uuidof(IShellBrowser), IID_PPV_ARGS(&candidate.browser));
        if (FAILED(status)) status = services->QueryService(SID_STopLevelBrowser, IID_PPV_ARGS(&candidate.browser));
        if (FAILED(status) || !candidate.browser || FAILED(candidate.browser->GetWindow(&candidate.tab))) continue;
        const auto tab = reinterpret_cast<std::uint64_t>(candidate.tab);
        const bool original = std::binary_search(request.beforeTabs, request.beforeTabs + request.beforeCount, tab);
        if ((!original && candidate.tab != created) ||
            std::any_of(result.begin(), result.end(), [&](const auto& existing) { return existing.tab == candidate.tab; })) continue;
        status = candidate.browser->QueryInterface(kBrowserServiceIid, reinterpret_cast<void**>(candidate.service.GetAddressOf()));
        if (FAILED(status) || !candidate.service) return FAILED(status) ? status : E_NOINTERFACE;
        if (FAILED(status = ReadRegistrationFolder(request, module, candidate, candidate.folder))) return status;
        result.push_back(std::move(candidate));
    }
    const size_t expected = static_cast<size_t>(request.beforeCount) + (created ? 1 : 0);
    if (result.size() != expected) return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    // Every original must be refreshed before the new registration. The latter
    // must remain last even when HWND numerical order differs from tab order.
    std::sort(result.begin(), result.end(), [created](const auto& left, const auto& right) {
        if (left.tab == created) return false;
        if (right.tab == created) return true;
        return reinterpret_cast<std::uintptr_t>(left.tab) < reinterpret_cast<std::uintptr_t>(right.tab);
    });
    return S_OK;
}
HRESULT RefreshRegistrations(const TargetFolderRequest& request, HMODULE module, HWND created,
    const std::vector<BrowserRegistration>& originals) {
    if (!created || FindCreatedTab(request) != created) return E_UNEXPECTED;
    std::vector<BrowserRegistration> fresh;
    HRESULT result = AcquireRegistrations(request, module, created, fresh);
    if (FAILED(result)) return result;
    std::vector<BYTE> desired(request.pidl, request.pidl + request.pidlBytes);
    // Validate the whole set before updating any registration. Original folders
    // must still match their pre-dispatch views; the new view must be requested.
    for (const auto& registration : fresh) {
        const auto before = std::find_if(originals.begin(), originals.end(),
            [&](const auto& item) { return item.tab == registration.tab; });
        if (registration.tab == created) {
            if (!SamePidl(registration.folder, desired)) return E_PENDING;
        } else if (before == originals.end() || !SamePidl(registration.folder, before->folder)) return E_UNEXPECTED;
    }
    using UpdateWindowList = HRESULT (STDMETHODCALLTYPE*)(IUnknown*);
    for (const auto& registration : fresh) {
        std::vector<BYTE> current;
        if (FindCreatedTab(request) != created || FAILED(result = ReadRegistrationFolder(request, module, registration, current)) ||
            !SamePidl(current, registration.folder)) return FAILED(result) ? result : E_UNEXPECTED;
        const auto* vtable = *reinterpret_cast<void***>(registration.service.Get());
        const auto update = reinterpret_cast<UpdateWindowList>(vtable[10]);
        // The guarded method publishes THIS browser's own PIDL and cookie. It
        // neither navigates the view nor revokes/re-registers any Shell window.
        if (FAILED(result = update(registration.service.Get()))) return result;
    }
    return S_OK;
}
HRESULT AcquireActions(const TargetFolderRequest& request, HMODULE module, ComPtr<IUnknown>& actions) {
    ComPtr<IShellWindows> windows;
    HRESULT result = CoCreateInstance(CLSID_ShellWindows, nullptr, CLSCTX_ALL, IID_PPV_ARGS(&windows));
    if (FAILED(result)) return result;
    long count = 0;
    if (FAILED(result = windows->get_Count(&count))) return result;
    if (count < 0 || count > 4096) return E_UNEXPECTED;
    for (long index = 0; index < count; ++index) {
        VARIANT item{}; item.vt = VT_I4; item.lVal = index;
        ComPtr<IDispatch> dispatch;
        ComPtr<IWebBrowser2> automation;
        SHANDLE_PTR raw = 0;
        if (FAILED(windows->Item(item, &dispatch)) || !dispatch || FAILED(dispatch.As(&automation)) ||
            FAILED(automation->get_HWND(&raw)) || reinterpret_cast<HWND>(raw) != reinterpret_cast<HWND>(request.targetRoot)) continue;
        ComPtr<IServiceProvider> services;
        ComPtr<IShellBrowser> browser;
        if (FAILED(dispatch.As(&services)) || FAILED(services->QueryService(SID_STopLevelBrowser, IID_PPV_ARGS(&browser)))) continue;
        HWND browserWindow = nullptr;
        if (FAILED(browser->GetWindow(&browserWindow)) || !OwnWindow(browserWindow, reinterpret_cast<HWND>(request.targetRoot),
            request.targetPid, request.targetThread)) continue;
        ComPtr<IServiceProvider> browserServices;
        if (FAILED(result = browser.As(&browserServices))) return result;
        for (const auto& iid : kTabActionsIids) {
            result = browserServices->QueryService(kDelayedVisibility, iid, reinterpret_cast<void**>(actions.ReleaseAndGetAddressOf()));
            if (SUCCEEDED(result)) break;
            if (result != E_NOINTERFACE) return result;
        }
        if (FAILED(result) || !actions) return FAILED(result) ? result : E_NOINTERFACE;
        const auto* vtable = *reinterpret_cast<void***>(actions.Get());
        const auto* expected = reinterpret_cast<const void*>(reinterpret_cast<std::uintptr_t>(module) + kTabActionsVtableRva);
        if (vtable != expected || !Readable(vtable, 6 * sizeof(void*)) ||
            vtable[3] != reinterpret_cast<const BYTE*>(module) + 0x1BA950 ||
            vtable[5] != reinterpret_cast<const BYTE*>(module) + 0x12E640) return E_NOINTERFACE;
        ComPtr<IOleWindow> frameWindow;
        HWND actualRoot = nullptr;
        if (FAILED(result = actions.As(&frameWindow)) || FAILED(result = frameWindow->GetWindow(&actualRoot))) return result;
        return actualRoot == reinterpret_cast<HWND>(request.targetRoot) && OwnTarget(request) ? S_OK : E_UNEXPECTED;
    }
    return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
}
HRESULT Execute(TargetFolderRequest& shared, const TargetFolderRequest& request, HANDLE controller) {
    shared.stage = 1;
    if (!OwnTarget(request) || request.beforeCount == 0 || request.beforeCount > kTargetFolderTabCapacity ||
        !std::is_sorted(request.beforeTabs, request.beforeTabs + request.beforeCount) ||
        std::adjacent_find(request.beforeTabs, request.beforeTabs + request.beforeCount) != request.beforeTabs + request.beforeCount ||
        !ValidPidl(request.pidl, request.pidlBytes)) return E_INVALIDARG;
    APTTYPE apartment{}; APTTYPEQUALIFIER qualifier{};
    HRESULT result = CoGetApartmentType(&apartment, &qualifier);
    if (FAILED(result) || (apartment != APTTYPE_STA && apartment != APTTYPE_MAINSTA)) return RPC_E_WRONG_THREAD;
    Handle moduleFile;
    HMODULE module = nullptr;
    shared.stage = 2;
    if (FAILED(result = ValidateModule(moduleFile, module))) return result;
    ComPtr<IUnknown> actions;
    shared.stage = 3;
    if (FAILED(result = AcquireActions(request, module, actions))) return result;
    std::vector<BrowserRegistration> originals;
    if (FAILED(result = AcquireRegistrations(request, module, nullptr, originals))) return result;
    Pidl folder;
    folder.value = ILCloneFull(reinterpret_cast<PCIDLIST_ABSOLUTE>(request.pidl));
    if (!folder.value) return E_OUTOFMEMORY;
    if (!OwnTarget(request) || WaitForSingleObject(controller, 0) != WAIT_TIMEOUT ||
        !SameTabs(ReadTabs(reinterpret_cast<HWND>(request.targetRoot), request.targetPid, request.targetThread), request))
        return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
    using AddTab = HRESULT (STDMETHODCALLTYPE*)(IUnknown*, PCIDLIST_ABSOLUTE, int, GUID*);
    using SelectTab = HRESULT (STDMETHODCALLTYPE*)(IUnknown*, GUID);
    const auto* vtable = *reinterpret_cast<void***>(actions.Get());
    const auto add = reinterpret_cast<AddTab>(vtable[3]);
    const auto select = reinterpret_cast<SelectTab>(vtable[5]);
    GUID created{};
    shared.stage = 4;
    InterlockedExchange(&shared.attempted, 1); // The following call may commit.
    if (FAILED(result = add(actions.Get(), folder.value, 2, &created))) return result;
    if (IsEqualGUID(created, GUID_NULL) || !OwnTarget(request)) return E_UNEXPECTED;
    shared.stage = 5;
    if (FAILED(result = select(actions.Get(), created))) return result;
    shared.createdTab = reinterpret_cast<std::uint64_t>(FindCreatedTab(request));
    shared.stage = 7;
    if (FAILED(result = RefreshRegistrations(request, module, reinterpret_cast<HWND>(shared.createdTab), originals))) return result;
    if (OwnTarget(request)) {
        const auto root = reinterpret_cast<HWND>(request.targetRoot);
        if (IsIconic(root)) ShowWindow(root, SW_RESTORE);
        // Foreground policy can deny this request; the committed route remains
        // successful and must never be repeated because of a focus denial.
        SetForegroundWindow(root);
    }
    shared.stage = 8;
    return S_OK;
}

bool ParseNonce(const wchar_t* text, ULONGLONG& low, ULONGLONG& high) noexcept {
    low = high = 0;
    for (size_t index = 0; index < 32; ++index) {
        const wchar_t character = text[index];
        unsigned digit = 0;
        if (character >= L'0' && character <= L'9') digit = character - L'0';
        else if (character >= L'a' && character <= L'f') digit = character - L'a' + 10;
        else return false;
        auto& value = index < 16 ? low : high;
        value = (value << 4) | digit;
    }
    return low && high;
}
void Receive(const MSG& message) noexcept {
    if (message.lParam <= 0 || message.lParam > 0xFFFF) return;
    try {
        wchar_t name[128]{};
        if (!GlobalGetAtomNameW(static_cast<ATOM>(message.lParam), name, _countof(name))) return;
        constexpr size_t prefix = _countof(kTargetFolderMappingPrefix) - 1;
        ULONGLONG low = 0, high = 0;
        if (wcslen(name) != prefix + 32 || wcsncmp(name, kTargetFolderMappingPrefix, prefix) || !ParseNonce(name + prefix, low, high)) return;
        Handle mapping;
        mapping.value = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE | READ_CONTROL, FALSE, name);
        if (!mapping.value || !MappingOwnedByCurrentUser(mapping.value)) return;
        auto* shared = static_cast<TargetFolderRequest*>(MapViewOfFile(mapping.value, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(TargetFolderRequest)));
        if (!shared) return;
        // Pin before authentication, COM or a possible caller-side pending
        // cancellation. Every matched bootstrap then has a stable return address.
        HMODULE pinned = nullptr;
        const bool pin = GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
            reinterpret_cast<LPCWSTR>(&TargetFolderGetMessageProc), &pinned) != FALSE;
        if (InterlockedCompareExchange(&shared->state, kTargetFolderRunning, kTargetFolderPending) == kTargetFolderPending) {
            HRESULT result = E_ACCESSDENIED;
            try {
                const auto request = std::make_unique<TargetFolderRequest>();
                memcpy(request.get(), shared, sizeof(*request));
                Handle controller;
                if (pin && request->magic == kTargetFolderMagic && request->size == sizeof(*request) &&
                    request->version == kTargetFolderVersion && request->cookie == message.wParam &&
                    request->nonceLow == low && request->nonceHigh == high &&
                    message.hwnd == reinterpret_cast<HWND>(request->targetRoot) && OwnTarget(*request) && Authenticate(*request, controller)) {
                    shared->modulePinned = 1;
                    result = Execute(*shared, *request, controller.value);
                }
            } catch (const std::bad_alloc&) { result = E_OUTOFMEMORY; }
            catch (...) { result = E_UNEXPECTED; }
            shared->result = result;
            InterlockedExchange(&shared->state, kTargetFolderComplete);
        }
        UnmapViewOfFile(shared);
    } catch (...) { /* No C++ exception may cross Explorer's message hook. */ }
}

struct RequestLease final {
    Handle mapping, targetProcess;
    TargetFolderRequest* view = nullptr;
    HHOOK hook = nullptr;
    ATOM atom = 0;
    ~RequestLease() {
        if (view) {
            InterlockedCompareExchange(&view->state, kTargetFolderCancelled, kTargetFolderPending);
            // A claimed callback can commit and owns this lease until its ack.
            while (State() == kTargetFolderRunning && targetProcess.value && WaitForSingleObject(targetProcess.value, 50) == WAIT_TIMEOUT) {}
        }
        if (hook) UnhookWindowsHookEx(hook);
        if (atom) GlobalDeleteAtom(atom);
        if (view) UnmapViewOfFile(view);
    }
    LONG State() const noexcept { return InterlockedCompareExchange(&view->state, kTargetFolderPending, kTargetFolderPending); }
    bool CancelForResult() const noexcept {
        const auto state = InterlockedCompareExchange(&view->state, kTargetFolderCancelled, kTargetFolderPending);
        return state == kTargetFolderRunning || InterlockedCompareExchange(&view->attempted, 0, 0) != 0;
    }
};
} // namespace

namespace explorerone {
NativeTabOpenResult OpenNativeForegroundTab(const RoutingTarget& target, PCIDLIST_ABSOLUTE folder) noexcept {
    std::unique_ptr<RequestLease> lease;
    try {
        DWORD threadPid = 0, session = 0;
        const DWORD thread = GetWindowThreadProcessId(target.root, &threadPid);
        if (!target.root || !folder || !target.pid || !target.processStart || !thread || threadPid != target.pid ||
            thread == GetCurrentThreadId() || !OwnWindow(target.root, target.root, target.pid, thread) ||
            !ClassIs(target.root, L"CabinetWClass") || !ProcessIdToSessionId(GetCurrentProcessId(), &session))
            return { false, E_INVALIDARG, nullptr };
        std::wstring sid;
        ULONGLONG controllerStart = 0;
        if (!TokenSid(GetCurrentProcess(), sid) || sid.size() >= 184 || !ProcessStart(GetCurrentProcess(), controllerStart))
            return { false, E_ACCESSDENIED, nullptr };
        lease = std::make_unique<RequestLease>();
        lease->targetProcess.value = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, target.pid);
        if (!ExpectedTargetProcess(lease->targetProcess.value, target.pid, target.processStart, session, sid))
            return { false, E_ACCESSDENIED, nullptr };
        std::vector<BYTE> pidl;
        if (!CopyPidl(folder, pidl)) return { false, E_INVALIDARG, nullptr };
        const auto before = ReadTabs(target.root, target.pid, thread);
        if (!before.valid || before.count == 0 || before.count >= kTargetFolderTabCapacity) return { false, E_UNEXPECTED, nullptr };
        std::array<ULONGLONG, 3> nonce{};
        const NTSTATUS random = BCryptGenRandom(nullptr, reinterpret_cast<PUCHAR>(nonce.data()), sizeof(nonce), BCRYPT_USE_SYSTEM_PREFERRED_RNG);
        if (random < 0) return { false, HRESULT_FROM_NT(random), nullptr };
        for (auto& value : nonce) value |= 1;
        wchar_t suffix[33]{};
        swprintf_s(suffix, L"%016llx%016llx", nonce[0], nonce[1]);
        const std::wstring name = std::wstring(kTargetFolderMappingPrefix) + suffix;
        LocalMemory descriptor;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW((L"O:" + sid + L"D:P(A;;GA;;;" + sid + L")").c_str(),
            SDDL_REVISION_1, reinterpret_cast<PSECURITY_DESCRIPTOR*>(&descriptor.value), nullptr)) return { false, LastErrorResult(), nullptr };
        SECURITY_ATTRIBUTES security{ sizeof(security), descriptor.value, FALSE };
        lease->mapping.value = CreateFileMappingW(INVALID_HANDLE_VALUE, &security, PAGE_READWRITE, 0, sizeof(TargetFolderRequest), name.c_str());
        const DWORD mappingError = GetLastError();
        if (!lease->mapping.value || mappingError == ERROR_ALREADY_EXISTS) return { false, HRESULT_FROM_WIN32(mappingError), nullptr };
        lease->view = static_cast<TargetFolderRequest*>(MapViewOfFile(lease->mapping.value, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(TargetFolderRequest)));
        if (!lease->view) return { false, LastErrorResult(), nullptr };
        auto& request = *lease->view;
        request.magic = kTargetFolderMagic; request.size = sizeof(request); request.version = kTargetFolderVersion;
        request.pidlBytes = static_cast<std::uint32_t>(pidl.size()); request.state = kTargetFolderPending;
        request.controllerPid = GetCurrentProcessId(); request.controllerSession = session;
        request.targetPid = target.pid; request.targetThread = thread; request.beforeCount = static_cast<std::uint32_t>(before.count);
        request.controllerStart = controllerStart; request.targetStart = target.processStart;
        request.targetRoot = reinterpret_cast<std::uint64_t>(target.root);
        request.cookie = nonce[2]; request.nonceLow = nonce[0]; request.nonceHigh = nonce[1]; request.result = E_PENDING;
        wcscpy_s(request.controllerSid, sid.c_str());
        std::copy_n(before.values.begin(), before.count, request.beforeTabs);
        memcpy(request.pidl, pidl.data(), pidl.size());
        MemoryBarrier();
        lease->atom = GlobalAddAtomW(name.c_str());
        const auto module = OwnModule();
        const UINT message = CommandMessage();
        if (!lease->atom || !module || !message) return { false, LastErrorResult(), nullptr };
        lease->hook = SetWindowsHookExW(WH_GETMESSAGE, TargetFolderGetMessageProc, module, thread);
        if (!lease->hook) return { false, LastErrorResult(), nullptr };
        if (!PostMessageW(target.root, message, static_cast<WPARAM>(request.cookie), static_cast<LPARAM>(lease->atom)))
            return { lease->CancelForResult(), LastErrorResult(), nullptr };
        const ULONGLONG started = GetTickCount64();
        while (lease->State() == kTargetFolderPending || lease->State() == kTargetFolderRunning) {
            if (WaitForSingleObject(lease->targetProcess.value, 20) != WAIT_TIMEOUT)
                return { lease->CancelForResult(), HRESULT_FROM_WIN32(ERROR_NOT_FOUND), nullptr };
            if (GetTickCount64() - started >= 6000) {
                const auto previous = InterlockedCompareExchange(&request.state, kTargetFolderCancelled, kTargetFolderPending);
                if (previous == kTargetFolderPending || previous == kTargetFolderCancelled)
                    return { false, HRESULT_FROM_WIN32(WAIT_TIMEOUT), nullptr };
                // A running callback cannot safely be cancelled. Keep this worker
                // and lease alive; the outer ingress timeout reports attempted.
            }
        }
        if (lease->State() != kTargetFolderComplete) return { false, HRESULT_FROM_WIN32(ERROR_CANCELLED), nullptr };
        const bool attempted = InterlockedCompareExchange(&request.attempted, 0, 0) != 0;
        const HRESULT result = request.result;
        if (!attempted || FAILED(result)) return { attempted, result, nullptr };
        const ULONGLONG waitStarted = GetTickCount64();
        do {
            if (const HWND created = FindCreatedTab(request)) return { true, result, created };
            if (WaitForSingleObject(lease->targetProcess.value, 20) != WAIT_TIMEOUT) break;
        } while (GetTickCount64() - waitStarted < 2000);
        return { true, HRESULT_FROM_WIN32(ERROR_INVALID_STATE), nullptr };
    } catch (const std::bad_alloc&) {
        return { lease && lease->view ? lease->CancelForResult() : false, E_OUTOFMEMORY, nullptr };
    } catch (...) {
        return { lease && lease->view ? lease->CancelForResult() : false, E_UNEXPECTED, nullptr };
    }
}
} // namespace explorerone

extern "C" __declspec(dllexport) LRESULT CALLBACK TargetFolderGetMessageProc(int code, WPARAM first, LPARAM parameter) {
    if (code == HC_ACTION && first == PM_REMOVE && parameter) {
        const auto& message = *reinterpret_cast<const MSG*>(parameter);
        if (message.message >= 0xC000 && message.message == CommandMessage()) Receive(message);
    }
    return CallNextHookEx(nullptr, code, first, parameter);
}

extern "C" __declspec(dllexport) HRESULT WINAPI NativeOpenForegroundFolder(HWND target, DWORD pid,
    ULONGLONG targetStart, PCWSTR folder, NativeForegroundResult* output) noexcept {
    if (!output || output->size != sizeof(NativeForegroundResult)) return E_INVALIDARG;
    *output = { sizeof(NativeForegroundResult), 0, E_INVALIDARG, 100, 0 };
    try {
        const size_t length = folder ? wcsnlen_s(folder, 32768) : 0;
        DWORD actualPid = 0;
        const DWORD thread = GetWindowThreadProcessId(target, &actualPid);
        if (!length || length > 32767 || !pid || !targetStart || !thread || actualPid != pid ||
            !OwnWindow(target, target, pid, thread) || !ClassIs(target, L"CabinetWClass")) return output->result;
        Handle process;
        process.value = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
        ULONGLONG start = 0;
        DWORD exit = 0;
        if (!process.value || !ProcessStart(process.value, start) || start != targetStart ||
            !GetExitCodeProcess(process.value, &exit) || exit != STILL_ACTIVE) {
            output->result = E_ACCESSDENIED; return output->result;
        }
        output->stage = 101;
        Pidl parsed;
        SFGAOF attributes = 0;
        output->result = SHParseDisplayName(folder, nullptr, &parsed.value, SFGAO_FOLDER, &attributes);
        if (FAILED(output->result)) return output->result;
        if (!parsed.value || !(attributes & SFGAO_FOLDER)) { output->result = E_INVALIDARG; return output->result; }
        output->stage = 102;
        const auto opened = explorerone::OpenNativeForegroundTab({ target, pid, targetStart }, parsed.value);
        output->attempted = opened.attempted ? 1 : 0;
        output->result = opened.result;
        output->createdTab = reinterpret_cast<ULONGLONG>(opened.createdTab);
        output->stage = 103;
        return output->result;
    } catch (const std::bad_alloc&) { output->result = E_OUTOFMEMORY; }
    catch (...) { output->result = E_UNEXPECTED; }
    return output->result;
}
