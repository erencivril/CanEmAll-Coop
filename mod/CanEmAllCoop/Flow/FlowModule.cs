using CanEmAllCoop.Core;
using HarmonyLib;

namespace CanEmAllCoop.Flow;
using Net = CanEmAllCoop.Net.Net;

/// <summary>Day flow, ready-up / elevator quorums, death and respawn, join path and save guard (plan section 2.3 "Flow", F1-F5).</summary>
public sealed class FlowModule : ICoopModule
{
    public string Name => "Flow";

    public void Init(Harmony harmony)
    {
        try
        {
            Net.Register(FlowMsg.StartRequest, FlowCore.OnStartRequest);
            Net.Register(FlowMsg.HostStart, FlowCore.OnHostStart);
            Net.Register(FlowMsg.InGame, FlowCore.OnInGame);
            Net.Register(FlowMsg.Timer, FlowCore.OnTimer);
            Net.Register(FlowMsg.FlowState, FlowCore.OnFlowState);
            Net.Register(FlowMsg.BarrierReport, FlowCore.OnBarrierReport);
            Net.Register(FlowMsg.BarrierGo, FlowCore.OnBarrierGo);
            Net.Register(FlowMsg.BarrierStatus, FlowCore.OnBarrierStatus);
            Net.Register(FlowMsg.PlayerDown, FlowCore.OnPlayerDown);
            Net.Register(FlowMsg.PlayerUp, FlowCore.OnPlayerUp);
            Net.Register(FlowMsg.TeamWipe, FlowCore.OnTeamWipe);
            Net.Register(FlowMsg.DownList, FlowCore.OnDownList);
        }
        catch (System.Exception e) { Log.Error("Flow: registering the message handlers failed", e); }
        Net.Notice += FlowCore.ShowToast;

        // cross-module hooks (read live by scaling / avatar)
        CanEmAllCoop.Scaling.Pace.IsDayRunning = () => FlowCore.Phase is FlowPhase.Day or FlowPhase.Night or FlowPhase.DayEnding;
        CanEmAllCoop.Avatar.AvatarModule.LocalDownProvider = () => FlowCore.IsDown;
        CanEmAllCoop.UI.CoopMenu.InGameplay = FlowCore.IsLocalGameplay;

        try { FlowPatches.Apply(harmony); }
        catch (System.Exception e) { Log.Error("Flow: applying the patches failed", e); }
    }

    public void OnUpdate() => FlowCore.Update();
    public void OnSessionStart() => FlowCore.OnSessionStart();
    public void OnSessionEnd() => FlowCore.OnSessionEnd();
    public void OnPeerJoined(Peer peer) => FlowCore.OnPeerJoined(peer);
    public void OnPeerLeft(Peer peer) => FlowCore.OnPeerLeft(peer);
    public string DebugState() => FlowCore.DebugState();
}
