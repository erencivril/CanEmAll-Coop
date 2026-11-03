using System;
using CanEmAllCoop.Core;

namespace CanEmAllCoop.Economy;
using CanEmAllCoop.Net;
using Net = CanEmAllCoop.Net.Net;

/// <summary>
/// Shared wallet. The host broadcasts the absolute balance (coalesced to 10 Hz, immediately after purchases);
/// clients apply it through the game's own ChangeMoney under Net.Applying so the counter animates as in vanilla.
/// </summary>
internal static class WalletSync
{
    static readonly WalletCoalescer Coalescer = new(100);

    // Client: a wallet that arrived while a slot replay is running is held back, otherwise the replay's own
    // payout would be added on top of a balance that already contains it.
    static bool _hasPending;
    static WalletMsg _pending;
    static long _lastTotal;
    static bool _haveTotal;

    public static string LastSent = "-";

    public static void Reset()
    {
        Coalescer.Reset();
        _hasPending = false;
        _haveTotal = false;
    }

    // ------------------------------------------------------------------------------------------------ host

    public static void HostUpdate()
    {
        if (!Net.IsHost || SlotSync.HostBusy) return;
        if (!GameAccess.TryReadWallet(out var money, out var total)) return;
        if (Coalescer.ShouldSend(money, total, GameAccess.NowMs, false)) Send(money, total);
    }

    /// <summary>Immediately after a purchase / spin / sale.</summary>
    public static void HostFlushNow()
    {
        if (!Net.IsHost) return;
        if (!GameAccess.TryReadWallet(out var money, out var total)) return;
        Send(money, total);
    }

    static void Send(long money, long total)
    {
        Coalescer.MarkSent(money, total, GameAccess.NowMs);
        LastSent = money.ToString();
        var msg = new WalletMsg { Money = money, TotalMoney = total };
        Net.Broadcast(EconMsg.Wallet, w => msg.Write(w));
    }

    public static void HostSendTo(Peer peer)
    {
        if (!Net.IsHost || !GameAccess.TryReadWallet(out var money, out var total)) return;
        var msg = new WalletMsg { Money = money, TotalMoney = total };
        Net.SendTo(peer, EconMsg.Wallet, w => msg.Write(w));
    }

    // ---------------------------------------------------------------------------------------------- client

    public static void OnWallet(Peer from, NetReader r)
    {
        if (!Net.IsClient) return;
        Apply(WalletMsg.Read(r), snap: false);
    }

    /// <summary>Set the local balance to the host's. snap = also jump the displayed counter (used for purchases / snapshots).</summary>
    public static void Apply(WalletMsg m, bool snap)
    {
        if ((SlotSync.ReplayActive || ShopSync.InboxBusy) && !snap)
        {
            _pending = m;
            _hasPending = true;
            return;
        }

        var mm = GameAccess.Money;
        var save = GameAccess.Save;
        if (mm == null || save == null) return;

        long income = _haveTotal ? m.TotalMoney - _lastTotal : 0;
        _lastTotal = m.TotalMoney;
        _haveTotal = true;
        save.totalMoney = m.TotalMoney;

        using (new ApplyScope())
        {
            if (snap) mm.SetMoney(m.Money);
            else mm.ChangeMoney(m.Money);

            // AddMoney is blocked on clients, so the floating "+$" texts are replaced by one batched text per update.
            if (income > 0 && !snap)
            {
                try { mm.SpawnFloatingText((int)Math.Min(income, int.MaxValue), MoneyManager.CoinTextType.Normal); }
                catch (Exception e) { Log.Debug("floating text failed: " + e.Message); }
            }
        }
    }

    /// <summary>Called when a slot replay ends: apply the newest wallet that was held back.</summary>
    public static void ReleasePending()
    {
        if (!_hasPending) return;
        _hasPending = false;
        Apply(_pending, snap: false);
    }

    /// <summary>Purchase inbox drained: the wallet that arrived behind those purchases is newer than theirs, apply it now (unless a slot replay still holds it).</summary>
    public static void ReleaseHeld()
    {
        if (SlotSync.ReplayActive) return;
        ReleasePending();
    }
}
