using System;
using CanEmAllCoop.Core;
using HarmonyLib;

namespace CanEmAllCoop.Economy;
using CanEmAllCoop.Net;
using Net = CanEmAllCoop.Net.Net;

public sealed class EconomyModule : ICoopModule
{
    const long SyncIntervalMs = 15000;
    const long ResyncCooldownMs = 5000;

    static readonly Type[] PatchClasses =
    {
        typeof(AddMoneyPatch), typeof(ChangeMoneyPatch), typeof(SetMoneyPatch),
        typeof(SkillNodeClickPatch), typeof(PermanentUpgradeClickPatch), typeof(SellPatch),
        typeof(SkillPopUpNodePatch), typeof(SkillPopUpPermanentPatch),
        typeof(SlotSpinPatch), typeof(SlotGenerateResultPatch),
    };

    // a snapshot is a full JsonUtility.ToJson of the save: one per peer per 3 s is plenty for a repair request
    static readonly RequestGate SnapGate = new();

    long _nextSyncMs;
    long _lastResyncMs = long.MinValue / 2;
    bool _resyncRequested;
    int _applied, _failed;

    public string Name => "Economy";

    public void Init(Harmony harmony)
    {
        try
        {
            Net.Register(EconMsg.Wallet, WalletSync.OnWallet);
            Net.Register(EconMsg.BuyReq, ShopSync.OnBuyReq);
            Net.Register(EconMsg.Purchased, ShopSync.OnPurchased);
            Net.Register(EconMsg.Rejected, ShopSync.OnRejected);
            Net.Register(EconMsg.PermReq, ShopSync.OnPermReq);
            Net.Register(EconMsg.PermPurchased, ShopSync.OnPermanentPurchased);
            Net.Register(EconMsg.SellReq, ShopSync.OnSellReq);
            Net.Register(EconMsg.Sold, ShopSync.OnSold);
            Net.Register(EconMsg.SpinReq, SlotSync.OnSpinReq);
            Net.Register(EconMsg.SpinGo, SlotSync.OnSpinGo);
            Net.Register(EconMsg.Snapshot, OnSnapshot);
            Net.Register(EconMsg.SnapReq, OnSnapReq);
            Net.Register(EconMsg.Sync, OnSync);
        }
        catch (Exception e) { Log.Error("Economy: message registration failed", e); }

        foreach (var type in PatchClasses)
        {
            try
            {
                harmony.CreateClassProcessor(type).Patch();
                _applied++;
            }
            catch (Exception e)
            {
                _failed++;
                Log.Error($"Economy: patch {type.Name} failed to apply; that part stays vanilla", e);
            }
        }
        Log.Info($"Economy: {_applied}/{PatchClasses.Length} patch classes applied");
    }

    public void OnUpdate()
    {
        if (!Net.IsOnline) return;
        WalletSync.HostUpdate();
        SlotSync.Update();
        ShopSync.UpdateInbox();

        if (Net.IsHost && GameAccess.NowMs >= _nextSyncMs)
        {
            _nextSyncMs = GameAccess.NowMs + SyncIntervalMs;
            BroadcastSync();
        }
    }

    public void OnSessionStart()
    {
        ResetAll();
        _nextSyncMs = GameAccess.NowMs + 2000;
    }

    public void OnSessionEnd() => ResetAll();

    public void OnPeerJoined(Peer peer)
    {
        if (!Net.IsHost) return; // the event fires on every machine for every remote peer
        WalletSync.HostSendTo(peer);
        string json = EconomySnapshot.BuildSnapshotJson();
        Net.SendTo(peer, EconMsg.Snapshot, w => w.Write(json));
    }

    public void OnPeerLeft(Peer peer) { }

    public string DebugState()
    {
        GameAccess.TryReadWallet(out var money, out var total);
        var save = GameAccess.Save;
        return $"patches={_applied}/{PatchClasses.Length} money={money} total={total} skills={save?.unlockedSkills?.Count ?? -1} " +
               $"perms={save?.unlockedPermanentUpgrades?.Count ?? -1} points={save?.upgradePoints ?? -1} " +
               $"lastWallet={WalletSync.LastSent} slot={SlotSync.State}";
    }

    void ResetAll()
    {
        SnapGate.Clear();
        WalletSync.Reset();
        ShopSync.Reset();
        SlotSync.Reset();
        _resyncRequested = false;
        EconomySnapshot.HasAppliedSnapshot = false;
        EconomySnapshot.LastReceivedJson = null;
    }

    // ---------------------------------------------------------------------------------------- snapshot / resync

    /// <summary>Host: push the compact progression summary now (flow calls this at day start; also sent every 15 s).</summary>
    public static void BroadcastSync()
    {
        if (!Net.IsHost) return;
        var save = GameAccess.Save;
        if (save == null) return;
        var msg = new SyncMsg
        {
            Points = save.upgradePoints, Day = save.day,
            Skills = GameAccess.ToArray(save.unlockedSkills), Perms = GameAccess.ToArray(save.unlockedPermanentUpgrades),
        };
        Net.Broadcast(EconMsg.Sync, w => msg.Write(w));
    }

    void OnSync(Peer from, NetReader r)
    {
        if (!Net.IsClient || !EconomySnapshot.HasAppliedSnapshot) return;
        if (ShopSync.InboxBusy) return;   // purchases are still being applied: the lists lag on purpose, the next sync (15 s) compares again
        var m = SyncMsg.Read(r);
        var save = GameAccess.Save;
        if (save == null) return;

        bool same = EconomyRules.SameSequence(m.Skills, GameAccess.ToArray(save.unlockedSkills))
                    && EconomyRules.SameSequence(m.Perms, GameAccess.ToArray(save.unlockedPermanentUpgrades));
        if (!same)
        {
            long now = GameAccess.NowMs;
            if (now - _lastResyncMs < ResyncCooldownMs) return;
            _lastResyncMs = now;
            _resyncRequested = true;
            Log.Warn("Economy: unlock lists differ from the host's; requesting a full snapshot");
            Net.SendToHost(EconMsg.SnapReq, _ => { });
            return;
        }

        if (save.upgradePoints != m.Points)
        {
            save.upgradePoints = m.Points;
            try { PermanentUpgradeManager.Instance?.UpdatePointsText(); } catch (Exception) { }
        }
    }

    void OnSnapReq(Peer from, NetReader r)
    {
        if (!Net.IsHost) return;
        if (!SnapGate.TryEnter(from.Index, GameAccess.NowMs, 3000)) return;
        string json = EconomySnapshot.BuildSnapshotJson();
        Net.SendTo(from, EconMsg.Snapshot, w => w.Write(json));
    }

    void OnSnapshot(Peer from, NetReader r)
    {
        if (!Net.IsClient) return;
        string json = r.ReadString();
        // A snapshot we asked for repairs a running session. The join push is only handed to flow (it decides when
        // the game scene is ready, see EconomySnapshot.PrepareSnapshotForGameStart / ApplySnapshot).
        if (_resyncRequested)
        {
            _resyncRequested = false;
            ShopSync.OnSnapshotApplied();
            EconomySnapshot.ApplySnapshot(json);
        }
        EconomySnapshot.NotifyReceived(json);
    }
}
