using System;
using CanEmAllCoop.Core;
using CanEmAllCoop.UI;
using HarmonyLib;

namespace CanEmAllCoop.Scaling;
using Net = CanEmAllCoop.Net.Net;

public sealed class ScalingModule : ICoopModule
{
    bool _lastBroadcastValid;
    int _lastN, _lastEff, _lastBase;

    public string Name => "Scaling";

    public void Init(Harmony harmony)
    {
        CoopUI.PaceText = Pace.Text;
        Net.Register(Pace.MsgState, OnState);
        Pace.Changed += BroadcastIfChanged;
        Flow.FlowApi.PhaseChanged += DayTelemetry.OnPhase;
        ScalingPatches.Apply(harmony);
    }

    public void OnUpdate()
    {
        // N is latched by flow at the start of every day (Pace.Latch) and by nothing else: a join or leave never changes
        // the pace of a running day, nor of the shop in between.
        ScalingConfig.Ensure();   // binds the module's config entries early (rate limited, no-op once bound) so they show in the .cfg file
        Pace.PollMode();
        DayTelemetry.Update();
        ShopPrices.Update();
    }

    public void OnSessionStart()
    {
        _lastBroadcastValid = false;
        if (Net.IsHost) Pace.Latch();
    }

    public void OnSessionEnd()
    {
        _lastBroadcastValid = false;
        DayTelemetry.End("session end");
        Pace.Reset();
        ShopPrices.Restore();
    }

    public void OnPeerJoined(Peer peer)
    {
        if (!Net.IsHost) return;
        Net.SendTo(peer, Pace.MsgState, Pace.Write);
    }

    public void OnPeerLeft(Peer peer) { }

    public string DebugState() =>
        $"N={Pace.N} players={Net.PlayerCount} mode={CoopConfig.PaceMode} eBase={Pace.EBase} eEff={Pace.EEff} k={Pace.K:0.000} " +
        $"maxEnemyCount={OrigMaxText()} scaled={(Pace.ScaledTruck ? 1 : 0)} disabled={(Pace.Disabled ? 1 : 0)} " +
        $"moneyIn={ScalingPatches.MoneyIn} moneyOut={ScalingPatches.MoneyOut} pricesScaled={ShopPrices.Scaled} next={Pace.PendingN} " + DayTelemetry.DebugState();

    static string OrigMaxText()
    {
        try
        {
            var t = Truck.Instance;
            return t == null ? "?" : $"{t.maxEnemyCount}/{Pace.OrigMax}";
        }
        catch (Exception) { return "?"; }
    }

    // Host: tell everyone when N / E_eff changed (UI only; clients never scale anything themselves).
    void BroadcastIfChanged()
    {
        if (!Net.IsHost) return;
        if (_lastBroadcastValid && _lastN == Pace.N && _lastEff == Pace.EEff && _lastBase == Pace.EBase) return;
        _lastBroadcastValid = true;
        _lastN = Pace.N;
        _lastEff = Pace.EEff;
        _lastBase = Pace.EBase;
        Net.Broadcast(Pace.MsgState, Pace.Write);
    }

    static void OnState(Peer from, CanEmAllCoop.Net.NetReader r)
    {
        if (Net.IsHost || !from.IsHost) return; // only the host's state is authoritative
        Pace.ReadClient(r);
    }
}
