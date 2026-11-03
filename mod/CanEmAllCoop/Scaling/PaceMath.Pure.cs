using System;
using System.Collections.Generic;
using System.Globalization;

namespace CanEmAllCoop.Scaling;

/// <summary>Pure pace math (plan section 1). No Unity / game types so it is unit-tested.</summary>
public static class PaceMath
{
    /// <summary>Registered-enemy slots kept free below PerfCap so the truck gate (enemies.Count + E &lt;= max) can still open.</summary>
    public const int DefaultHeadroom = 100;

    /// <summary>w(N) = N^a. N &lt;= 1 is exactly 1.</summary>
    public static double Weight(int n, double a) => n <= 1 ? 1.0 : Math.Pow(n, a);

    /// <summary>Round half up (the plan's table), with a tiny epsilon so x.5 products computed in floating point round up.</summary>
    public static int RoundHalfUp(double x) => (int)Math.Floor(x + 0.5 + 1e-9);

    /// <summary>
    /// E_eff = clamp(round_half_up(E_base * w), E_base, PerfCap - headroom). Never below E_base (so k &gt;= 1),
    /// N = 1 returns E_base unchanged.
    /// </summary>
    public static int EffectiveEnemies(int eBase, int n, double a, int perfCap, int headroom = DefaultHeadroom)
    {
        if (eBase < 1) return eBase;
        if (n <= 1) return eBase;
        int scaled = RoundHalfUp(eBase * Weight(n, a));
        int ceiling = Math.Max(1, perfCap - headroom);
        return Math.Max(eBase, Math.Min(scaled, ceiling));
    }

    /// <summary>
    /// Field cap = min(ceil(orig * w), PerfCap), but never below the solo asset value (N = 1 or a small PerfCap must not
    /// shrink the vanilla cap).
    /// </summary>
    public static int FieldCap(int origMax, int n, double a, int perfCap)
    {
        if (origMax < 1 || n <= 1) return origMax;
        long raw = (long)Math.Ceiling(origMax * Weight(n, a) - 1e-9);
        int capped = (int)Math.Min(raw, Math.Max(perfCap, 1));
        return Math.Max(origMax, capped);
    }

    /// <summary>Realized pace factor k = E_eff / E_base (1 when E_base is unknown).</summary>
    public static double Ratio(int eEff, int eBase) => eBase < 1 ? 1.0 : (double)eEff / eBase;

    /// <summary>
    /// Players for the day: the host plus the clients whose game is running (a connected peer that is still loading or sits
    /// in the menu does not play the day, so it must not scale the supply). Always 1..4.
    /// </summary>
    public static int PlayerCount(int inGameClients) => Math.Clamp(1 + Math.Max(0, inGameClients), 1, 4);

    /// <summary>
    /// The supply ratio as integers, k = num / den with num &gt;= den (k is never below 1): SupplyNum = max(E_eff, E_base).
    /// 1/1 while E_base or E_eff is unknown.
    /// </summary>
    public static void SupplyFraction(int eBase, int eEff, out long num, out long den)
    {
        if (eBase < 1 || eEff < 1) { num = 1; den = 1; return; }
        num = Math.Max(eEff, eBase);
        den = eBase;
    }

    /// <summary>Denominator of the fixed-point divisor used when the exponent valve is not 1.</summary>
    public const long FixedDen = 1_000_000;

    /// <summary>
    /// Income divisor = k^exponent as a fraction num/den (income paid = amount * den / num). Exponent 1 (the default) keeps
    /// the exact integer ratio of the supply; 0 pays everything; other values use a 6-digit fixed-point approximation.
    /// </summary>
    public static void IncomeDivisor(long supplyNum, long supplyDen, double exponent, out long num, out long den)
    {
        if (supplyDen < 1 || supplyNum < supplyDen) { supplyNum = 1; supplyDen = 1; }
        if (double.IsNaN(exponent) || exponent <= 0.0 || supplyNum == supplyDen) { num = 1; den = 1; return; }
        if (Math.Abs(exponent - 1.0) < 1e-9) { num = supplyNum; den = supplyDen; return; }
        double d = Math.Pow((double)supplyNum / supplyDen, Math.Min(exponent, 4.0));
        num = Math.Max(FixedDen, (long)Math.Round(d * FixedDen));   // never below 1.0
        den = FixedDen;
    }

    /// <summary>Co-op skill price: the solo price times num/den (the income divisor), rounded half up, at most int.MaxValue.</summary>
    public static int ScalePrice(int solo, long num, long den)
    {
        if (solo <= 0 || den < 1 || num <= den) return solo;
        long p = (solo * num + den / 2) / den;
        return p > int.MaxValue ? int.MaxValue : (int)p;
    }

    /// <summary>Default of the one-frame spawn valve: the largest truck batch Truck.SpawnEnemiesInsideTruck is asked to create at once.</summary>
    public const int DefaultMaxSpawnBatch = 650;

    /// <summary>
    /// The "perfCap" handed to <see cref="EffectiveEnemies"/> so the batch never exceeds <paramref name="maxSpawnBatch"/>
    /// (EffectiveEnemies subtracts the headroom again) while PerfCap alone still bounds the field cap.
    /// </summary>
    public static int BatchCap(int perfCap, int maxSpawnBatch) =>
        Math.Min(perfCap, Math.Max(1, maxSpawnBatch) + DefaultHeadroom);

    /// <summary>Text for the menu / HUD, e.g. "N=2 k=1.74".</summary>
    public static string Format(int n, int eEff, int eBase, bool free)
    {
        string k = Ratio(eEff, eBase).ToString("0.00", CultureInfo.InvariantCulture);
        return free ? $"N={n} k={k} (free)" : $"N={n} k={k}";
    }
}

/// <summary>Reports when a polled value changed (the first observation only records it).</summary>
public sealed class ChangeWatch<T> where T : struct
{
    T _last;
    bool _has;

    public bool Update(T value)
    {
        if (!_has) { _last = value; _has = true; return false; }
        if (EqualityComparer<T>.Default.Equals(_last, value)) return false;
        _last = value;
        return true;
    }

    public void Reset() => _has = false;
}

/// <summary>
/// Exact fractional accumulator: Take(amount, num, den) returns floor of the running sum of amount*num/den and keeps the
/// remainder, in integer arithmetic only (no floating point drift over millions of calls).
/// Money: amount / k with k = eEff/eBase  =&gt;  Take(amount, eBase, eEff).
/// The remainder lives in units of 1/den of the last call; when the ratio changes (N, a purchase, a rebirth) it is
/// rebased to the new denominator so the fraction already earned is neither lost nor inflated. Only Reset() clears it.
/// </summary>
public sealed class RatioCarry
{
    long _acc;
    long _den;   // denominator _acc is expressed in (0 = nothing carried yet)

    public long Remainder => _acc;

    public void Reset() { _acc = 0; _den = 0; }

    public long Take(long amount, long num, long den)
    {
        if (den <= 0 || num < 0 || amount < 0) return amount;
        if (num == den) return amount;     // identity ratio: nothing to divide, the carried fraction stays for later
        if (_den != den)
        {
            // fraction _acc/_den -> same fraction over the new denominator (floor: never pays out more than was earned)
            _acc = _den > 0 ? (_acc % _den) * den / _den : 0;
            _den = den;
        }
        _acc += amount * num;
        long whole = _acc / den;
        _acc -= whole * den;
        return whole;
    }
}
