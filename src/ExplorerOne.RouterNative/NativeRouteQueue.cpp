#include "NativeRouteQueue.h"
#include <objbase.h>
#include <process.h>
#include <wrl/client.h>
#include <atomic>
#include <deque>
#include <memory>
#include <mutex>
#include <new>

using Microsoft::WRL::ComPtr;
namespace explorerone {
namespace {
constexpr DWORD MaximumPending = 64;
constexpr DWORD CompletionTimeout = 12000;
enum : LONG { Pending = 0, Executing = 1, Complete = 2, Cancelled = 3 };
struct Handle final {
    HANDLE value = nullptr;
    ~Handle() { if (value) CloseHandle(value); }
};
struct Job final {
    RoutingTarget target{};
    PIDLIST_ABSOLUTE folder = nullptr;
    Handle controller, completed;
    ComPtr<IStream> handshake, site;
    std::atomic<LONG> state{ Pending };
    RouteResult result{ false, E_PENDING };
    ~Job() {
        // A stream still present here was rejected before publication and is
        // destroyed in its creating apartment. Published jobs are unmarshaled
        // (including cancelled jobs) by the worker before their last release.
        if (handshake) CoReleaseMarshalData(handshake.Get());
        if (site) CoReleaseMarshalData(site.Get());
        CoTaskMemFree(folder);
    }
};
struct Queue final {
    Handle work, stop, ready, thread;
    std::mutex mutex;
    std::deque<std::shared_ptr<Job>> jobs;
    std::atomic<DWORD> pending{};
    std::atomic<DWORD> threadId{};
    std::atomic<HRESULT> initialized{ E_PENDING };
    std::atomic<bool> accepting{};
    DWORD shellThread = 0;
    RouteQueueCompletion completion = nullptr;
};
std::atomic<std::shared_ptr<Queue>> g_queue;
thread_local bool g_onQueueThread = false;

HRESULT LastErrorResult() noexcept {
    const DWORD error = GetLastError();
    return HRESULT_FROM_WIN32(error ? error : ERROR_GEN_FAILURE);
}
void DispatchMessages() noexcept {
    MSG message{};
    while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
        if (message.message == WM_QUIT) continue;
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
}
void ExecuteJob(Queue& queue, const std::shared_ptr<Job>& job) noexcept {
    ComPtr<IUnknown> handshake, site;
    HRESULT unmarshaled = S_OK;
    if (job->handshake) {
        // Consume even a cancelled job's marshaling packet so retained COM
        // references are released in the receiving apartment.
        unmarshaled = CoGetInterfaceAndReleaseStream(job->handshake.Detach(), IID_PPV_ARGS(&handshake));
    }
    if (job->site) {
        const HRESULT siteResult = CoGetInterfaceAndReleaseStream(job->site.Detach(), IID_PPV_ARGS(&site));
        if (SUCCEEDED(unmarshaled)) unmarshaled = siteResult;
    }
    LONG expected = Pending;
    if (!job->state.compare_exchange_strong(expected, Executing)) {
        queue.pending.fetch_sub(1);
        return;
    }
    RouteResult result{ false, HRESULT_FROM_WIN32(ERROR_CANCELLED) };
    if (WaitForSingleObject(job->controller.value, 0) == WAIT_TIMEOUT) {
        result = SUCCEEDED(unmarshaled) ? TryRoutePidlToTab(job->target, job->folder, handshake.Get(), site.Get()) : RouteResult{ false, unmarshaled };
    }
    job->result = result;
    if (queue.completion) queue.completion(result);
    job->state.store(Complete);
    SetEvent(job->completed.value);
    queue.pending.fetch_sub(1);
}
unsigned __stdcall Worker(void* parameter) noexcept {
    // The creator retains the Queue until this thread handle signals.
    auto& queue = *static_cast<Queue*>(parameter);
    g_onQueueThread = true;
    queue.threadId = GetCurrentThreadId();
    const HRESULT initialized = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);
    queue.initialized = initialized;
    // Create the STA message queue before reporting readiness.
    MSG message{};
    PeekMessageW(&message, nullptr, WM_USER, WM_USER, PM_NOREMOVE);
    SetEvent(queue.ready.value);
    if (FAILED(initialized)) return 0;
    const HANDLE events[] = { queue.work.value, queue.stop.value };
    for (;;) {
        std::shared_ptr<Job> job;
        {
            const std::lock_guard lock(queue.mutex);
            if (!queue.jobs.empty()) {
                job = std::move(queue.jobs.front());
                queue.jobs.pop_front();
            }
            if (queue.jobs.empty()) ResetEvent(queue.work.value);
        }
        if (job) {
            ExecuteJob(queue, job);
            DispatchMessages();
            continue;
        }
        if (WaitForSingleObject(queue.stop.value, 0) == WAIT_OBJECT_0) break;
        const DWORD waited = MsgWaitForMultipleObjectsEx(_countof(events), events, INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
        if (waited == WAIT_OBJECT_0 + _countof(events)) DispatchMessages();
        else if (waited == WAIT_FAILED) break;
    }
    CoUninitialize();
    g_onQueueThread = false;
    return 0;
}
HRESULT WaitForCompletion(HANDLE completed) noexcept {
    DWORD index = 0;
    const HRESULT waited = CoWaitForMultipleHandles(COWAIT_DISPATCH_CALLS | COWAIT_DISPATCH_WINDOW_MESSAGES,
        CompletionTimeout, 1, &completed, &index);
    if (waited == CO_E_NOTINITIALIZED) {
        const DWORD nativeWait = WaitForSingleObject(completed, CompletionTimeout);
        if (nativeWait == WAIT_OBJECT_0) return S_OK;
        return nativeWait == WAIT_TIMEOUT ? HRESULT_FROM_WIN32(WAIT_TIMEOUT) : LastErrorResult();
    }
    return waited;
}
} // namespace

HRESULT StartNativeRouteQueue(DWORD shellThread, RouteQueueCompletion completion) noexcept {
    try {
        const auto existing = g_queue.load();
        if (existing) return existing->accepting.load() ? S_OK : HRESULT_FROM_WIN32(ERROR_BUSY);
        auto queue = std::make_shared<Queue>();
        queue->shellThread = shellThread;
        queue->completion = completion;
        queue->work.value = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        queue->stop.value = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        queue->ready.value = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!queue->work.value || !queue->stop.value || !queue->ready.value) return LastErrorResult();
        queue->thread.value = reinterpret_cast<HANDLE>(_beginthreadex(nullptr, 0, Worker, queue.get(), 0, nullptr));
        if (!queue->thread.value) return E_OUTOFMEMORY;
        // Readiness requires only CoInitializeEx and creating a message queue;
        // the worker does no cross-apartment COM work before this acknowledgement.
        g_queue.store(queue);
        if (WaitForSingleObject(queue->ready.value, 3000) != WAIT_OBJECT_0) {
            SetEvent(queue->stop.value);
            return HRESULT_FROM_WIN32(WAIT_TIMEOUT);
        }
        const HRESULT initialized = queue->initialized.load();
        if (FAILED(initialized)) {
            SetEvent(queue->stop.value);
            return initialized;
        }
        queue->accepting = true;
        return S_OK;
    } catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
    catch (...) { return E_UNEXPECTED; }
}
RouteResult RoutePidlOnSta(const RoutingTarget& target, PCIDLIST_ABSOLUTE folder,
    IUnknown* handshake, IUnknown* site, HANDLE controllerProcess) noexcept {
    try {
        const auto queue = g_queue.load();
        if (!queue || !queue->accepting.load() || !folder || !controllerProcess || g_onQueueThread)
            return { false, HRESULT_FROM_WIN32(ERROR_INVALID_STATE) };
        // Waiting here would block the very STA needed by the browser proxy.
        // Until asynchronous native fallback has a proven ownership contract,
        // retain the original launch path for shell-thread ingress.
        const DWORD currentThread = GetCurrentThreadId();
        if (currentThread == queue->shellThread || currentThread == GetWindowThreadProcessId(target.root, nullptr))
            return { false, RPC_E_CANTCALLOUT_ININPUTSYNCCALL };
        auto job = std::make_shared<Job>();
        job->target = target;
        job->folder = ILCloneFull(folder);
        job->completed.value = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!job->folder || !job->completed.value) return { false, E_OUTOFMEMORY };
        if (!DuplicateHandle(GetCurrentProcess(), controllerProcess, GetCurrentProcess(), &job->controller.value,
            SYNCHRONIZE, FALSE, 0)) return { false, LastErrorResult() };
        if (handshake) {
            const HRESULT marshaled = CoMarshalInterThreadInterfaceInStream(IID_IUnknown, handshake, &job->handshake);
            if (FAILED(marshaled)) return { false, marshaled };
        }
        if (site) {
            const HRESULT marshaled = CoMarshalInterThreadInterfaceInStream(IID_IUnknown, site, &job->site);
            if (FAILED(marshaled)) return { false, marshaled };
        }
        {
            const std::lock_guard lock(queue->mutex);
            if (!queue->accepting.load() || queue->pending.load() >= MaximumPending)
                return { false, HRESULT_FROM_WIN32(ERROR_BUSY) };
            queue->jobs.push_back(job);
            queue->pending.fetch_add(1);
            SetEvent(queue->work.value);
        }
        const HRESULT waited = WaitForCompletion(job->completed.value);
        if (SUCCEEDED(waited)) return job->result;
        LONG expected = Pending;
        if (job->state.compare_exchange_strong(expected, Cancelled)) {
            // A queued job can be cancelled safely: it never crossed dispatch.
            return { false, waited };
        }
        if (expected == Complete) return job->result;
        // A running job may already have posted a tab. Its retained data and
        // handshake stay owned by the worker; never launch a duplicate fallback.
        return { true, waited };
    } catch (const std::bad_alloc&) { return { false, E_OUTOFMEMORY }; }
    catch (...) { return { false, E_UNEXPECTED }; }
}
bool StopNativeRouteQueue() noexcept {
    const auto queue = g_queue.load();
    if (!queue) return true;
    {
        const std::lock_guard lock(queue->mutex);
        queue->accepting = false;
    }
    SetEvent(queue->stop.value);
    if (queue->pending.load() || WaitForSingleObject(queue->thread.value, 0) != WAIT_OBJECT_0) return false;
    g_queue.store({});
    return true;
}
DWORD NativeRouteQueuePending() noexcept {
    const auto queue = g_queue.load();
    return queue ? queue->pending.load() : 0;
}
bool IsNativeRouteQueueThread() noexcept { return g_onQueueThread; }
} // namespace explorerone
