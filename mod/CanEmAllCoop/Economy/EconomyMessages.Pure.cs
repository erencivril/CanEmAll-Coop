using System.Collections.Generic;
using CanEmAllCoop.Net;

namespace CanEmAllCoop.Economy;

/// <summary>Economy message ids (range 100-199 is owned by this module).</summary>
public static class EconMsg
{
    public const ushort Wallet = 100;        // host -> clients   WalletMsg
    public const ushort BuyReq = 101;        // client -> host    int skillId
    public const ushort Purchased = 102;     // host -> clients   PurchaseMsg (money tree)
    public const ushort Rejected = 103;      // host -> requester byte kind, int id, byte reason
    public const ushort PermReq = 104;       // client -> host    int skillId
    public const ushort PermPurchased = 105; // host -> clients   PurchaseMsg (permanent upgrade)
    public const ushort SellReq = 106;       // client -> host    (empty)
    public const ushort Sold = 107;          // host -> clients   string snapshot json
    public const ushort SpinReq = 108;       // client -> host    long bet
    public const ushort SpinGo = 109;        // host -> clients   SpinGoMsg
    public const ushort Snapshot = 111;      // host -> client    string snapshot json
    public const ushort SnapReq = 112;       // client -> host    (empty)
    public const ushort Sync = 113;          // host -> clients   SyncMsg
}

public struct WalletMsg
{
    public long Money, TotalMoney;

    public void Write(NetWriter w) { w.Write(Money); w.Write(TotalMoney); }
    public static WalletMsg Read(NetReader r) => new WalletMsg { Money = r.ReadLong(), TotalMoney = r.ReadLong() };
}

/// <summary>Result of an accepted purchase. Points is SaveData.upgradePoints after the purchase (permanent upgrades).</summary>
public struct PurchaseMsg
{
    public int SkillId;
    public long Money, TotalMoney;
    public int Points;
    public int Buyer;

    public void Write(NetWriter w)
    {
        w.Write(SkillId); w.Write(Money); w.Write(TotalMoney); w.Write(Points); w.Write(Buyer);
    }

    public static PurchaseMsg Read(NetReader r) => new PurchaseMsg
    {
        SkillId = r.ReadInt(), Money = r.ReadLong(), TotalMoney = r.ReadLong(), Points = r.ReadInt(), Buyer = r.ReadInt(),
    };
}

/// <summary>Everything a client needs to replay one slot spin: the stake, the balance before the stake and the reel result.</summary>
public struct SpinGoMsg
{
    public int Spinner;
    public long Bet, Before;
    public int R0, R1, R2;

    public void Write(NetWriter w)
    {
        w.Write(Spinner); w.Write(Bet); w.Write(Before); w.Write(R0); w.Write(R1); w.Write(R2);
    }

    public static SpinGoMsg Read(NetReader r) => new SpinGoMsg
    {
        Spinner = r.ReadInt(), Bet = r.ReadLong(), Before = r.ReadLong(), R0 = r.ReadInt(), R1 = r.ReadInt(), R2 = r.ReadInt(),
    };
}

/// <summary>Compact progression summary the host sends periodically; a mismatch makes the client request a full snapshot.</summary>
public struct SyncMsg
{
    public int Points, Day;
    public int[] Skills, Perms;

    public void Write(NetWriter w)
    {
        w.Write(Points); w.Write(Day);
        WriteList(w, Skills);
        WriteList(w, Perms);
    }

    public static SyncMsg Read(NetReader r)
    {
        var m = new SyncMsg { Points = r.ReadInt(), Day = r.ReadInt() };
        m.Skills = ReadList(r);
        m.Perms = ReadList(r);
        return m;
    }

    static void WriteList(NetWriter w, IReadOnlyList<int> list)
    {
        int n = list?.Count ?? 0;
        w.Write(n);
        for (int i = 0; i < n; i++) w.Write(list[i]);
    }

    static int[] ReadList(NetReader r)
    {
        int n = r.ReadInt();
        if (n < 0 || n > 100000) throw new System.IO.InvalidDataException("bad list length " + n);
        var a = new int[n];
        for (int i = 0; i < n; i++) a[i] = r.ReadInt();
        return a;
    }
}
