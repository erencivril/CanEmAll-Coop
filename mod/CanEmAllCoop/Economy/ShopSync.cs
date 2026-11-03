using System;
using System.Collections.Generic;
using System.Diagnostics;
using CanEmAllCoop.Core;
using CanEmAllCoop.Scaling;

namespace CanEmAllCoop.Economy;
using CanEmAllCoop.Net;
using Net = CanEmAllCoop.Net.Net;

/// <summary>
/// Skill tree / permanent upgrade / rebirth flow.
/// Host: the game's own OnClick/Sell run unchanged (also for a friend's request, after validation) and a postfix
/// announces the outcome. Client: the click becomes a request; the announcement is mirrored locally without
/// repeating the payment (money and points come from the host).
/// </summary>
internal static class ShopSync
{
    const int PermKeyBase = 100000;
    const int SellKey = 200000;

    static readonly RequestGate Gate = new();
    static Peer _remoteBuyer;                 // host: who asked for the click that is running right now (null = host itself)
    static long _lastSellMs = long.MinValue / 2;

    /// <summary>Host: SkillPopUp.Show is skipped while a friend's purchase runs through the host's UI code.</summary>
    public static bool SuppressPopup;

    // Client: Purchased / PermPurchased messages wait here and are applied within a per-frame time budget. An autobuy burst
    // used to run ApplySkill + tree animation + NodePurchased + Rescale + sound + wallet for every skill inside one frame.
    struct Inbound { public bool Perm; public PurchaseMsg Msg; }
    static readonly Queue<Inbound> Inbox = new();
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    const double InboxBudgetMs = 4.0;
    static bool _haveBatchWallet;
    static WalletMsg _batchWallet;

    /// <summary>true while purchases are waiting: the unlock lists lag behind the host's on purpose, so no repair may be requested.</summary>
    public static bool InboxBusy => Inbox.Count > 0;

    static bool InboxHas(bool perm, int id)
    {
        foreach (var i in Inbox) if (i.Perm == perm && i.Msg.SkillId == id) return true;
        return false;
    }

    public static void Reset()
    {
        Inbox.Clear();
        _haveBatchWallet = false;
        Gate.Clear();
        _remoteBuyer = null;
        SuppressPopup = false;
        GameAccess.ForgetScene();
    }

    // ------------------------------------------------------------------------------------------ client requests

    public static void RequestBuy(int skillId)
    {
        if (!Gate.TryEnter(skillId, GameAccess.NowMs, 1500)) return;
        Net.SendToHost(EconMsg.BuyReq, w => w.Write(skillId));
    }

    public static void RequestPermanent(int skillId)
    {
        if (!Gate.TryEnter(PermKeyBase + skillId, GameAccess.NowMs, 1500)) return;
        Net.SendToHost(EconMsg.PermReq, w => w.Write(skillId));
    }

    public static void RequestSell()
    {
        if (!Gate.TryEnter(SellKey, GameAccess.NowMs, 3000)) return;
        Net.SendToHost(EconMsg.SellReq, _ => { });
    }

    // ----------------------------------------------------------------------------------------------- host side

    public static void OnBuyReq(Peer from, NetReader r)
    {
        if (!Net.IsHost) return;
        int id = r.ReadInt();
        var node = GameAccess.FindSkillNode(id);
        var mm = GameAccess.Money;
        var skill = node?.skill;
        var reason = EconomyRules.ValidateBuy(skill != null && mm != null, skill != null && skill.isPurchased,
            node != null && node.IsLocked, mm != null ? mm.Money : 0, skill != null ? skill.price : 0);
        if (reason != RejectReason.None) { Reject(from, RejectKind.Skill, id, reason); return; }

        RunRemoteClick(from, () => node.OnClick());
        if (!skill.isPurchased) Reject(from, RejectKind.Skill, id, RejectReason.Unknown);
    }

    public static void OnPermReq(Peer from, NetReader r)
    {
        if (!Net.IsHost) return;
        int id = r.ReadInt();
        var node = GameAccess.FindPermanent(id);
        var save = GameAccess.Save;
        var skill = node?.skill;
        var reason = EconomyRules.ValidatePermanent(skill != null && save != null, skill != null && skill.isPurchased,
            save != null ? save.upgradePoints : 0, skill != null ? skill.price : 0);
        if (reason != RejectReason.None) { Reject(from, RejectKind.Permanent, id, reason); return; }

        RunRemoteClick(from, () => node.OnClick());
        if (!skill.isPurchased) Reject(from, RejectKind.Permanent, id, RejectReason.Unknown);
    }

