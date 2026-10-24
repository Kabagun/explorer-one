#include "NativeLaunchProtocol.h"
#include "FolderTabRouting.h"
#include "NativeRouteQueue.h"
#include "NativeSourcePresentation.h"
#include "vendor/minhook-1.3.4/include/MinHook.h"
#include <aclapi.h>
#include <bcrypt.h>
#include <sddl.h>
#include <shlobj.h>
#include <atomic>
#include <cstddef>
#include <cstring>
#include <cwchar>
#include <cwctype>
#include <memory>
#include <new>
#include <string>
#include <vector>

namespace {
constexpr DWORD kExpectedTimestamp = 0xFD64D9D7;
constexpr DWORD kExpectedImageSize = 0x00323000;
constexpr LONGLONG kExpectedFileSize = 3268608;
constexpr DWORD kHostRva = 0x1D8F90;
constexpr DWORD kHostEnd = 0x1D8FE7;
constexpr UINT_PTR kCleanupTimer = 1;
constexpr BYTE kExpectedPrologue[] = { 0x4c,0x8b,0xdc,0x48,0x83,0xec,0x78,0x48,0x8b,0x84,0x24,0xb8,0x00,0x00,0x00,0x49,
    0x89,0x43,0xd8,0x48,0x8b,0x84,0x24,0xb0,0x00,0x00,0x00,0x49,0x89,0x43,0xd0,0x48 };

struct Handle final {
    HANDLE value = nullptr;
    Handle() = default;
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
};
struct LocalMemory final {
    void* value = nullptr;
    ~LocalMemory() { if (value) LocalFree(value); }
};
struct Hash final {
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    std::vector<BYTE> object;
    ~Hash() {
        if (hash) BCryptDestroyHash(hash);
        if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0);
    }
};
struct Controller final {
    Handle process;
    DWORD pid = 0, session = 0;
    ULONGLONG started = 0, nonceLow = 0, nonceHigh = 0;
    std::wstring sid;
    bool Alive() const noexcept { return WaitForSingleObject(process.value, 0) == WAIT_TIMEOUT; }
};
struct RouteConfiguration final {
    std::shared_ptr<Controller> controller;
    explorerone::RoutingTarget target{};
};

// Exact PDB ABI for ExplorerHostWithPrelaunch::ShowWindow. POINT is by value;
// the final private handshake interface is passed through as IUnknown only.
using HostShowWindow = HRESULT (STDMETHODCALLTYPE*)(void*, PCIDLIST_ABSOLUTE,
    UINT, POINT, int, HWND, IUnknown*, IUnknown*);
std::atomic<HostShowWindow> g_original{}, g_target{};
std::atomic<std::shared_ptr<const RouteConfiguration>> g_configuration;
std::atomic<bool> g_intercepting{}, g_routeEnabled{};
std::atomic<DWORD> g_active{};
std::atomic<ULONGLONG> g_total{}, g_routed{}, g_forwarded{}, g_failed{}, g_recursive{};
std::atomic<LONG> g_lastRoute{ E_PENDING }, g_lastOriginal{ E_PENDING };
std::atomic<UINT> g_lastFlags{};
std::atomic<DWORD> g_lastHostContext{};
thread_local bool g_insideRouter = false;

// All remaining state belongs to the desktop's shell UI thread.
std::shared_ptr<Controller> g_controller;
HWND g_agent = nullptr;
HMODULE g_module = nullptr;
bool g_pinned = false, g_initialized = false, g_removed = true;
bool g_cleanupDeferred = false, g_foreignPatch = false, g_unsupported = false;
BYTE g_ownedPatch[sizeof(kExpectedPrologue)]{};
HRESULT g_moduleValidation = E_PENDING;
LONG g_mhInitialize = -1, g_mhCreate = -1, g_mhEnable = -1;
LONG g_mhDisable = -1, g_mhRemove = -1, g_mhUninitialize = -1;

