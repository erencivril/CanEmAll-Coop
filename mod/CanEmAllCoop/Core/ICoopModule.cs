using HarmonyLib;

namespace CanEmAllCoop.Core;

public interface ICoopModule
{
    string Name { get; }
    void Init(Harmony harmony);   // apply patches; catch + log failures, never throw
    void OnUpdate();              // every frame (main thread), also when offline
    void OnSessionStart();        // after Role becomes Host/Client
    void OnSessionEnd();          // back to Offline; restore vanilla state
    void OnPeerJoined(Peer peer); // host side: send full state of this module to the new peer
    void OnPeerLeft(Peer peer);
    string DebugState();          // one line "key=value ..." for the [STATE] log probe
}
