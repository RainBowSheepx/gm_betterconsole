// gmsv_betterconsole: the native half of BetterConsole's companion addon.
//
// BetterConsole.exe starts srcds with the environment variable BETTERCONSOLE_PIPE set to the name
// of a named pipe it listens on. This module connects to it and gives Lua:
//
//   betterconsole.Version()                 -> "x.y.z"
//   betterconsole.Enabled()                 -> true when the server was started by BetterConsole
//   betterconsole.Connected()               -> true while the pipe is up
//   betterconsole.Send(json)                -> queue one message (a line of JSON) for the app
//   betterconsole.Receive()                 -> { json, ... } received since the last call, or nil
//   betterconsole.ServerCommand(cmd, now)   -> run a console command through the engine itself
//   betterconsole.NetStats(entIndex)        -> { in, out, pin, pout, ping, loss, choke, fps, address, loopback } | nil
//   betterconsole.ThreadTime()              -> CPU milliseconds the calling thread has used, or nil (not calibrated yet)
//   betterconsole.ConCommands(convar, name) -> { {name, help, flags, cmd}, ... } | nil, error
//   betterconsole.Counters()                -> { sent = bytes, dropped = messages }
//
// Without BETTERCONSOLE_PIPE nothing is started and every call is cheap.

#include <GarrysMod/Lua/Interface.h>

#include <windows.h>
#include <intrin.h>

#include <memory>
#include <string>
#include <vector>

#include "engine.h"
#include "pipe_client.h"

using namespace GarrysMod::Lua;

namespace {

std::unique_ptr<bc::PipeClient> g_pipe;
std::vector<std::string> g_received;

// ---- thread CPU time --------------------------------------------------------------------
// QueryThreadCycleTime counts TSC ticks while the thread runs. The TSC rate is measured against
// QueryPerformanceCounter: first after 0.2 s, refined once more after 3 s.
LARGE_INTEGER g_qpcStart{};
unsigned long long g_tscStart = 0;
double g_tscPerMs = 0;
bool g_tscFinal = false;

void CalibrateTsc() {
    if (g_tscFinal) return;
    LARGE_INTEGER now, freq;
    QueryPerformanceCounter(&now);
    QueryPerformanceFrequency(&freq);
    double ms = double(now.QuadPart - g_qpcStart.QuadPart) * 1000.0 / double(freq.QuadPart);
    if (ms < 200.0) return;
    g_tscPerMs = double(__rdtsc() - g_tscStart) / ms;
    if (ms >= 3000.0) g_tscFinal = true;
}

std::string GetEnv(const wchar_t* name) {
    wchar_t buf[512];
    DWORD n = GetEnvironmentVariableW(name, buf, 512);
    if (n == 0 || n >= 512) return {};
    int len = WideCharToMultiByte(CP_UTF8, 0, buf, int(n), nullptr, 0, nullptr, nullptr);
    std::string s(size_t(len), '\0');
    WideCharToMultiByte(CP_UTF8, 0, buf, int(n), s.data(), len, nullptr, nullptr);
    return s;
}

std::wstring GetEnvW(const wchar_t* name) {
    wchar_t buf[512];
    DWORD n = GetEnvironmentVariableW(name, buf, 512);
    if (n == 0 || n >= 512) return {};
    return std::wstring(buf, n);
}

}  // namespace

LUA_FUNCTION(bc_Version) {
    LUA->PushString(BC_VERSION);
    return 1;
}

LUA_FUNCTION(bc_Enabled) {
    LUA->PushBool(g_pipe != nullptr);
    return 1;
}

LUA_FUNCTION(bc_Connected) {
    LUA->PushBool(g_pipe && g_pipe->Connected());
    return 1;
}

LUA_FUNCTION(bc_Send) {
    unsigned int len = 0;
    const char* s = LUA->CheckString(1);
    s = LUA->GetString(1, &len);
    LUA->PushBool(g_pipe && g_pipe->Send(s, len));
    return 1;
}

LUA_FUNCTION(bc_Receive) {
    if (!g_pipe) return 0;
    g_received.clear();
    g_pipe->Receive(g_received);
    if (g_received.empty()) return 0;
    LUA->CreateTable();
    for (size_t i = 0; i < g_received.size(); ++i) {
        LUA->PushNumber(double(i + 1));
        LUA->PushString(g_received[i].data(), unsigned(g_received[i].size()));
        LUA->SetTable(-3);
    }
    g_received.clear();
    return 1;
}

LUA_FUNCTION(bc_ServerCommand) {
    const char* cmd = LUA->CheckString(1);
    bool now = LUA->IsType(2, Type::Bool) && LUA->GetBool(2);
    std::string err;
    if (bc::Engine::ServerCommand(cmd, now, err)) {
        LUA->PushBool(true);
        return 1;
    }
    LUA->PushBool(false);
    LUA->PushString(err.c_str());
    return 2;
}