HRESULT LastErrorResult() noexcept {
    const DWORD error = GetLastError();
    return HRESULT_FROM_WIN32(error ? error : ERROR_GEN_FAILURE);
}
UINT CommandMessage() noexcept {
    static const UINT message = []() noexcept -> UINT {
        try {
            HMODULE ownModule = nullptr;
            if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                reinterpret_cast<LPCWSTR>(&NativeLaunchRouterGetMessageProc), &ownModule)) return 0;
            std::vector<wchar_t> path(32768);
            const DWORD length = GetModuleFileNameW(ownModule, path.data(), static_cast<DWORD>(path.size()));
            if (!length || length >= path.size()) return 0;
            const wchar_t* basename = wcsrchr(path.data(), L'\\');
            basename = basename ? basename + 1 : path.data();
            const std::wstring name = std::wstring(kRouterMessageName) + L"." + basename;
            return RegisterWindowMessageW(name.c_str());
        } catch (...) { return 0; }
    }();
    return message;
}
bool ProcessStart(HANDLE process, ULONGLONG& started) noexcept {
    FILETIME created{}, exited{}, kernel{}, user{};
    if (!GetProcessTimes(process, &created, &exited, &kernel, &user)) return false;
    started = (static_cast<ULONGLONG>(created.dwHighDateTime) << 32) | created.dwLowDateTime;
    return true;
}
bool TokenSid(HANDLE process, std::wstring& sid) {
    Handle token;
    if (!OpenProcessToken(process, TOKEN_QUERY, &token.value)) return false;
    alignas(TOKEN_USER) BYTE buffer[sizeof(TOKEN_USER) + SECURITY_MAX_SID_SIZE]{};
    DWORD bytes = 0;
    if (!GetTokenInformation(token.value, TokenUser, buffer, sizeof(buffer), &bytes)) return false;
    LocalMemory text;
    if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(buffer)->User.Sid,
        reinterpret_cast<LPWSTR*>(&text.value))) return false;
    sid.assign(static_cast<LPCWSTR>(text.value));
    return true;
}
bool MappingOwnedByCurrentUser(HANDLE mapping) {
    PSID owner = nullptr;
    LocalMemory descriptor;
    if (GetSecurityInfo(mapping, SE_KERNEL_OBJECT, OWNER_SECURITY_INFORMATION, &owner, nullptr, nullptr, nullptr,
        reinterpret_cast<PSECURITY_DESCRIPTOR*>(&descriptor.value)) != ERROR_SUCCESS || !owner) return false;
    Handle token;
    alignas(TOKEN_USER) BYTE buffer[sizeof(TOKEN_USER) + SECURITY_MAX_SID_SIZE]{};
    DWORD bytes = 0;
    return OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token.value) &&
        GetTokenInformation(token.value, TokenUser, buffer, sizeof(buffer), &bytes) &&
        EqualSid(owner, reinterpret_cast<TOKEN_USER*>(buffer)->User.Sid) != FALSE;
}
std::shared_ptr<Controller> AuthenticateController(const NativeLaunchRequest& request) {
    if (!request.controllerPid || request.controllerPid == GetCurrentProcessId() || !request.controllerStart ||
        !request.nonceLow || !request.nonceHigh || !wmemchr(request.controllerSid, L'\0', _countof(request.controllerSid))) return {};
    DWORD ownSession = 0, session = 0;
    if (!ProcessIdToSessionId(GetCurrentProcessId(), &ownSession) || !ownSession ||
        !ProcessIdToSessionId(request.controllerPid, &session) || session != ownSession || session != request.controllerSession) return {};
    auto controller = std::make_shared<Controller>();
    controller->process.value = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, request.controllerPid);
    std::wstring ownSid;
    if (!controller->process.value || !ProcessStart(controller->process.value, controller->started) ||
        controller->started != request.controllerStart || !TokenSid(controller->process.value, controller->sid) ||
        !TokenSid(GetCurrentProcess(), ownSid) || controller->sid != ownSid || controller->sid != request.controllerSid || !controller->Alive()) return {};
    controller->pid = request.controllerPid;
    controller->session = session;
    controller->nonceLow = request.nonceLow;
    controller->nonceHigh = request.nonceHigh;
    return controller;
}
bool SameController(const NativeLaunchRequest& request) noexcept {
    return g_controller && g_controller->Alive() && request.controllerPid == g_controller->pid &&
        request.controllerSession == g_controller->session && request.controllerStart == g_controller->started &&
        request.nonceLow == g_controller->nonceLow && request.nonceHigh == g_controller->nonceHigh &&
        wcscmp(request.controllerSid, g_controller->sid.c_str()) == 0;
}
bool ValidTarget(const explorerone::RoutingTarget& target) noexcept {
    if (!target.root) return !target.pid && !target.processStart;
    if (!target.pid || !target.processStart || !IsWindow(target.root) || GetAncestor(target.root, GA_ROOT) != target.root) return false;
    DWORD pid = 0;
    if (!GetWindowThreadProcessId(target.root, &pid) || pid != target.pid) return false;
    wchar_t windowClass[32]{};
    if (!GetClassNameW(target.root, windowClass, _countof(windowClass)) || wcscmp(windowClass, L"CabinetWClass") != 0) return false;
    Handle process;
    process.value = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, target.pid);
    ULONGLONG started = 0;
    DWORD session = 0, ownSession = 0;
    return process.value && ProcessStart(process.value, started) && started == target.processStart &&
        WaitForSingleObject(process.value, 0) == WAIT_TIMEOUT && ProcessIdToSessionId(target.pid, &session) &&
        ProcessIdToSessionId(GetCurrentProcessId(), &ownSession) && session == ownSession;
}

