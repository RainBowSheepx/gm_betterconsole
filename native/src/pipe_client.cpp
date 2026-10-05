#include "pipe_client.h"

#include <windows.h>

namespace bc {
namespace {

constexpr size_t kMaxOutbox = 16u << 20;  // 16 MB of unsent messages, then new ones are dropped
constexpr size_t kMaxLine = 8u << 20;      // a single incoming line longer than this is discarded
constexpr DWORD kReconnectMs = 1000;

}  // namespace

PipeClient::PipeClient(std::wstring pipeName) : name_(std::move(pipeName)) {
    stopEvent_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    sendEvent_ = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    thread_ = std::thread([this] { Run(); });
}

PipeClient::~PipeClient() {
    SetEvent(stopEvent_);
    if (thread_.joinable()) thread_.join();
    CloseHandle(stopEvent_);
    CloseHandle(sendEvent_);
}

bool PipeClient::Send(const char* data, size_t size) {
    if (!connected_.load(std::memory_order_relaxed)) return false;
    {
        std::lock_guard<std::mutex> lock(outMutex_);
        if (outbox_.size() + size + 1 > kMaxOutbox) {
            dropped_.fetch_add(1, std::memory_order_relaxed);
            return false;
        }
        outbox_.append(data, size);
        outbox_.push_back('\n');
    }
    SetEvent(sendEvent_);
    return true;
}

void PipeClient::Receive(std::vector<std::string>& out) {
    std::lock_guard<std::mutex> lock(inMutex_);
    if (inbox_.empty()) return;
    for (auto& s : inbox_) out.push_back(std::move(s));
    inbox_.clear();
}

void PipeClient::Run() {
    std::wstring path = L"\\\\.\\pipe\\" + name_;
    while (WaitForSingleObject(stopEvent_, 0) != WAIT_OBJECT_0) {
        HANDLE pipe = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
                                  FILE_FLAG_OVERLAPPED, nullptr);
        if (pipe == INVALID_HANDLE_VALUE) {
            if (GetLastError() == ERROR_PIPE_BUSY) WaitNamedPipeW(path.c_str(), 500);
            if (WaitForSingleObject(stopEvent_, kReconnectMs) == WAIT_OBJECT_0) break;
            continue;
        }
        {
            // A new session starts empty: whatever the old connection did not send is stale.
            std::lock_guard<std::mutex> lock(outMutex_);
            outbox_.clear();
        }
        connected_.store(true, std::memory_order_relaxed);
        bool stop = Session(pipe);
        connected_.store(false, std::memory_order_relaxed);
        CancelIoEx(pipe, nullptr);
        CloseHandle(pipe);
        if (stop) break;
        if (WaitForSingleObject(stopEvent_, kReconnectMs) == WAIT_OBJECT_0) break;
    }
}

// Returns true when the module is shutting down, false when the pipe broke.
bool PipeClient::Session(void* pipeHandle) {
    HANDLE pipe = static_cast<HANDLE>(pipeHandle);
    HANDLE readEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HANDLE writeEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    std::vector<char> readBuf(64 * 1024);
    OVERLAPPED readOv{};
    readOv.hEvent = readEvent;
    bool stopping = false;
    bool ok = true;

    auto startRead = [&]() -> bool {
        ResetEvent(readEvent);
        if (!ReadFile(pipe, readBuf.data(), DWORD(readBuf.size()), nullptr, &readOv)) {
            DWORD e = GetLastError();
            if (e != ERROR_IO_PENDING) return false;
        }
        return true;
    };

    auto onBytes = [&](DWORD n) {
        std::lock_guard<std::mutex> lock(inMutex_);
        size_t start = 0;
        for (DWORD i = 0; i < n; ++i) {
            if (readBuf[i] != '\n') continue;
            partial_.append(readBuf.data() + start, i - start);
            if (!partial_.empty()) inbox_.push_back(std::move(partial_));
            partial_.clear();
            start = i + 1;
        }
        partial_.append(readBuf.data() + start, n - start);
        if (partial_.size() > kMaxLine) partial_.clear();
        if (inbox_.size() > 100000) inbox_.erase(inbox_.begin(), inbox_.begin() + 50000);
    };

    if (!startRead()) ok = false;

    std::string sending;
    while (ok) {
        HANDLE waits[3] = {static_cast<HANDLE>(stopEvent_), readEvent, static_cast<HANDLE>(sendEvent_)};
        DWORD w = WaitForMultipleObjects(3, waits, FALSE, 1000);
        if (w == WAIT_OBJECT_0) {
            stopping = true;
            break;
        }
        if (w == WAIT_OBJECT_0 + 1) {
            DWORD n = 0;
            if (!GetOverlappedResult(pipe, &readOv, &n, FALSE)) {
                if (GetLastError() == ERROR_MORE_DATA) {
                    onBytes(n);
                } else {
                    ok = false;
                    break;
                }
            } else {
                onBytes(n);
            }
            if (!startRead()) {
                ok = false;
                break;
            }
            continue;
        }
        // Send event or timeout: flush the outbox.
        {
            std::lock_guard<std::mutex> lock(outMutex_);
            sending.swap(outbox_);
            outbox_.clear();
        }
        size_t off = 0;
        while (off < sending.size()) {
            OVERLAPPED wov{};
            wov.hEvent = writeEvent;
            ResetEvent(writeEvent);
            DWORD chunk = DWORD(std::min<size_t>(sending.size() - off, 1u << 20));
            DWORD written = 0;
            if (!WriteFile(pipe, sending.data() + off, chunk, nullptr, &wov)) {
                if (GetLastError() != ERROR_IO_PENDING) {
                    ok = false;
                    break;
                }
                HANDLE ws[2] = {static_cast<HANDLE>(stopEvent_), writeEvent};
                DWORD r = WaitForMultipleObjects(2, ws, FALSE, INFINITE);
                if (r == WAIT_OBJECT_0) {
                    CancelIoEx(pipe, &wov);
                    GetOverlappedResult(pipe, &wov, &written, TRUE);
                    stopping = true;
                    ok = false;
                    break;
                }
            }
            if (!GetOverlappedResult(pipe, &wov, &written, TRUE)) {
                ok = false;
                break;
            }
            off += written;
            bytesSent_.fetch_add(written, std::memory_order_relaxed);
        }
        sending.clear();
    }

    CancelIoEx(pipe, &readOv);
    DWORD dummy = 0;
    GetOverlappedResult(pipe, &readOv, &dummy, TRUE);
    CloseHandle(readEvent);
    CloseHandle(writeEvent);
    {
        std::lock_guard<std::mutex> lock(inMutex_);
        partial_.clear();
    }
    return stopping;
}

}  // namespace bc
