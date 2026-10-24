#include "NativeSourcePresentation.h"
#include <aclapi.h>
#include <sddl.h>
#include <atomic>
#include <cstddef>
#include <cstring>
#include <cwchar>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>

namespace {
constexpr DWORD Magic = 0x31505345, Version = 1, MaximumBaseline = 256;
constexpr size_t MaximumCandidates = 512;
constexpr ULONGLONG Lifetime = 4000;
#define SOURCE_PROP(name) inline constexpr wchar_t P##name[] = L"ExplorerOne.NativeSource.V1." #name
SOURCE_PROP(Flags); SOURCE_PROP(OwnerPid); SOURCE_PROP(OwnerStart); SOURCE_PROP(NonceLow); SOURCE_PROP(NonceHigh);
SOURCE_PROP(WindowPid); SOURCE_PROP(WindowStart); SOURCE_PROP(WindowCookie); SOURCE_PROP(Deadline);
SOURCE_PROP(OriginalExStyle); SOURCE_PROP(OriginalColorKey); SOURCE_PROP(OriginalAlpha); SOURCE_PROP(OriginalFlags); SOURCE_PROP(Claimed);
#undef SOURCE_PROP
constexpr const wchar_t* Properties[] = { PFlags, POwnerPid, POwnerStart, PNonceLow, PNonceHigh,
    PWindowPid, PWindowStart, PWindowCookie, PDeadline, POriginalExStyle, POriginalColorKey, POriginalAlpha, POriginalFlags, PClaimed };
struct Handle final {
    HANDLE value = nullptr;
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
};
struct LocalMemory final { void* value = nullptr; ~LocalMemory() { if (value) LocalFree(value); } };
struct ProtectedWindow final { ULONGLONG window = 0, start = 0; DWORD pid = 0, reserved = 0; };
struct Configuration final {
    DWORD magic = Magic, size = sizeof(Configuration), version = Version;
    volatile LONG sequence = 0;
    DWORD enabled = 0, controllerPid = 0, session = 0, targetPid = 0, count = 0;
    // Occupies the existing alignment DWORD; the shared layout stays unchanged.
    DWORD baselineComplete = 0;
    ULONGLONG controllerStart = 0, nonceLow = 0, nonceHigh = 0, targetRoot = 0, targetStart = 0;
    wchar_t sid[184]{};
    ProtectedWindow baseline[MaximumBaseline]{};
};
struct Mapping final {
    Handle handle;
    Configuration* view = nullptr;
    ~Mapping() { if (view) UnmapViewOfFile(view); }
};
struct Candidate final {
    HWND window = nullptr;
    Handle controller;
    Configuration configuration{};
    ULONGLONG created = 0, deadline = 0, cookie = 0, processStart = 0;
    LONG_PTR originalStyle = 0;
    COLORREF colorKey = 0;
    BYTE alpha = 255;
    DWORD layeredFlags = LWA_ALPHA, snapshotFlags = kNativeSourceValid;
    std::atomic<bool> suppressed{ false };
    std::atomic<bool> processing{ false };
    bool alphaApplied = false;
};
static_assert(sizeof(Configuration) == 6592);
static_assert(offsetof(Configuration, baseline) == 448);
std::mutex g_candidatesMutex;
std::unordered_map<HWND, std::shared_ptr<Candidate>> g_candidates;
std::atomic<ULONGLONG> g_cookie{ 1 };
Mapping g_mapping;
HWINEVENTHOOK g_eventHook = nullptr;
HHOOK g_callWndHook = nullptr;
HRESULT g_lastResult = S_FALSE;

HRESULT ErrorResult() noexcept { const DWORD error = GetLastError(); return HRESULT_FROM_WIN32(error ? error : ERROR_GEN_FAILURE); }
bool ProcessStart(HANDLE process, ULONGLONG& start) noexcept {
    FILETIME created{}, exited{}, kernel{}, user{};
    if (!GetProcessTimes(process, &created, &exited, &kernel, &user)) return false;
    start = (static_cast<ULONGLONG>(created.dwHighDateTime) << 32) | created.dwLowDateTime;
    return true;
}
bool TokenSid(HANDLE process, std::wstring& sid) {
    Handle token;
    alignas(TOKEN_USER) BYTE user[sizeof(TOKEN_USER) + SECURITY_MAX_SID_SIZE]{};
    DWORD received = 0;
    LocalMemory text;
    if (!OpenProcessToken(process, TOKEN_QUERY, &token.value) ||
        !GetTokenInformation(token.value, TokenUser, user, sizeof(user), &received) ||
        !ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(user)->User.Sid, reinterpret_cast<LPWSTR*>(&text.value))) return false;
    sid.assign(static_cast<const wchar_t*>(text.value));
    return true;
}
HMODULE OwnModule(bool pin = false) noexcept {
    HMODULE module = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
        (pin ? GET_MODULE_HANDLE_EX_FLAG_PIN : GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT),
        reinterpret_cast<LPCWSTR>(&NativeSourcePresentationEventProc), &module);
    return module;
}
std::wstring MappingName() {
    wchar_t path[32768]{};
    const HMODULE module = OwnModule();
    const DWORD length = module ? GetModuleFileNameW(module, path, _countof(path)) : 0;
    if (!length || length >= _countof(path)) return {};
    const auto* basename = wcsrchr(path, L'\\');
    return std::wstring(L"Local\\ExplorerOne.NativeSource.V1.") + (basename ? basename + 1 : path);
}
bool ExplorerImage(HANDLE process) noexcept {
    wchar_t actual[32768]{}, windows[MAX_PATH]{};
    DWORD length = _countof(actual);
    const UINT count = GetWindowsDirectoryW(windows, _countof(windows));
    if (!count || count >= _countof(windows) || !QueryFullProcessImageNameW(process, 0, actual, &length)) return false;
    const size_t suffix = wcslen(L"\\explorer.exe");
    return length == count + suffix && _wcsnicmp(actual, windows, count) == 0 && _wcsicmp(actual + count, L"\\explorer.exe") == 0;
}
bool CurrentProcessIsExplorer() noexcept {
    static const bool explorer = ExplorerImage(GetCurrentProcess());
    return explorer;
}
bool Cabinet(HWND window, DWORD& pid, DWORD& thread) noexcept {
    wchar_t name[32]{};
    thread = GetWindowThreadProcessId(window, &pid);
    return thread && IsWindow(window) && GetAncestor(window, GA_ROOT) == window &&
        !(GetWindowLongPtrW(window, GWL_STYLE) & WS_CHILD) &&
        GetClassNameW(window, name, _countof(name)) && wcscmp(name, L"CabinetWClass") == 0;
}
bool SameUserProcess(HANDLE process, DWORD pid, DWORD session, ULONGLONG start, const wchar_t* expectedSid) {
    DWORD actualSession = 0;
    ULONGLONG actualStart = 0;
    std::wstring sid;
    return ProcessIdToSessionId(pid, &actualSession) && actualSession == session &&
        ProcessStart(process, actualStart) && actualStart == start && TokenSid(process, sid) && sid == expectedSid;
}
bool MainAlive(const Configuration& configuration) {
    if (!configuration.targetRoot || !configuration.targetPid || !configuration.targetStart) return false;
    const HWND root = reinterpret_cast<HWND>(configuration.targetRoot);
    DWORD pid = 0, thread = 0;
    if (!Cabinet(root, pid, thread) || pid != configuration.targetPid) return false;
    Handle process;
    process.value = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, pid);
    return process.value && WaitForSingleObject(process.value, 0) == WAIT_TIMEOUT && ExplorerImage(process.value) &&
        SameUserProcess(process.value, pid, configuration.session, configuration.targetStart, configuration.sid);
}
bool MappingOwner(HANDLE mapping) {
    LocalMemory descriptor;
    PSID owner = nullptr;
    Handle token;
    alignas(TOKEN_USER) BYTE user[sizeof(TOKEN_USER) + SECURITY_MAX_SID_SIZE]{};
    DWORD bytes = 0;
    return GetSecurityInfo(mapping, SE_KERNEL_OBJECT, OWNER_SECURITY_INFORMATION, &owner, nullptr, nullptr, nullptr,
        reinterpret_cast<PSECURITY_DESCRIPTOR*>(&descriptor.value)) == ERROR_SUCCESS && owner &&
        OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token.value) &&
        GetTokenInformation(token.value, TokenUser, user, sizeof(user), &bytes) &&
        EqualSid(owner, reinterpret_cast<TOKEN_USER*>(user)->User.Sid) != FALSE;
}
bool ReadConfiguration(Configuration& configuration) {
    const auto name = MappingName();
    if (name.empty()) return false;
    Handle mapping;
    mapping.value = OpenFileMappingW(FILE_MAP_READ | READ_CONTROL, FALSE, name.c_str());
    if (!mapping.value || !MappingOwner(mapping.value)) return false;
    const auto* view = static_cast<const Configuration*>(MapViewOfFile(mapping.value, FILE_MAP_READ, 0, 0, sizeof(Configuration)));
    if (!view) return false;
    bool consistent = false;
    for (int attempt = 0; attempt != 3; ++attempt) {
        const LONG before = view->sequence;
        if (before & 1) continue;
        MemoryBarrier();
        memcpy(&configuration, view, sizeof(configuration));
        MemoryBarrier();
        if (before == view->sequence) { consistent = true; break; }
    }
    UnmapViewOfFile(view);
    return consistent && configuration.magic == Magic && configuration.size == sizeof(configuration) &&
        configuration.version == Version && configuration.enabled && configuration.controllerPid &&
        configuration.controllerStart && configuration.nonceLow && configuration.nonceHigh && configuration.count <= MaximumBaseline &&
        configuration.baselineComplete == 1 &&
        wmemchr(configuration.sid, L'\0', _countof(configuration.sid));
}
bool ProtectedByBaseline(const Configuration& configuration, HWND window, DWORD pid, ULONGLONG started) noexcept {
    if (reinterpret_cast<ULONGLONG>(window) == configuration.targetRoot) return true;
    for (DWORD index = 0; index < configuration.count; ++index)
        if (configuration.baseline[index].window == reinterpret_cast<ULONGLONG>(window) &&
            configuration.baseline[index].pid == pid && configuration.baseline[index].start == started) return true;
    return false;
}
std::shared_ptr<Candidate> FindCandidate(HWND window) {
    const std::lock_guard lock(g_candidatesMutex);
    const auto found = g_candidates.find(window);
    return found == g_candidates.end() ? nullptr : found->second;
}
std::shared_ptr<Candidate> AuthenticatedCandidate(HWND window, const Configuration& configuration) {
    ULONGLONG started = 0;
    const DWORD pid = GetCurrentProcessId();
    if (configuration.baselineComplete != 1 || !ProcessStart(GetCurrentProcess(), started) ||
        ProtectedByBaseline(configuration, window, pid, started) ||
        !SameUserProcess(GetCurrentProcess(), pid, configuration.session, started, configuration.sid) || !MainAlive(configuration)) return {};
    auto candidate = std::make_shared<Candidate>();
    candidate->window = window; candidate->configuration = configuration;
    candidate->created = GetTickCount64(); candidate->processStart = started; candidate->cookie = g_cookie.fetch_add(1);
    candidate->controller.value = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, configuration.controllerPid);
    if (!candidate->controller.value || WaitForSingleObject(candidate->controller.value, 0) != WAIT_TIMEOUT ||
        !SameUserProcess(candidate->controller.value, configuration.controllerPid,
            configuration.session, configuration.controllerStart, configuration.sid)) return {};
    const std::lock_guard lock(g_candidatesMutex);
    const auto existing = g_candidates.find(window);
    if (existing != g_candidates.end()) return existing->second;
    // Never evict a suppressed window: its timer and original appearance remain owned.
    if (g_candidates.size() >= MaximumCandidates) return {};
    g_candidates.emplace(window, candidate);
    return candidate;
}
void Publish(const Configuration& configuration) noexcept {
    if (!g_mapping.view) return;
    const LONG sequence = InterlockedIncrement(&g_mapping.view->sequence);
    memcpy(g_mapping.view, &configuration, offsetof(Configuration, sequence));
    memcpy(reinterpret_cast<BYTE*>(g_mapping.view) + offsetof(Configuration, enabled),
        reinterpret_cast<const BYTE*>(&configuration) + offsetof(Configuration, enabled),
        sizeof(Configuration) - offsetof(Configuration, enabled));
    MemoryBarrier();
    InterlockedExchange(&g_mapping.view->sequence, sequence + 1);
}
bool Matches(const Candidate& candidate) noexcept {
    const HWND window = candidate.window;
    return reinterpret_cast<ULONG_PTR>(GetPropW(window, PFlags)) == candidate.snapshotFlags &&
        reinterpret_cast<ULONG_PTR>(GetPropW(window, POwnerPid)) == candidate.configuration.controllerPid &&
        reinterpret_cast<ULONG_PTR>(GetPropW(window, POwnerStart)) == candidate.configuration.controllerStart &&
        reinterpret_cast<ULONG_PTR>(GetPropW(window, PNonceLow)) == candidate.configuration.nonceLow &&
        reinterpret_cast<ULONG_PTR>(GetPropW(window, PNonceHigh)) == candidate.configuration.nonceHigh &&
        reinterpret_cast<ULONG_PTR>(GetPropW(window, PWindowCookie)) == candidate.cookie &&
        reinterpret_cast<ULONG_PTR>(GetPropW(window, PWindowPid)) == GetCurrentProcessId() &&
        reinterpret_cast<ULONG_PTR>(GetPropW(window, PWindowStart)) == candidate.processStart;
}
bool Property(HWND window, const wchar_t* name, ULONG_PTR value) noexcept {
    return SetPropW(window, name, reinterpret_cast<HANDLE>(value)) != FALSE;
}
void RemoveProperties(HWND window) noexcept {
    // Clear the commit marker first so concurrent managed readers fail closed.
    for (const auto* property : Properties) RemovePropW(window, property);
}
bool PublishSnapshot(Candidate& candidate) noexcept {
    const HWND window = candidate.window;
    if (GetPropW(window, PFlags)) return false;
    const auto& configuration = candidate.configuration;
    if (!Property(window, POwnerPid, configuration.controllerPid) || !Property(window, POwnerStart, configuration.controllerStart) ||
        !Property(window, PNonceLow, configuration.nonceLow) || !Property(window, PNonceHigh, configuration.nonceHigh) ||
        !Property(window, PWindowPid, GetCurrentProcessId()) || !Property(window, PWindowStart, candidate.processStart) ||
        !Property(window, PWindowCookie, candidate.cookie) || !Property(window, PDeadline, candidate.deadline) ||
        !Property(window, POriginalExStyle, static_cast<DWORD>(candidate.originalStyle)) ||
        !Property(window, POriginalColorKey, candidate.colorKey) || !Property(window, POriginalAlpha, candidate.alpha) ||
        !Property(window, POriginalFlags, candidate.layeredFlags) || !Property(window, PClaimed, 0) ||
        !Property(window, PFlags, candidate.snapshotFlags)) {
        RemoveProperties(window);
        return false;
    }
    return true;
}
void Erase(HWND window) noexcept { const std::lock_guard lock(g_candidatesMutex); g_candidates.erase(window); }
void CALLBACK SourceTimer(HWND window, UINT, UINT_PTR timer, DWORD) noexcept;
UINT_PTR TimerId() noexcept { return reinterpret_cast<UINT_PTR>(&SourceTimer); }
void Restore(Candidate& candidate) noexcept {
    // Appearance changes send window messages synchronously. The pre-show hook
    // must not reapply suppression while this owned restoration is in progress.
    const bool alreadyProcessing = candidate.processing.exchange(true);
    struct ProcessingReset final {
        Candidate& candidate;
        bool previous;
        ~ProcessingReset() { candidate.processing.store(previous); }
    } processingReset{ candidate, alreadyProcessing };
    if (candidate.suppressed && Matches(candidate)) {
        COLORREF currentKey = 0;
        BYTE currentAlpha = 255;
        DWORD currentFlags = 0;
        const bool ownsAppearance = !candidate.alphaApplied ||
            (GetLayeredWindowAttributes(candidate.window, &currentKey, &currentAlpha, &currentFlags) &&
            currentAlpha == 0 && currentKey == candidate.colorKey && currentFlags == (candidate.layeredFlags | LWA_ALPHA));
        if (ownsAppearance) {
            if (candidate.snapshotFlags & kNativeSourceWasLayered) {
                if (!SetLayeredWindowAttributes(candidate.window, candidate.colorKey, candidate.alpha, candidate.layeredFlags)) return;
            } else {
                const LONG_PTR currentStyle = GetWindowLongPtrW(candidate.window, GWL_EXSTYLE);
                SetLastError(0);
                const LONG_PTR previous = SetWindowLongPtrW(candidate.window, GWL_EXSTYLE, currentStyle & ~static_cast<LONG_PTR>(WS_EX_LAYERED));
                if (!previous && GetLastError()) return;
            }
        }
        RemoveProperties(candidate.window);
    }
    KillTimer(candidate.window, TimerId());
    Erase(candidate.window);
}
void CALLBACK SourceTimer(HWND window, UINT, UINT_PTR timer, DWORD) noexcept {
    try {
        std::shared_ptr<Candidate> candidate;
        { const std::lock_guard lock(g_candidatesMutex); const auto found = g_candidates.find(window); if (found != g_candidates.end()) candidate = found->second; }
        if (!candidate) { KillTimer(window, timer); return; }
        if (!candidate->suppressed || !Matches(*candidate)) { KillTimer(window, timer); Erase(window); return; }
        Configuration current{};
        if (GetTickCount64() >= candidate->deadline || WaitForSingleObject(candidate->controller.value, 0) != WAIT_TIMEOUT ||
            !ReadConfiguration(current) || current.controllerPid != candidate->configuration.controllerPid ||
            current.controllerStart != candidate->configuration.controllerStart || current.nonceLow != candidate->configuration.nonceLow ||
            current.nonceHigh != candidate->configuration.nonceHigh || !MainAlive(current)) Restore(*candidate);
    } catch (...) { /* The fixed deadline remains enforceable on the next timer. */ }
}
void PresentSource(HWND window, bool creating) {
    DWORD pid = 0;
    const DWORD thread = GetWindowThreadProcessId(window, &pid);
    wchar_t name[32]{};
    if (!thread || pid != GetCurrentProcessId() || thread != GetCurrentThreadId() || !IsWindow(window) ||
        !GetClassNameW(window, name, _countof(name)) || wcscmp(name, L"CabinetWClass") != 0 ||
        (GetWindowLongPtrW(window, GWL_STYLE) & WS_CHILD) || IsIconic(window)) return;
    // At WM_NCCREATE / CREATE the parent relation may still be unfinished. The
    // exact class, non-child style, owner thread and authenticated identity are
    // sufficient for early suppression. Later paths require the final root too.
    if (!creating && GetAncestor(window, GA_ROOT) != window) return;
    auto candidate = FindCandidate(window);
    if (!candidate) {
        Configuration configuration{};
        if (!ReadConfiguration(configuration)) return;
        candidate = AuthenticatedCandidate(window, configuration);
    }
    if (!candidate || candidate->processing.exchange(true)) return;
    struct ProcessingReset final {
        Candidate& candidate;
        ~ProcessingReset() { candidate.processing.store(false); }
    } processingReset{ *candidate };
    if (candidate->suppressed && reinterpret_cast<ULONG_PTR>(GetPropW(window, PClaimed)) >= kNativeSourceRestoring) return;
    Configuration current{};
    if ((!candidate->suppressed && GetTickCount64() - candidate->created > Lifetime) ||
        (candidate->suppressed && GetTickCount64() >= candidate->deadline) ||
        WaitForSingleObject(candidate->controller.value, 0) != WAIT_TIMEOUT || !ReadConfiguration(current) ||
        current.controllerPid != candidate->configuration.controllerPid || current.controllerStart != candidate->configuration.controllerStart ||
        current.nonceLow != candidate->configuration.nonceLow || current.nonceHigh != candidate->configuration.nonceHigh ||
        ProtectedByBaseline(current, window, pid, candidate->processStart) || !MainAlive(current)) {
        if (candidate->suppressed) Restore(*candidate); else Erase(window);
        return;
    }
    if (!candidate->suppressed) {
        if (!OwnModule(true)) { Erase(window); return; }
        // This is the first mutation, including when it happens during creation.
        // Never recapture our own alpha-zero attributes at a later SHOW.
        candidate->originalStyle = GetWindowLongPtrW(window, GWL_EXSTYLE);
        if (candidate->originalStyle & WS_EX_LAYERED) {
            candidate->snapshotFlags |= kNativeSourceWasLayered;
            if (!GetLayeredWindowAttributes(window, &candidate->colorKey, &candidate->alpha, &candidate->layeredFlags) ||
                !candidate->layeredFlags || (candidate->layeredFlags & ~(LWA_COLORKEY | LWA_ALPHA))) { Erase(window); return; }
            candidate->snapshotFlags |= kNativeSourceHadAttributes;
        }
        candidate->deadline = GetTickCount64() + Lifetime;
        // Arm the independent recovery before publishing a claimable snapshot.
        if (!SetTimer(window, TimerId(), 250, SourceTimer)) { Erase(window); return; }
        if (!PublishSnapshot(*candidate)) { KillTimer(window, TimerId()); Erase(window); return; }
        candidate->suppressed = true;
    }
    if (!Matches(*candidate)) { Erase(window); return; }
    // Explorer can finalize its extended style after CREATE. Reassert only the
    // owned layered bit before show; retain every unrelated current style bit.
    const LONG_PTR currentStyle = GetWindowLongPtrW(window, GWL_EXSTYLE);
    if (!(currentStyle & WS_EX_LAYERED)) {
        if (!Matches(*candidate) || reinterpret_cast<ULONG_PTR>(GetPropW(window, PClaimed)) >= kNativeSourceRestoring) return;
        SetLastError(0);
        const LONG_PTR previous = SetWindowLongPtrW(window, GWL_EXSTYLE, currentStyle | WS_EX_LAYERED);
        if (!previous && GetLastError()) { Restore(*candidate); return; }
    }
    if (!Matches(*candidate) || reinterpret_cast<ULONG_PTR>(GetPropW(window, PClaimed)) >= kNativeSourceRestoring) return;
    if (!SetLayeredWindowAttributes(window, candidate->colorKey, 0, candidate->layeredFlags | LWA_ALPHA)) { Restore(*candidate); return; }
    candidate->alphaApplied = true;
    if (!SetTimer(window, TimerId(), 250, SourceTimer)) Restore(*candidate);
}
BOOL CALLBACK CaptureBaseline(HWND window, LPARAM parameter) noexcept {
    auto& configuration = *reinterpret_cast<Configuration*>(parameter);
    wchar_t name[32]{};
    if (!GetClassNameW(window, name, _countof(name))) {
        if (!IsWindow(window)) return TRUE;
        configuration.baselineComplete = 0;
        return FALSE;
    }
    if (wcscmp(name, L"CabinetWClass") != 0) return TRUE;
    DWORD pid = 0;
    if (!GetWindowThreadProcessId(window, &pid)) { configuration.baselineComplete = 0; return FALSE; }
    Handle process;
    process.value = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    ULONGLONG started = 0;
    // Include every enumerated Cabinet, including hidden roots. An inaccessible
    // identity must fail the baseline rather than make a preexisting HWND eligible.
    if (!process.value || !ProcessStart(process.value, started)) { configuration.baselineComplete = 0; return FALSE; }
    if (configuration.count == MaximumBaseline) {
        configuration.count = MaximumBaseline + 1; configuration.baselineComplete = 0; return FALSE;
    }
    configuration.baseline[configuration.count++] = { reinterpret_cast<ULONGLONG>(window), started, pid, 0 };
    return TRUE;
}
bool Populate(const NativeLaunchRequest& request, Configuration& configuration) {
    if (!request.controllerPid || !request.controllerStart || !request.nonceLow || !request.nonceHigh ||
        !wmemchr(request.controllerSid, L'\0', _countof(request.controllerSid))) return false;
    Handle controller;
    controller.value = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, request.controllerPid);
    std::wstring ownSid;
    DWORD ownSession = 0;
    if (!controller.value || WaitForSingleObject(controller.value, 0) != WAIT_TIMEOUT || !TokenSid(GetCurrentProcess(), ownSid) ||
        ownSid != request.controllerSid || !ProcessIdToSessionId(GetCurrentProcessId(), &ownSession) || ownSession != request.controllerSession ||
        !SameUserProcess(controller.value, request.controllerPid, ownSession, request.controllerStart, request.controllerSid)) return false;
    configuration.enabled = 1;
    configuration.controllerPid = request.controllerPid;
    configuration.controllerStart = request.controllerStart;
    configuration.session = request.controllerSession;
    configuration.nonceLow = request.nonceLow; configuration.nonceHigh = request.nonceHigh;
    configuration.targetRoot = request.targetRoot; configuration.targetPid = request.targetPid; configuration.targetStart = request.targetStart;
    wcscpy_s(configuration.sid, request.controllerSid);
    return true;
}
} // namespace

