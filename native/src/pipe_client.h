// Named pipe client: the module's end of the channel to BetterConsole.exe.
//
// All pipe I/O happens on one worker thread. The game thread only touches two mutex-protected
// queues (Send appends a line, Receive swaps the inbox out), so a slow or missing app can never
// stall a server frame. Messages are lines of JSON; this class does not look inside them.
#pragma once

#include <atomic>
#include <cstdint>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace bc {

class PipeClient {
public:
    explicit PipeClient(std::wstring pipeName);
    ~PipeClient();

    PipeClient(const PipeClient&) = delete;
    PipeClient& operator=(const PipeClient&) = delete;

    // Queues one message (a newline is added). Returns false when it was dropped because the
    // outgoing buffer is full or nothing is connected.
    bool Send(const char* data, size_t size);

    // Moves every complete line received so far into `out`.
    void Receive(std::vector<std::string>& out);

    bool Connected() const { return connected_.load(std::memory_order_relaxed); }
    uint64_t Dropped() const { return dropped_.load(std::memory_order_relaxed); }
    uint64_t BytesSent() const { return bytesSent_.load(std::memory_order_relaxed); }

private:
    void Run();
    bool Session(void* pipe);

    std::wstring name_;
    std::thread thread_;
    void* stopEvent_ = nullptr;
    void* sendEvent_ = nullptr;

    std::mutex outMutex_;
    std::string outbox_;

    std::mutex inMutex_;
    std::vector<std::string> inbox_;
    std::string partial_;

    std::atomic<bool> connected_{false};
    std::atomic<uint64_t> dropped_{0};
    std::atomic<uint64_t> bytesSent_{0};
};

}  // namespace bc