    public static void OnSellReq(Peer from, NetReader r)
    {
        if (!Net.IsHost) return;
        long now = GameAccess.NowMs;
        if (now - _lastSellMs < 3000) return; // two friends clicking at once must not sell the factory twice
        var pm = PermanentUpgradeManager.Instance;
        if (pm == null) { Reject(from, RejectKind.Sell, 0, RejectReason.Unknown); return; }
        RunRemoteClick(from, () => pm.Sell());
    }

    static void RunRemoteClick(Peer from, Action click)
    {
        _remoteBuyer = from;
        SuppressPopup = true;
        try { click(); }
        catch (Exception e) { Log.Error("remote shop click failed", e); }
        finally { SuppressPopup = false; _remoteBuyer = null; }
    }

    static void Reject(Peer to, RejectKind kind, int id, RejectReason reason)
    {
        Net.SendTo(to, EconMsg.Rejected, w => { w.Write((byte)kind); w.Write(id); w.Write((byte)reason); });
    }

    /// <summary>Postfix of SkillNode.OnClick on the host when the click bought something.</summary>
    public static void HostSkillPurchased(SkillNode node)
    {
        var skill = node.skill;
        if (skill == null) return;
        var msg = BuildPurchase((int)skill.skillId);
        Net.Broadcast(EconMsg.Purchased, w => msg.Write(w));
        WalletSync.HostFlushNow();
    }

    public static void HostPermanentPurchased(PermanentUpgrade node)
    {
        var skill = node.skill;
        if (skill == null) return;
        var msg = BuildPurchase((int)skill.skillId);
        Net.Broadcast(EconMsg.PermPurchased, w => msg.Write(w));
        WalletSync.HostFlushNow();
    }

    static PurchaseMsg BuildPurchase(int skillId)
    {
        GameAccess.TryReadWallet(out var money, out var total);
        return new PurchaseMsg
        {
            SkillId = skillId,
            Money = money,
            TotalMoney = total,
            Points = GameAccess.Save?.upgradePoints ?? 0,
            Buyer = _remoteBuyer?.Index ?? Net.LocalPeer.Index,
        };
    }

    /// <summary>Postfix of PermanentUpgradeManager.Sell on the host.</summary>
    public static void HostSold()
    {
        _lastSellMs = GameAccess.NowMs;
        string json = EconomySnapshot.BuildSnapshotJson();
        Net.Broadcast(EconMsg.Sold, w => w.Write(json));
        WalletSync.HostFlushNow();
    }

    // ----------------------------------------------------------------------------------------------- client side

    public static void OnPurchased(Peer from, NetReader r)
    {
        if (!Net.IsClient) return;
        var m = PurchaseMsg.Read(r);
        Gate.Release(m.SkillId);
        Inbox.Enqueue(new Inbound { Perm = false, Msg = m });
        DrainInbox();
    }

    static void ApplyPurchased(in PurchaseMsg m)
    {
        var node = GameAccess.FindSkillNode(m.SkillId);
        var save = GameAccess.Save;
        if (node == null || node.skill == null || save == null)
        {
            Log.Warn($"Purchased({m.SkillId}) but the node/save is missing; requesting a snapshot");
            Net.SendToHost(EconMsg.SnapReq, _ => { });
            return;
        }

        var skill = node.skill;
        using (new ApplyScope())
        {
            if (skill.isPurchased) return;
            // Mirror of the post-payment half of SkillNode.OnClick; the payment itself is never repeated.
            if (!GameAccess.Contains(save.unlockedSkills, m.SkillId)) save.unlockedSkills.Add(skill.skillId);
            skill.isPurchased = true;
            Guard("RefreshLockState", () => node.RefreshLockState());
            // OnClick tints a bought node with its own colours; the remote purchase skips OnClick, so the client did not show it
            Guard("PurchasedColors", () =>
            {
                if (node.background != null) node.background.color = node.purchasedColor;
                if (node.icon != null) node.icon.color = node.purchasedIconColor;
            });
            Guard("ApplySkill", () => skill.ApplySkill());
            var tree = GameAccess.Tree;
            if (tree != null)
            {
                // the pop animation is cosmetic: with more purchases waiting behind this one it is skipped
                if (Inbox.Count <= 1) Guard("AnimateNode", () => tree.AnimateNode(node));
                Guard("NodePurchased", () => tree.NodePurchased(node));
            }
        }
    }