namespace explorerone {
HRESULT StartSourcePresentation(const NativeLaunchRequest& request) noexcept {
    try {
        if (g_eventHook && g_callWndHook) return UpdateSourcePresentation(request);
        Configuration configuration{};
        if (!Populate(request, configuration)) return g_lastResult = E_ACCESSDENIED;
        configuration.baselineComplete = 1;
        const BOOL enumerated = EnumWindows(CaptureBaseline, reinterpret_cast<LPARAM>(&configuration));
        if (configuration.count > MaximumBaseline) return g_lastResult = HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW);
        if (!enumerated || configuration.baselineComplete != 1) return g_lastResult = HRESULT_FROM_WIN32(ERROR_RETRY);
        const auto name = MappingName();
        const std::wstring sddl = L"O:" + std::wstring(configuration.sid) + L"D:P(A;;GA;;;" + configuration.sid + L")";
        LocalMemory descriptor;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1,
            reinterpret_cast<PSECURITY_DESCRIPTOR*>(&descriptor.value), nullptr)) return g_lastResult = ErrorResult();
        SECURITY_ATTRIBUTES attributes{ sizeof(attributes), descriptor.value, FALSE };
        if (!g_mapping.handle.value) {
            g_mapping.handle.value = CreateFileMappingW(INVALID_HANDLE_VALUE, &attributes, PAGE_READWRITE, 0, sizeof(Configuration), name.c_str());
            if (!g_mapping.handle.value) return g_lastResult = ErrorResult();
            if (GetLastError() == ERROR_ALREADY_EXISTS) { CloseHandle(g_mapping.handle.value); g_mapping.handle.value = nullptr; return g_lastResult = HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS); }
            g_mapping.view = static_cast<Configuration*>(MapViewOfFile(g_mapping.handle.value, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(Configuration)));
            if (!g_mapping.view) {
                const HRESULT result = ErrorResult();
                CloseHandle(g_mapping.handle.value); g_mapping.handle.value = nullptr;
                return g_lastResult = result;
            }
        }
        Publish(configuration);
        // WinEvent SHOW can be raised after the first visible interval. This
        // synchronous hook covers creation plus pre-show window messages instead.
        g_callWndHook = SetWindowsHookExW(WH_CALLWNDPROC, NativeSourcePresentationCallWndProc, OwnModule(), 0);
        if (!g_callWndHook) { configuration.enabled = 0; Publish(configuration); return g_lastResult = ErrorResult(); }
        g_eventHook = SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_SHOW, OwnModule(), NativeSourcePresentationEventProc,
            0, 0, WINEVENT_INCONTEXT);
        if (!g_eventHook) {
            const HRESULT result = ErrorResult();
            configuration.enabled = 0; Publish(configuration);
            UnhookWindowsHookEx(g_callWndHook); g_callWndHook = nullptr;
            return g_lastResult = result;
        }
        return g_lastResult = S_OK;
    } catch (const std::bad_alloc&) { return g_lastResult = E_OUTOFMEMORY; }
    catch (...) { return g_lastResult = E_UNEXPECTED; }
}
HRESULT UpdateSourcePresentation(const NativeLaunchRequest& request) noexcept {
    try {
        if (!g_eventHook || !g_callWndHook || !g_mapping.view) return StartSourcePresentation(request);
        Configuration configuration{};
        memcpy(&configuration, g_mapping.view, sizeof(configuration));
        if (configuration.controllerPid != request.controllerPid || configuration.controllerStart != request.controllerStart ||
            configuration.nonceLow != request.nonceLow || configuration.nonceHigh != request.nonceHigh || !Populate(request, configuration))
            return g_lastResult = E_ACCESSDENIED;
        Publish(configuration);
        return g_lastResult = S_OK;
    } catch (...) { return g_lastResult = E_UNEXPECTED; }
}
void StopSourcePresentation() noexcept {
    if (g_mapping.view) {
        Configuration configuration{};
        memcpy(&configuration, g_mapping.view, sizeof(configuration));
        configuration.enabled = 0;
        Publish(configuration);
    }
    if (g_eventHook) { UnhookWinEvent(g_eventHook); g_eventHook = nullptr; }
    if (g_callWndHook) { UnhookWindowsHookEx(g_callWndHook); g_callWndHook = nullptr; }
}
bool SourcePresentationEnabled() noexcept { return g_eventHook != nullptr && g_callWndHook != nullptr; }
HRESULT SourcePresentationLastResult() noexcept { return g_lastResult; }
} // namespace explorerone

