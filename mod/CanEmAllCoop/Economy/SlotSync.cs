using System;
using CanEmAllCoop.Core;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace CanEmAllCoop.Economy;
using CanEmAllCoop.Net;
using Net = CanEmAllCoop.Net.Net;

/// <summary>
/// Slot machine. The host is the only peer that rolls: a spin (the host's own click or a friend's request) runs
/// the real SlotMachine.Spin on the host, whose GenerateResult is captured and broadcast. Every client then runs
/// the real Spin too (same animation, sounds, result screen) with that forced result and a pre-stake balance, so
/// no gameplay RNG or payment happens on a client. The host's IsSpinning is the session-wide lock.
/// </summary>
internal static class SlotSync
{
    const int SpinKey = 300000;
    const long ReplayTimeoutMs = 40000;
    const long HostPendingTimeoutMs = 60000;

    static readonly RequestGate Gate = new();

    // ---- host
    static bool _hostPending;       // a spin started by us is in flight (suppresses wallet traffic until it ends)
    static bool _hostStarted;
    static bool _hostSent;
    static long _hostBefore;
    static long _hostBet;
    static int _hostSpinner;
    static int[] _hostResult;
    static long _hostPendingSince;
    static bool _hostAuthorized;

    // ---- client
    static int[] _forced;
    static bool _replaySawSpinning;
    static long _replayStart;

    public static bool ReplayActive { get; private set; }
    public static bool HostBusy => _hostPending;
    public static string State => Net.IsHost ? (_hostPending ? "host-spin" : "idle") : ReplayActive ? "replay" : "idle";

    public static void Reset()
    {
        Gate.Clear();
        _hostPending = _hostStarted = _hostSent = _hostAuthorized = false;
        _hostResult = null;
        _forced = null;
        EndReplay();
    }

    // ------------------------------------------------------------------------------------------------ host

    /// <summary>Spin prefix on the host (own click or authorized remote request): clamp the stake and remember the balance.</summary>
    public static void HostBeforeSpin(SlotMachine slot)
    {
        if (slot.IsSpinning) return; // the original ignores it as well
        var save = GameAccess.Save;
        if (save == null) return;

        long money = save.money;
        long cap = EconomyRules.MaxBet(money, CoopConfig.MaxBetFraction);
        var input = slot.moneyInputField;
        if (input != null && cap > 0 && input.Value > cap)
        {
            input.SetValue(cap);
            Log.Info($"slot: stake clamped to {cap} (MaxBetFraction {CoopConfig.MaxBetFraction})");
        }

        _hostBefore = money;
        _hostPending = true;
        _hostStarted = _hostSent = false;
        _hostResult = null;
        _hostPendingSince = GameAccess.NowMs;
        if (!_hostAuthorized) _hostSpinner = Net.LocalPeer.Index;
    }

    public static void HostAfterSpin(SlotMachine slot)
    {
        if (!_hostPending || _hostStarted) return;
        if (!slot.IsSpinning) { ClearHost(); return; } // rejected by the game (no / too high stake)
        var save = GameAccess.Save;
        _hostBet = save != null ? _hostBefore - save.money : 0;
        if (_hostBet <= 0) _hostBet = slot.moneyInputField != null ? slot.moneyInputField.Value : 0;
        _hostStarted = true;
        TrySendGo();
    }

    public static void HostGenerated(Il2CppStructArray<int> result)
    {
        if (!_hostPending || result == null || result.Length < 3) return;
        _hostResult = new[] { result[0], result[1], result[2] };
        TrySendGo();
    }

    static void TrySendGo()
    {
        if (!_hostStarted || _hostResult == null || _hostSent) return;
        _hostSent = true;
        var msg = new SpinGoMsg
        {
            Spinner = _hostSpinner, Bet = _hostBet, Before = _hostBefore,
            R0 = _hostResult[0], R1 = _hostResult[1], R2 = _hostResult[2],
        };
        Net.Broadcast(EconMsg.SpinGo, w => msg.Write(w));
    }