    /// <summary>Applies waiting purchases until the frame budget is spent (at least one), then once per drained batch: sound, Rescale, wallet.</summary>
    static void DrainInbox()
    {
        if (Inbox.Count == 0) return;
        long end = Clock.ElapsedTicks + (long)(InboxBudgetMs * Stopwatch.Frequency / 1000.0);
        bool any = false;
        while (Inbox.Count > 0)
        {
            var item = Inbox.Dequeue();
            any = true;
            _batchWallet = new WalletMsg { Money = item.Msg.Money, TotalMoney = item.Msg.TotalMoney };
            _haveBatchWallet = true;
            if (item.Perm) ApplyPermanentPurchased(item.Msg); else ApplyPurchased(item.Msg);
            if (Clock.ElapsedTicks >= end) break;
        }
        if (!any || Inbox.Count > 0) return;

        Guard("purchase clip", () => UISound.Instance?.PlayPurchaseClip());
        Guard("Pace", Pace.OnSkillApplied);
        if (_haveBatchWallet)
        {
            _haveBatchWallet = false;
            WalletSync.Apply(_batchWallet, snap: true);
        }
        WalletSync.ReleaseHeld();
    }

    /// <summary>Per frame (EconomyModule.OnUpdate): keep draining a long purchase burst.</summary>
    public static void UpdateInbox()
    {
        if (!Net.IsClient) { if (Inbox.Count > 0) Inbox.Clear(); return; }
        DrainInbox();
    }

    public static void OnPermanentPurchased(Peer from, NetReader r)
    {
        if (!Net.IsClient) return;
        var m = PurchaseMsg.Read(r);
        Gate.Release(PermKeyBase + m.SkillId);
        Inbox.Enqueue(new Inbound { Perm = true, Msg = m });
        DrainInbox();
    }

    static void ApplyPermanentPurchased(in PurchaseMsg m)
    {
        var node = GameAccess.FindPermanent(m.SkillId);
        var save = GameAccess.Save;
        if (node == null || node.skill == null || save == null)
        {
            Log.Warn($"PermPurchased({m.SkillId}) but the node/save is missing; requesting a snapshot");
            Net.SendToHost(EconMsg.SnapReq, _ => { });
            return;
        }

        var skill = node.skill;
        using (new ApplyScope())
        {
            save.upgradePoints = m.Points;
            if (!skill.isPurchased)
            {
                if (!GameAccess.Contains(save.unlockedPermanentUpgrades, m.SkillId)) save.unlockedPermanentUpgrades.Add(skill.skillId);
                skill.isPurchased = true;
                // StartingCash only adds cash; the host already paid it into the wallet that is applied below.
                if (!EconomyRules.IsStartingCash(m.SkillId)) Guard("ApplySkill", () => skill.ApplySkill());
            }
            Guard("UpdatePointsText", () => PermanentUpgradeManager.Instance?.UpdatePointsText());
        }
    }

    public static void OnSold(Peer from, NetReader r)
    {
        if (!Net.IsClient) return;
        Gate.Release(SellKey);
        Inbox.Clear();          // the snapshot already contains every purchase that was queued before it (ordered channel)
        _haveBatchWallet = false;
        EconomySnapshot.ApplySnapshot(r.ReadString());
    }

    /// <summary>A repair snapshot replaces the lists as well: whatever waits in the inbox is already part of it.</summary>
    public static void OnSnapshotApplied()
    {
        Inbox.Clear();
        _haveBatchWallet = false;
    }

    public static void OnRejected(Peer from, NetReader r)
    {
        if (!Net.IsClient) return;
        var kind = (RejectKind)r.ReadByte();
        int id = r.ReadInt();
        var reason = (RejectReason)r.ReadByte();
        Log.Debug($"request rejected: {kind} {id} {reason}");

        switch (kind)
        {
            case RejectKind.Skill:
            {
                Gate.Release(id);
                var node = GameAccess.FindSkillNode(id);
                // The purchase may already have arrived (two clicks): then there is nothing to complain about.
                if (InboxHas(false, id) || (node != null && node.skill != null && node.skill.isPurchased)) return;
                Guard("shake", () => { node?.Shake(); SkillPopUp.Instance?.ShakePriceText(); });
                break;
            }
            case RejectKind.Permanent:
            {
                Gate.Release(PermKeyBase + id);
                var node = GameAccess.FindPermanent(id);
                if (InboxHas(true, id) || (node != null && node.skill != null && node.skill.isPurchased)) return;
                Guard("shake", () => SkillPopUp.Instance?.ShakePermanentPointsText());
                break;
            }
            case RejectKind.Slot:
                SlotSync.OnRejected();
                break;
            case RejectKind.Sell:
                Gate.Release(SellKey);
                break;
        }
        if (kind != RejectKind.Slot) Guard("error clip", () => UISound.Instance?.PlayErrorClip());
    }

    internal static void Guard(string what, Action a)
    {
        try { a(); }
        catch (Exception e) { Log.Error("economy: " + what + " failed", e); }
    }
}