// Keep a non-write/non-delete-shared handle open through patch installation.
HRESULT ValidateModule(Handle& file, HMODULE& module) {
    module = GetModuleHandleW(L"ExplorerFrame.dll");
    if (!module) return HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND);
    wchar_t path[MAX_PATH]{}, system[MAX_PATH]{};
    const DWORD length = GetModuleFileNameW(module, path, _countof(path));
    const UINT systemLength = GetSystemDirectoryW(system, _countof(system));
    if (!length || length >= _countof(path) || !systemLength || systemLength >= _countof(system)) return E_UNEXPECTED;
    const std::wstring expectedPath = std::wstring(system) + L"\\ExplorerFrame.dll";
    if (_wcsicmp(path, expectedPath.c_str()) != 0) return E_ACCESSDENIED;
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
    if (loadedDos->e_magic != IMAGE_DOS_SIGNATURE || loadedDos->e_lfanew != dos.e_lfanew) return E_UNEXPECTED;
    const auto* loadedNt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(reinterpret_cast<const BYTE*>(module) + loadedDos->e_lfanew);
    if (loadedNt->Signature != IMAGE_NT_SIGNATURE || loadedNt->FileHeader.TimeDateStamp != kExpectedTimestamp ||
        loadedNt->OptionalHeader.SizeOfImage != kExpectedImageSize) return E_UNEXPECTED;
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
    constexpr BYTE expectedDigest[] = { 0xb7,0xbd,0xa6,0x01,0x1c,0x0c,0x13,0xa8,0x1a,0x6e,0xa9,0x67,0x07,0x89,0xc3,0xec,
        0xb2,0x41,0xca,0xf9,0x94,0xd3,0x1e,0x27,0x4a,0xdd,0x18,0x70,0x49,0x29,0xca,0x6d };
    return memcmp(digest, expectedDigest, sizeof(digest)) == 0 ? S_OK : E_UNEXPECTED;
}

