// Direct calls into the Source engine, below Garry's Mod's Lua layer.
//
//  * Console commands go through IVEngineServer::ServerCommand / ServerExecute. Lua's
//    game.ConsoleCommand refuses a list of commands ("game.ConsoleCommand blocked!"); the operator
//    typing into BetterConsole should not be limited by that.
//  * Per-client network numbers come from IVEngineServer::GetPlayerNetInfo -> INetChannelInfo
//    (the same numbers the engine's "stats" and "net_graph" use). Lua has no access to them.
//  * The list of every console command and variable (for auto-completion) is the engine's linked
//    list of ConCommandBase objects.
//
// Interface layouts are from the Source SDK 2013 headers that match GMod (eiface.h,
// inetchannelinfo.h, convar.h). The vtable slots are the same on both GMod branches and both
// bitnesses (VEngineServer021).
#pragma once

#include <string>
#include <vector>

namespace bc {

struct NetChannelStats {
    int index = 0;           // entity index of the player
    float inBytes = 0;       // bytes/s received from the client
    float outBytes = 0;      // bytes/s sent to the client
    float inPackets = 0;
    float outPackets = 0;
    float latencyMs = 0;     // average round trip of outgoing packets
    float lossPercent = 0;   // incoming packet loss
    float chokePercent = 0;  // outgoing choke
    float clientFps = 0;     // remote frame rate the client reports
    bool loopback = false;
    std::string address;
};

struct ConCommandInfo {
    std::string name;
    std::string help;
    int flags = 0;
    int isCommand = -1;  // 1 command, 0 variable, -1 unknown
};

class Engine {
public:
    // Finds IVEngineServer in engine.dll. Never calls a virtual function. Idempotent.
    static bool Resolve(std::string& err);

    // Must be called on the game thread.
    static bool ServerCommand(const std::string& command, bool executeNow, std::string& err);

    // GetPlayerNetInfo for one entity index. Returns false when the player has no channel (bots).
    static bool NetStats(int entIndex, NetChannelStats& out);

    // Walks the ConCommandBase list starting at `head` (a ConVar*), checking first that its name is
    // `expectName`. Returns false + err when the layout does not match.
    static bool ListConCommands(const void* head, const char* expectName, std::vector<ConCommandInfo>& out, std::string& err);
};

}  // namespace bc
