using System;
using System.Collections.Generic;

namespace CanEmAllCoop.Economy;

public enum RejectReason : byte
{
    None = 0,
    Unknown = 1,
    AlreadyPurchased = 2,
    Locked = 3,
    CannotAfford = 4,
    Busy = 5,
    NoMoney = 6,
}

/// <summary>What a rejection refers to (first byte of the Rejected message).</summary>
public enum RejectKind : byte { Skill = 0, Permanent = 1, Slot = 2, Sell = 3 }

/// <summary>Pure decision logic of the host-side shop; no Unity types so it is unit-tested.</summary>
public static class EconomyRules
{
    /// <summary>Largest stake allowed for one spin. fraction >= 1 (or NaN) = no extra limit. Never below 1 while money > 0.</summary>
    public static long MaxBet(long money, float fraction)
    {
        if (money <= 0) return 0;
        if (float.IsNaN(fraction) || fraction >= 1f) return money;
        if (fraction <= 0f) return 1;
        long cap = (long)Math.Floor(money * (double)fraction);
        return Math.Max(1, Math.Min(cap, money));
    }

    public static RejectReason ValidateBuy(bool exists, bool purchased, bool locked, long money, long price)
    {
        if (!exists) return RejectReason.Unknown;
        if (purchased) return RejectReason.AlreadyPurchased;
        if (locked) return RejectReason.Locked;
        if (money < price) return RejectReason.CannotAfford;
        return RejectReason.None;
    }

    public static RejectReason ValidatePermanent(bool exists, bool purchased, long points, long price)
    {
        if (!exists) return RejectReason.Unknown;
        if (purchased) return RejectReason.AlreadyPurchased;
        if (points < price) return RejectReason.CannotAfford;
        return RejectReason.None;
    }

    /// <summary>StartingCash I-III: their ApplySkill grants cash, which a client must take from the host wallet instead.</summary>
    public static bool IsStartingCash(int skillId) => skillId >= 232 && skillId <= 234;

    public static bool SameSequence(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        if (a == null || b == null) return a == b;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}

/// <summary>Decides when the host wallet is (re)broadcast: at most every <c>minIntervalMs</c> and only on change, or when forced.</summary>
public sealed class WalletCoalescer
{
    readonly int _minIntervalMs;
    long _sentMoney = long.MinValue, _sentTotal = long.MinValue, _sentAt = long.MinValue / 2;

    public WalletCoalescer(int minIntervalMs) => _minIntervalMs = minIntervalMs;

    public bool ShouldSend(long money, long total, long nowMs, bool force)
    {
        if (force) return true;
        bool changed = money != _sentMoney || total != _sentTotal;
        return changed && nowMs - _sentAt >= _minIntervalMs;
    }

    public void MarkSent(long money, long total, long nowMs)
    {
        _sentMoney = money;
        _sentTotal = total;
        _sentAt = nowMs;
    }

    public void Reset()
    {
        _sentMoney = _sentTotal = long.MinValue;
        _sentAt = long.MinValue / 2;
    }
}

/// <summary>Per-key cooldown so a spamming client sends one request per key until it is answered (or the cooldown ends).</summary>
public sealed class RequestGate
{
    readonly Dictionary<int, long> _until = new();

    public bool TryEnter(int key, long nowMs, int cooldownMs)
    {
        if (_until.TryGetValue(key, out var until) && nowMs < until) return false;
        _until[key] = nowMs + cooldownMs;
        return true;
    }

    public void Release(int key) => _until.Remove(key);
    public void Clear() => _until.Clear();
}