struct ActiveCallback final {
    ActiveCallback() noexcept { g_active.fetch_add(1); }
    ~ActiveCallback() { g_active.fetch_sub(1); }
};
struct RecursionScope final {
    const bool previous = g_insideRouter;
    RecursionScope() noexcept { g_insideRouter = true; }
    ~RecursionScope() { g_insideRouter = previous; }
};
void QueuedRouteCompleted(const explorerone::RouteResult& route) noexcept {
    g_lastRoute = route.result;
    if (route.attempted) {
        g_routed.fetch_add(1);
        if (FAILED(route.result)) g_failed.fetch_add(1);
    }
}
HRESULT STDMETHODCALLTYPE RouteHost(void* self, PCIDLIST_ABSOLUTE folder, UINT flags,
    POINT point, int show, HWND owner, IUnknown* site, IUnknown* handshake) noexcept {
    const DWORD incomingError = GetLastError();
    DWORD outgoingError = incomingError;
    HRESULT result = E_UNEXPECTED;
    {
        ActiveCallback active;
        const bool recursive = g_insideRouter || explorerone::IsNativeRouteQueueThread();
        RecursionScope recursion;
        g_total.fetch_add(1);
        g_lastFlags = flags;
        g_lastHostContext = (handshake ? 0x10000u : 0u) | (site ? 0x20000u : 0u);
        if (recursive) g_recursive.fetch_add(1);
        bool attempted = false;
        if (!recursive && folder && g_routeEnabled.load()) {
            const auto configuration = g_configuration.load();
            if (configuration && configuration->controller->Alive() && configuration->target.root && ValidTarget(configuration->target)) {
                SetLastError(incomingError);
                const auto route = explorerone::RoutePidlOnSta(configuration->target, folder, handshake, site,
                    configuration->controller->process.value);
                outgoingError = GetLastError();
                g_lastRoute = route.result;
                attempted = route.attempted;
                if (attempted) {
                    result = route.result;
                }
            }
        }
        if (!attempted) {
            // Disable restores the target first. Late detour entrants therefore
            // call the restored target while callbacks that already acquired a
            // trampoline keep it alive through the active-count drain.
            const auto original = g_intercepting.load() ? g_original.load() : g_target.load();
            SetLastError(incomingError);
            if (original) result = original(self, folder, flags, point, show, owner, site, handshake);
            outgoingError = GetLastError();
            g_forwarded.fetch_add(1);
            g_lastOriginal = result;
        }
    }
    SetLastError(outgoingError);
    return result;
}

HRESULT PinModule() noexcept {
    if (g_pinned) return S_OK;
    HMODULE pinned = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(&RouteHost), &pinned) || !pinned) return LastErrorResult();
    g_module = pinned;
    g_pinned = true;
    return S_OK;
}
HRESULT InstallHook() {
    if (!g_removed) return g_intercepting.load() && !g_foreignPatch ? S_OK : HRESULT_FROM_WIN32(ERROR_BUSY);
    Handle file;
    HMODULE frame = nullptr;
    g_moduleValidation = ValidateModule(file, frame);
    if (FAILED(g_moduleValidation)) {
        // A fresh desktop process may load ExplorerFrame only on its first
        // folder launch. Let the controller retry this transient absence.
        // A present module failing identity/hash validation stays unsupported.
        g_unsupported = g_moduleValidation != HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND);
        return g_moduleValidation;
    }
    const auto target = reinterpret_cast<HostShowWindow>(reinterpret_cast<std::uintptr_t>(frame) + kHostRva);
    DWORD64 unwindBase = 0;
    const auto* unwind = RtlLookupFunctionEntry(reinterpret_cast<DWORD64>(target), &unwindBase, nullptr);
    if (!unwind || unwindBase != reinterpret_cast<DWORD64>(frame) || unwind->BeginAddress != kHostRva || unwind->EndAddress != kHostEnd ||
        memcmp(reinterpret_cast<const void*>(target), kExpectedPrologue, sizeof(kExpectedPrologue)) != 0) {
        g_unsupported = true;
        g_moduleValidation = E_UNEXPECTED;
        return E_UNEXPECTED;
    }
    g_unsupported = false;
    HRESULT result = PinModule();
    if (FAILED(result)) return result;
    result = explorerone::StartNativeRouteQueue(GetCurrentThreadId(), QueuedRouteCompleted);
    if (FAILED(result)) {
        g_cleanupDeferred = !explorerone::StopNativeRouteQueue();
        return result;
    }
    g_mhInitialize = MH_Initialize();
    if (g_mhInitialize != MH_OK) {
        g_cleanupDeferred = !explorerone::StopNativeRouteQueue();
        return E_FAIL;
    }
    g_initialized = true;
    HostShowWindow original = nullptr;
    g_mhCreate = MH_CreateHook(reinterpret_cast<LPVOID>(target), reinterpret_cast<LPVOID>(&RouteHost), reinterpret_cast<LPVOID*>(&original));
    if (g_mhCreate != MH_OK) {
        g_mhUninitialize = MH_Uninitialize();
        g_initialized = g_mhUninitialize != MH_OK;
        g_cleanupDeferred = !explorerone::StopNativeRouteQueue();
        return E_FAIL;
    }
    g_target = target;
    g_original = original;
    g_removed = false;
    g_foreignPatch = false;
    // Publish before enable: the first callback must use the trampoline.
    g_intercepting = true;
    g_mhEnable = MH_EnableHook(reinterpret_cast<LPVOID>(target));
    if (g_mhEnable != MH_OK) {
        g_intercepting = false;
        g_mhRemove = MH_RemoveHook(reinterpret_cast<LPVOID>(target));
        g_removed = g_mhRemove == MH_OK;
        if (g_removed) {
            g_mhUninitialize = MH_Uninitialize();
            g_initialized = g_mhUninitialize != MH_OK;
        }
        g_cleanupDeferred = !explorerone::StopNativeRouteQueue();
        return E_FAIL;
    }
    memcpy(g_ownedPatch, reinterpret_cast<const void*>(target), sizeof(g_ownedPatch));
    g_cleanupDeferred = false;
    return S_OK;
}
HRESULT StopHook() noexcept {
    explorerone::StopSourcePresentation();
    g_routeEnabled = false;
    g_configuration.store({});
    if (!explorerone::StopNativeRouteQueue()) {
        g_cleanupDeferred = true;
        return S_FALSE;
    }
    if (g_removed) {
        if (g_initialized) {
            g_mhUninitialize = MH_Uninitialize();
            g_initialized = g_mhUninitialize != MH_OK;
        }
        g_cleanupDeferred = g_initialized;
        return g_initialized ? E_FAIL : S_OK;
    }
    const auto target = g_target.load();
    if (g_intercepting.load()) {
        // Never let MinHook restore bytes over a patch installed by someone else.
        if (memcmp(reinterpret_cast<const void*>(target), g_ownedPatch, sizeof(g_ownedPatch)) != 0) {
            g_foreignPatch = true;
            g_cleanupDeferred = true;
            return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
        }
        g_mhDisable = MH_DisableHook(reinterpret_cast<LPVOID>(target));
        if (g_mhDisable != MH_OK) { g_cleanupDeferred = true; return E_FAIL; }
        g_intercepting = false;
        g_cleanupDeferred = true;
        return S_FALSE; // Yield one shell message turn before freeing anything.
    }
    if (g_active.load()) { g_cleanupDeferred = true; return S_FALSE; }
    g_mhRemove = MH_RemoveHook(reinterpret_cast<LPVOID>(target));
    if (g_mhRemove != MH_OK) { g_cleanupDeferred = true; return E_FAIL; }
    g_removed = true;
    if (g_initialized) {
        g_mhUninitialize = MH_Uninitialize();
        g_initialized = g_mhUninitialize != MH_OK;
    }
    g_cleanupDeferred = g_initialized;
    return g_initialized ? E_FAIL : S_OK;
}

