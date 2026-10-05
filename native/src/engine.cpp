#include "engine.h"

#include <windows.h>

#include <cstdint>
#include <cstring>

#if defined(_M_X64)
#  define BC_THISCALL
#else
#  define BC_THISCALL __thiscall
#endif

namespace bc {
namespace {

// IVEngineServer (eiface.h, "VEngineServer021")
constexpr int kGetPlayerNetInfo = 20;  // INetChannelInfo* GetPlayerNetInfo(int playerIndex)
constexpr int kServerCommand = 36;     // void ServerCommand(const char* str)
constexpr int kServerExecute = 37;     // void ServerExecute()

// INetChannelInfo (inetchannelinfo.h)
constexpr int kGetAddress = 1;
constexpr int kIsLoopback = 6;
constexpr int kGetAvgLatency = 10;
constexpr int kGetAvgLoss = 11;
constexpr int kGetAvgChoke = 12;
constexpr int kGetAvgData = 13;
constexpr int kGetAvgPackets = 14;
constexpr int kGetRemoteFramerate = 24;
constexpr int FLOW_OUTGOING = 0;
constexpr int FLOW_INCOMING = 1;

const char* const kVersions[] = {"VEngineServer021", "VEngineServer022", "VEngineServer023"};

using CreateInterfaceFn = void* (*)(const char* name, int* returnCode);

void* g_engine = nullptr;

template <typename R, typename... A>
R VCall(void* obj, int slot, A... args) {
    using Fn = R(BC_THISCALL*)(void*, A...);
    return reinterpret_cast<Fn>((*reinterpret_cast<void***>(obj))[slot])(obj, args...);
}

bool IsReadable(const void* p, size_t size) {
    MEMORY_BASIC_INFORMATION mbi{};
    if (!VirtualQuery(p, &mbi, sizeof(mbi))) return false;
    if (mbi.State != MEM_COMMIT) return false;
    if (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) return false;
    auto end = static_cast<const uint8_t*>(mbi.BaseAddress) + mbi.RegionSize;
    return static_cast<const uint8_t*>(p) + size <= end;
}

// ---- ConCommandBase walk ------------------------------------------------------------------
// Layout (convar.h): vtable, m_pNext, m_bRegistered, m_pszName, m_pszHelpString, m_nFlags,
// every field pointer-aligned. The newest registration is the head of the list, so walking from
// a ConVar that Lua has just created reaches everything registered before it.

struct RawEntry {
    const uint8_t* node;
    const char* name;
    const char* help;
    int flags;
};

// Only plain data in here: __try cannot unwind C++ objects.
int WalkRaw(const uint8_t* start, const char* expect, RawEntry* out, int max) {
    const size_t P = sizeof(void*);
    int n = 0;
    __try {
        const char* nm = *reinterpret_cast<const char* const*>(start + 3 * P);
        if (!nm || std::strcmp(nm, expect) != 0) return -1;
        for (const uint8_t* c = start; c && n < max; c = *reinterpret_cast<const uint8_t* const*>(c + P)) {
            out[n].node = c;
            out[n].name = *reinterpret_cast<const char* const*>(c + 3 * P);
            out[n].help = *reinterpret_cast<const char* const*>(c + 4 * P);
            out[n].flags = *reinterpret_cast<const int*>(c + 5 * P);
            if (!out[n].name) break;
            ++n;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return n > 0 ? n : -2;
    }
    return n;
}

// Copies a C string under SEH (the pointer came from the engine's memory).
int CopyString(const char* p, char* buf, int cap) {
    int n = 0;
    __try {
        while (p && n < cap - 1 && p[n]) {
            buf[n] = p[n];
            ++n;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        n = 0;
    }
    buf[n] = 0;
    return n;
}

// ConCommandBase::IsCommand is the second virtual (after the destructor). 1 / 0, -1 on a fault.
using IsCommandFn = bool(BC_THISCALL*)(const void*);
int IsCommand(const uint8_t* node) {
    __try {
        void* const* vt = *reinterpret_cast<void* const* const*>(node);
        if (!vt || !IsReadable(vt, 2 * sizeof(void*)) || !vt[1]) return -1;
        return reinterpret_cast<IsCommandFn>(vt[1])(node) ? 1 : 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -1;
    }
}

}  // namespace

bool Engine::Resolve(std::string& err) {
    if (g_engine) return true;
    HMODULE engine = GetModuleHandleA("engine.dll");
    if (!engine) {
        err = "engine.dll is not loaded";
        return false;
    }
    auto create = reinterpret_cast<CreateInterfaceFn>(GetProcAddress(engine, "CreateInterface"));
    if (!create) {
        err = "engine.dll does not export CreateInterface";
        return false;
    }
    for (const char* v : kVersions) {
        int rc = 0;
        if (void* p = create(v, &rc)) {
            g_engine = p;
            return true;
        }
    }
    err = "IVEngineServer not found";
    return false;
}

bool Engine::ServerCommand(const std::string& command, bool executeNow, std::string& err) {
    if (!Resolve(err)) return false;
    std::string line = command;
    if (line.empty() || line.back() != '\n') line.push_back('\n');
    VCall<void, const char*>(g_engine, kServerCommand, line.c_str());
    if (executeNow) VCall<void>(g_engine, kServerExecute);
    return true;
}

bool Engine::NetStats(int entIndex, NetChannelStats& out) {
    std::string err;
    if (!Resolve(err)) return false;
    void* nci = VCall<void*, int>(g_engine, kGetPlayerNetInfo, entIndex);
    if (!nci) return false;
    out.index = entIndex;
    const char* addr = VCall<const char*>(nci, kGetAddress);
    out.address = addr ? addr : "";
    out.loopback = VCall<bool>(nci, kIsLoopback);
    out.inBytes = VCall<float, int>(nci, kGetAvgData, FLOW_INCOMING);
    out.outBytes = VCall<float, int>(nci, kGetAvgData, FLOW_OUTGOING);
    out.inPackets = VCall<float, int>(nci, kGetAvgPackets, FLOW_INCOMING);
    out.outPackets = VCall<float, int>(nci, kGetAvgPackets, FLOW_OUTGOING);
    out.latencyMs = VCall<float, int>(nci, kGetAvgLatency, FLOW_OUTGOING) * 1000.0f;
    out.lossPercent = VCall<float, int>(nci, kGetAvgLoss, FLOW_INCOMING) * 100.0f;
    out.chokePercent = VCall<float, int>(nci, kGetAvgChoke, FLOW_OUTGOING) * 100.0f;
    float ft = 0, sd = 0;
    VCall<void, float*, float*>(nci, kGetRemoteFramerate, &ft, &sd);
    out.clientFps = ft > 0 ? 1.0f / ft : 0.0f;
    return true;
}

bool Engine::ListConCommands(const void* head, const char* expectName, std::vector<ConCommandInfo>& out, std::string& err) {
    constexpr int kMax = 32768;
    std::vector<RawEntry> raw(kMax);
    int n = WalkRaw(static_cast<const uint8_t*>(head), expectName, raw.data(), kMax);
    if (n == -1) {
        err = "unknown ConCommandBase layout (the probe's name is not where it should be)";
        return false;
    }
    if (n < 0) {
        err = "the console command list could not be read";
        return false;
    }

    // IsCommand is trusted only if it says "variable" for the probe and "command" for "status".
    bool trusted = n > 0 && IsCommand(raw[0].node) == 0;
    std::vector<char> buf(2048);
    out.clear();
    out.reserve(n);
    int known = -1;
    for (int i = 0; i < n; ++i) {
        ConCommandInfo info;
        CopyString(raw[i].name, buf.data(), 256);
        info.name = buf.data();
        if (raw[i].help) {
            CopyString(raw[i].help, buf.data(), int(buf.size()));
            info.help = buf.data();
        }
        info.flags = raw[i].flags;
        if (known < 0 && (info.name == "status" || info.name == "echo")) known = i;
        out.push_back(std::move(info));
    }
    if (trusted) trusted = known >= 0 && IsCommand(raw[known].node) == 1;
    if (trusted) {
        for (int i = 0; i < n; ++i) out[i].isCommand = IsCommand(raw[i].node);
    }
    return true;
}

}  // namespace bc