LUA_FUNCTION(bc_NetStats) {
    int idx = int(LUA->CheckNumber(1));
    if (idx < 1 || idx > 255) return 0;
    bc::NetChannelStats s;
    if (!bc::Engine::NetStats(idx, s)) return 0;
    LUA->CreateTable();
    LUA->PushNumber(s.inBytes);      LUA->SetField(-2, "in");
    LUA->PushNumber(s.outBytes);     LUA->SetField(-2, "out");
    LUA->PushNumber(s.inPackets);    LUA->SetField(-2, "pin");
    LUA->PushNumber(s.outPackets);   LUA->SetField(-2, "pout");
    LUA->PushNumber(s.latencyMs);    LUA->SetField(-2, "ping");
    LUA->PushNumber(s.lossPercent);  LUA->SetField(-2, "loss");
    LUA->PushNumber(s.chokePercent); LUA->SetField(-2, "choke");
    LUA->PushNumber(s.clientFps);    LUA->SetField(-2, "fps");
    LUA->PushString(s.address.c_str()); LUA->SetField(-2, "address");
    LUA->PushBool(s.loopback);       LUA->SetField(-2, "loopback");
    return 1;
}

LUA_FUNCTION(bc_ThreadTime) {
    CalibrateTsc();
    if (g_tscPerMs <= 0) return 0;
    ULONG64 cycles = 0;
    if (!QueryThreadCycleTime(GetCurrentThread(), &cycles)) return 0;
    LUA->PushNumber(double(cycles) / g_tscPerMs);
    return 1;
}

// Written out by hand (not LUA_FUNCTION) because the raw lua_State is needed for the fallback.
int bc_ConCommands(lua_State* L) {
    ILuaBase* LUA = L->luabase;
    LUA->SetState(L);
    void* cv = LUA->GetUserType<void>(1, Type::ConVar);
    if (!cv) {
        // Some builds keep the ConVar pointer as the first field of a plain userdata.
        static auto touserdata = reinterpret_cast<void* (*)(lua_State*, int)>(
            GetProcAddress(GetModuleHandleA("lua_shared.dll"), "lua_touserdata"));
        void* raw = touserdata ? touserdata(L, 1) : nullptr;
        if (raw) {
            MEMORY_BASIC_INFORMATION mbi{};
            if (VirtualQuery(raw, &mbi, sizeof(mbi)) && mbi.State == MEM_COMMIT) cv = *static_cast<void**>(raw);
        }
    }
    const char* expect = LUA->CheckString(2);
    if (!cv) {
        LUA->PushNil();
        LUA->PushString("argument 1: ConVar expected");
        return 2;
    }
    std::vector<bc::ConCommandInfo> list;
    std::string err;
    if (!bc::Engine::ListConCommands(cv, expect, list, err)) {
        LUA->PushNil();
        LUA->PushString(err.c_str());
        return 2;
    }
    LUA->CreateTable();
    for (size_t i = 0; i < list.size(); ++i) {
        LUA->PushNumber(double(i + 1));
        LUA->CreateTable();
        LUA->PushString(list[i].name.c_str()); LUA->SetField(-2, "name");
        if (!list[i].help.empty()) { LUA->PushString(list[i].help.c_str()); LUA->SetField(-2, "help"); }
        LUA->PushNumber(list[i].flags); LUA->SetField(-2, "flags");
        if (list[i].isCommand >= 0) { LUA->PushBool(list[i].isCommand == 1); LUA->SetField(-2, "cmd"); }
        LUA->SetTable(-3);
    }
    return 1;
}

LUA_FUNCTION(bc_Counters) {
    LUA->CreateTable();
    LUA->PushNumber(g_pipe ? double(g_pipe->BytesSent()) : 0.0); LUA->SetField(-2, "sent");
    LUA->PushNumber(g_pipe ? double(g_pipe->Dropped()) : 0.0);   LUA->SetField(-2, "dropped");
    return 1;
}

GMOD_MODULE_OPEN() {
    QueryPerformanceCounter(&g_qpcStart);
    g_tscStart = __rdtsc();

    std::wstring pipe = GetEnvW(L"BETTERCONSOLE_PIPE");
    if (!pipe.empty()) g_pipe = std::make_unique<bc::PipeClient>(pipe);

    LUA->PushSpecial(SPECIAL_GLOB);
    LUA->CreateTable();
    LUA->PushCFunction(bc_Version);       LUA->SetField(-2, "Version");
    LUA->PushCFunction(bc_Enabled);       LUA->SetField(-2, "Enabled");
    LUA->PushCFunction(bc_Connected);     LUA->SetField(-2, "Connected");
    LUA->PushCFunction(bc_Send);          LUA->SetField(-2, "Send");
    LUA->PushCFunction(bc_Receive);       LUA->SetField(-2, "Receive");
    LUA->PushCFunction(bc_ServerCommand); LUA->SetField(-2, "ServerCommand");
    LUA->PushCFunction(bc_NetStats);      LUA->SetField(-2, "NetStats");
    LUA->PushCFunction(bc_ThreadTime);    LUA->SetField(-2, "ThreadTime");
    LUA->PushCFunction(bc_ConCommands);   LUA->SetField(-2, "ConCommands");
    LUA->PushCFunction(bc_Counters);      LUA->SetField(-2, "Counters");
    LUA->SetField(-2, "betterconsole");
    LUA->Pop();
    return 0;
}

GMOD_MODULE_CLOSE() {
    (void)LUA;
    // Stops and joins the pipe thread; the next Lua state (map change) connects again.
    g_pipe.reset();
    return 0;
}