void Snapshot(NativeLaunchRequest& response) noexcept {
    response.reserved = static_cast<std::uint32_t>(explorerone::SourcePresentationLastResult());
    response.moduleValidationResult = g_moduleValidation;
    response.agentWindow = reinterpret_cast<std::uint64_t>(g_agent);
    response.totalCalls = g_total.load();
    response.routedCalls = g_routed.load();
    response.forwardedCalls = g_forwarded.load();
    response.failedRoutes = g_failed.load();
    response.recursiveCalls = g_recursive.load();
    response.hookEnabled = g_intercepting.load() ? 1u : 0u;
    response.hookRemoved = g_removed ? 1u : 0u;
    response.cleanupDeferred = g_cleanupDeferred ? 1u : 0u;
    response.activeCallbacks = g_active.load() + explorerone::NativeRouteQueuePending();
    response.unsupported = g_unsupported ? 1u : 0u;
    response.foreignPatch = g_foreignPatch ? 1u : 0u;
    response.modulePinned = g_pinned ? 1u : 0u;
    response.mhInitialize = g_mhInitialize; response.mhCreate = g_mhCreate; response.mhEnable = g_mhEnable;
    response.mhDisable = g_mhDisable; response.mhRemove = g_mhRemove; response.mhUninitialize = g_mhUninitialize;
    response.lastRouteResult = g_lastRoute.load();
    response.lastOriginalResult = g_lastOriginal.load();
    response.lastFlags = g_lastFlags.load();
    // Diagnostic encoding only: low 16 bits are the helper stage; bit 16
    // records a handshake and bit 17 a site on the last actual host ingress;
    // bits 20..27 carry the helper's read-only site-interface probe mask.
    // These bits do not change, filter, or reinterpret the native launch flags.
    response.stage = (response.totalCalls ? explorerone::LastRoutingStage() & 0xFFFFu : 1u) |
        g_lastHostContext.load() | ((explorerone::LastRoutingSiteProbe() & 0xFFu) << 20);
    // Source appearance diagnostics keep the existing helper/context fields.
    if (explorerone::SourcePresentationEnabled()) response.stage |= 0x40000000u;
    if (FAILED(explorerone::SourcePresentationLastResult())) response.stage |= 0x20000000u;
}