extern "C" __declspec(dllexport) void CALLBACK NativeSourcePresentationEventProc(
    HWINEVENTHOOK, DWORD event, HWND window, LONG object, LONG child, DWORD, DWORD) {
    // In-context hooks can load into GUI processes. Do not inspect windows,
    // mappings, input, or content in any executable except system Explorer.
    if (!CurrentProcessIsExplorer() || object != OBJID_WINDOW || child != CHILDID_SELF || !window) return;
    try {
        if (event == EVENT_OBJECT_DESTROY) {
            const auto candidate = FindCandidate(window);
            if (candidate) {
                if (candidate->suppressed) KillTimer(window, TimerId());
                Erase(window);
            }
            return;
        }
        if (event == EVENT_OBJECT_CREATE) PresentSource(window, true);
        else if (event == EVENT_OBJECT_SHOW) PresentSource(window, false);
    } catch (...) { /* Never propagate an exception through Explorer's event dispatcher. */ }
}

extern "C" __declspec(dllexport) LRESULT CALLBACK NativeSourcePresentationCallWndProc(
    int code, WPARAM parameter, LPARAM message) {
    // This global hook can be loaded into other GUI applications. Do not inspect
    // their messages or windows; only system Explorer reaches the message filter.
    if (code >= 0 && CurrentProcessIsExplorer() && message) {
        try {
            const auto& incoming = *reinterpret_cast<const CWPSTRUCT*>(message);
            // A hook delivered on the installing thread for another bitness is
            // not an Explorer source event. Reject its HWND before message data.
            DWORD pid = 0;
            if (!incoming.hwnd || !GetWindowThreadProcessId(incoming.hwnd, &pid) || pid != GetCurrentProcessId())
                return CallNextHookEx(nullptr, code, parameter, message);
            switch (incoming.message) {
            case WM_NCCREATE:
            case WM_CREATE:
                PresentSource(incoming.hwnd, true);
                break;
            case WM_SHOWWINDOW:
                if (incoming.wParam) PresentSource(incoming.hwnd, false);
                break;
            case WM_WINDOWPOSCHANGING:
                if (incoming.lParam && (reinterpret_cast<const WINDOWPOS*>(incoming.lParam)->flags & SWP_SHOWWINDOW))
                    PresentSource(incoming.hwnd, false);
                break;
            case WM_STYLECHANGED:
                if (incoming.wParam == static_cast<WPARAM>(GWL_EXSTYLE) && FindCandidate(incoming.hwnd))
                    PresentSource(incoming.hwnd, false);
                break;
            case WM_NCDESTROY:
                if (const auto candidate = FindCandidate(incoming.hwnd)) {
                    if (candidate->suppressed) KillTimer(incoming.hwnd, TimerId());
                    Erase(incoming.hwnd);
                }
                break;
            default:
                break;
            }
        } catch (...) { /* Never propagate through Explorer's message dispatcher. */ }
    }
    return CallNextHookEx(nullptr, code, parameter, message);
}