    public static void OnSpinReq(Peer from, NetReader r)
    {
        if (!Net.IsHost) return;
        long bet = r.ReadLong();
        var slot = SlotMachine.Instance;
        if (slot == null || slot.IsSpinning || _hostPending) { RejectSpin(from, RejectReason.Busy); return; }

        var save = GameAccess.Save;
        long cap = save != null ? EconomyRules.MaxBet(save.money, CoopConfig.MaxBetFraction) : 0;
        long stake = Math.Min(bet, cap);
        if (stake <= 0) { RejectSpin(from, RejectReason.NoMoney); return; }

        _hostAuthorized = true;
        _hostSpinner = from.Index;
        try
        {
            slot.moneyInputField.SetValue(stake);
            slot.Spin();
        }
        catch (Exception e) { Log.Error("slot: remote spin failed", e); ClearHost(); }
        finally { _hostAuthorized = false; }
        if (!_hostPending) RejectSpin(from, RejectReason.NoMoney);
    }

    static void RejectSpin(Peer to, RejectReason reason)
    {
        Net.SendTo(to, EconMsg.Rejected, w => { w.Write((byte)RejectKind.Slot); w.Write(0); w.Write((byte)reason); });
    }

    static void ClearHost()
    {
        _hostPending = _hostStarted = _hostSent = false;
        _hostResult = null;
    }

    // ---------------------------------------------------------------------------------------------- client

    /// <summary>Spin prefix on a client. Returns true when the original may run.</summary>
    public static bool ClientSpinClicked(SlotMachine slot)
    {
        if (ReplayActive) return true;         // our own replay
        if (slot.IsSpinning) return false;     // someone's spin is in progress: the lock is session-wide
        var save = GameAccess.Save;
        long bet = slot.moneyInputField != null ? slot.moneyInputField.Value : 0;
        if (save == null || bet <= 0 || bet > save.money) return true; // the game's own "invalid stake" feedback, nothing is paid
        if (!Gate.TryEnter(SpinKey, GameAccess.NowMs, 4000)) return false;
        Net.SendToHost(EconMsg.SpinReq, w => w.Write(bet));
        return false;
    }

    public static void OnRejected()
    {
        Gate.Release(SpinKey);
        ShopSync.Guard("error clip", () => UISound.Instance?.PlayErrorClip());
    }

    public static void OnSpinGo(Peer from, NetReader r)
    {
        if (!Net.IsClient) return;
        var m = SpinGoMsg.Read(r);
        Gate.Release(SpinKey);
        var slot = SlotMachine.Instance;
        var mm = GameAccess.Money;
        if (slot == null || mm == null) return;
        if (slot.IsSpinning || ReplayActive)
        {
            Log.Warn("slot: SpinGo while a spin is already running; ignored (wallet resync will fix the balance)");
            return;
        }

        ReplayActive = true;
        _replayStart = GameAccess.NowMs;
        _replaySawSpinning = false;
        _forced = new[] { m.R0, m.R1, m.R2 };
        try
        {
            using (new ApplyScope())
            {
                mm.ChangeMoney(m.Before); // the original Spin deducts the stake again and checks stake <= balance
                slot.moneyInputField.SetValue(m.Bet);
                slot.Spin();
            }
            if (slot.IsSpinning) _replaySawSpinning = true;
            else EndReplay();
        }
        catch (Exception e)
        {
            Log.Error("slot: replay failed", e);
            EndReplay();
        }
    }

    /// <summary>GenerateResult prefix: on a client the host's reels are used instead of a local roll.</summary>
    public static bool TryForcedResult(out Il2CppStructArray<int> result)
    {
        result = null;
        if (!ReplayActive || _forced == null) return false;
        result = new Il2CppStructArray<int>(3);
        for (int i = 0; i < 3; i++) result[i] = _forced[i];
        _forced = null;
        return true;
    }

    static void EndReplay()
    {
        if (!ReplayActive) return;
        ReplayActive = false;
        _forced = null;
        WalletSync.ReleasePending();
    }

    // ------------------------------------------------------------------------------------------------ both

    public static void Update()
    {
        var slot = SlotMachine.Instance;
        long now = GameAccess.NowMs;

        if (_hostPending)
        {
            bool over = slot == null || (_hostSent && !slot.IsSpinning) || now - _hostPendingSince > HostPendingTimeoutMs;
            if (over)
            {
                ClearHost();
                WalletSync.HostFlushNow();
            }
        }

        if (ReplayActive)
        {
            if (slot != null && slot.IsSpinning) _replaySawSpinning = true;
            bool over = slot == null || (_replaySawSpinning && !slot.IsSpinning) || now - _replayStart > ReplayTimeoutMs;
            if (over) EndReplay();
        }
    }
}