void ProcessCommand(HWND destination, WPARAM nonce, LPARAM atom) noexcept;
LRESULT CALLBACK AgentWindowProc(HWND window, UINT message, WPARAM first, LPARAM second) noexcept {
    if (message == WM_TIMER && first == kCleanupTimer) {
        if (g_controller && !g_controller->Alive()) {
            const HRESULT stopped = StopHook();
            if (stopped == S_OK && g_removed && !g_cleanupDeferred) {
                g_controller.reset();
                DestroyWindow(window);
            }
        } else if (g_cleanupDeferred && !g_foreignPatch) StopHook();
        return 0;
    }
    if (message == WM_NCDESTROY) {
        KillTimer(window, kCleanupTimer);
        if (g_agent == window) g_agent = nullptr;
    } else if (message >= 0xC000 && message == CommandMessage()) {
        ProcessCommand(window, first, second);
        return 0;
    }
    return DefWindowProcW(window, message, first, second);
}
HRESULT EnsureAgent() noexcept {
    if (g_agent) return S_OK;
    HRESULT result = PinModule();
    if (FAILED(result)) return result;
    WNDCLASSEXW windowClass{};
    windowClass.cbSize = sizeof(windowClass);
    windowClass.lpfnWndProc = AgentWindowProc;
    windowClass.hInstance = g_module;
    windowClass.lpszClassName = kRouterWindowClass;
    if (!RegisterClassExW(&windowClass) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return LastErrorResult();
    g_agent = CreateWindowExW(0, kRouterWindowClass, nullptr, 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, g_module, nullptr);
    if (!g_agent) return LastErrorResult();
    if (!SetTimer(g_agent, kCleanupTimer, 250, nullptr)) {
        result = LastErrorResult();
        DestroyWindow(g_agent);
        return result;
    }
    return S_OK;
}
HRESULT Execute(const NativeLaunchRequest& request) {
    if (request.action < kRouterStart || request.action > kRouterStatus || request.reserved ||
        !wmemchr(request.controllerSid, L'\0', _countof(request.controllerSid))) return E_INVALIDARG;
    if (request.action == kRouterStart) {
        // A completed Stop may be followed by a new controller object in the
        // same process, with a fresh nonce. Only a fully removed hook permits
        // that takeover; an active/draining session keeps its existing owner.
        if (g_controller && g_controller->Alive() && !SameController(request) && !g_removed) return E_ACCESSDENIED;
        if (g_controller && !g_controller->Alive()) {
            if (StopHook() != S_OK) return HRESULT_FROM_WIN32(ERROR_BUSY);
            g_controller.reset();
        }
        auto controller = AuthenticateController(request);
        if (!controller) return E_ACCESSDENIED;
        const explorerone::RoutingTarget target{ reinterpret_cast<HWND>(request.targetRoot), request.targetPid, request.targetStart };
        if (!ValidTarget(target)) return HRESULT_FROM_WIN32(ERROR_INVALID_WINDOW_HANDLE);
        HRESULT result = EnsureAgent();
        if (FAILED(result)) return result;
        g_controller = std::move(controller);
        result = InstallHook();
        if (FAILED(result)) return result;
        auto configuration = std::make_shared<RouteConfiguration>();
        configuration->controller = g_controller;
        configuration->target = target;
        g_configuration.store(std::move(configuration));
        g_routeEnabled = true;
        explorerone::StartSourcePresentation(request);
        return S_OK;
    }
    if (!SameController(request)) return E_ACCESSDENIED;
    if (request.action == kRouterStop) return StopHook();
    if (request.action == kRouterStatus) return S_OK;
    const explorerone::RoutingTarget target{ reinterpret_cast<HWND>(request.targetRoot), request.targetPid, request.targetStart };
    if (!ValidTarget(target)) return HRESULT_FROM_WIN32(ERROR_INVALID_WINDOW_HANDLE);
    if (!g_intercepting.load() || g_foreignPatch || g_cleanupDeferred) return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
    auto configuration = std::make_shared<RouteConfiguration>();
    configuration->controller = g_controller;
    configuration->target = target;
    g_configuration.store(std::move(configuration));
    explorerone::UpdateSourcePresentation(request);
    return S_OK;
}
bool OwnShell(const NativeLaunchRequest& request, HWND destination) noexcept {
    const HWND shell = GetShellWindow();
    DWORD shellPid = 0;
    const DWORD thread = GetWindowThreadProcessId(shell, &shellPid);
    ULONGLONG started = 0;
    return shell && shellPid == GetCurrentProcessId() && shellPid == request.shellPid &&
        thread == GetCurrentThreadId() && thread == request.shellThread &&
        (destination == shell || destination == g_agent) && ProcessStart(GetCurrentProcess(), started) &&
        started == request.shellStart;
}
void ProcessCommand(HWND destination, WPARAM nonce, LPARAM atom) noexcept {
    if (atom <= 0 || atom > 0xFFFF) return;
    try {
        wchar_t name[128]{};
        if (!GlobalGetAtomNameW(static_cast<ATOM>(atom), name, _countof(name))) return;
        constexpr size_t prefix = _countof(kRouterMappingPrefix) - 1;
        const size_t length = wcslen(name);
        if (length != prefix + 32 || wcsncmp(name, kRouterMappingPrefix, prefix) != 0) return;
        for (size_t index = prefix; index < length; ++index) if (!iswxdigit(name[index])) return;
        Handle mapping;
        mapping.value = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE | READ_CONTROL, FALSE, name);
        if (!mapping.value || !MappingOwnedByCurrentUser(mapping.value)) return;
        auto* shared = static_cast<NativeLaunchRequest*>(MapViewOfFile(mapping.value, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(NativeLaunchRequest)));
        if (!shared) return;
        // No callback or timer retains this mapping. Completion is the lifetime ack.
        if (InterlockedCompareExchange(&shared->state, kRouterRunning, kRouterPending) == kRouterPending) {
            NativeLaunchRequest request{};
            memcpy(&request, shared, sizeof(request));
            HRESULT result = E_ACCESSDENIED;
            if (request.magic == kRouterMagic && request.size == sizeof(request) && request.version == kRouterVersion &&
                request.nonceLow == nonce && OwnShell(request, destination)) {
                try { result = Execute(request); }
                catch (const std::bad_alloc&) { result = E_OUTOFMEMORY; }
                catch (...) { result = E_FAIL; }
            }
            Snapshot(*shared);
            shared->result = result;
            InterlockedExchange(&shared->state, kRouterComplete);
        }
        UnmapViewOfFile(shared);
    } catch (...) { /* C++ exceptions must never enter Explorer's dispatcher. */ }
}
} // namespace

extern "C" __declspec(dllexport) LRESULT CALLBACK NativeLaunchRouterGetMessageProc(int code, WPARAM first, LPARAM parameter) {
    if (code == HC_ACTION && first == PM_REMOVE && parameter) {
        const auto* message = reinterpret_cast<const MSG*>(parameter);
        if (message->message >= 0xC000 && message->message == CommandMessage())
            ProcessCommand(message->hwnd, message->wParam, message->lParam);
    }
    return CallNextHookEx(nullptr, code, first, parameter);
}
extern "C" __declspec(dllexport) LONG WINAPI NativeLaunchRouterCancelPending(void* mappedRequest) {
    if (!mappedRequest) return kRouterCancelled;
    return InterlockedCompareExchange(&static_cast<NativeLaunchRequest*>(mappedRequest)->state, kRouterCancelled, kRouterPending);
}
BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) { return TRUE; }
