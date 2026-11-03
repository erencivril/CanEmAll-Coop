using System;
using CanEmAllCoop.Scaling;
using Xunit;

namespace CanEmAllCoop.Tests;

public class ScalingTests
{
    const double A = 0.8;
    const int Cap = 1000;

    // Plan section 1.3: rows N = 2,3,4; columns E_base = 1, 3, 12, 25, 90, 250.
    [Theory]
    [InlineData(2, 1, 2, 2.00)]
    [InlineData(2, 3, 5, 1.67)]
    [InlineData(2, 12, 21, 1.75)]
    [InlineData(2, 25, 44, 1.76)]
    [InlineData(2, 90, 157, 1.74)]
    [InlineData(2, 250, 435, 1.74)]
    [InlineData(3, 1, 2, 2.00)]
    [InlineData(3, 3, 7, 2.33)]
    [InlineData(3, 12, 29, 2.42)]
    [InlineData(3, 25, 60, 2.40)]
    [InlineData(3, 90, 217, 2.41)]
    [InlineData(3, 250, 602, 2.41)]
    [InlineData(4, 1, 3, 3.00)]
    [InlineData(4, 3, 9, 3.00)]
    [InlineData(4, 12, 36, 3.00)]
    [InlineData(4, 25, 76, 3.04)]
    [InlineData(4, 90, 273, 3.03)]
    [InlineData(4, 250, 758, 3.03)]
    public void PlanTable(int n, int eBase, int expectedEff, double expectedK)
    {
        int eff = PaceMath.EffectiveEnemies(eBase, n, A, Cap);
        Assert.Equal(expectedEff, eff);
        Assert.Equal(expectedK, Math.Round(PaceMath.Ratio(eff, eBase), 2, MidpointRounding.AwayFromZero), 2);
    }

    [Theory]
    [InlineData(2, 1.74)]
    [InlineData(3, 2.41)]
    [InlineData(4, 3.03)]
    public void WeightTable(int n, double w) => Assert.Equal(w, Math.Round(PaceMath.Weight(n, A), 2), 2);

    [Fact]
    public void RoundHalfUp_IsHalfUpNotBankers()
    {
        Assert.Equal(3, PaceMath.RoundHalfUp(2.5));
        Assert.Equal(2, PaceMath.RoundHalfUp(1.5));
        Assert.Equal(1, PaceMath.RoundHalfUp(0.5));
        Assert.Equal(4, PaceMath.RoundHalfUp(3.4999999999));
        Assert.Equal(4, PaceMath.RoundHalfUp(3.5));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(12)]
    [InlineData(250)]
    [InlineData(5000)]
    public void SoloIsIdentity(int eBase)
    {
        Assert.Equal(eBase, PaceMath.EffectiveEnemies(eBase, 1, A, Cap));
        Assert.Equal(1.0, PaceMath.Ratio(eBase, eBase));
        Assert.Equal(750, PaceMath.FieldCap(750, 1, A, Cap));
        Assert.Equal(750, PaceMath.FieldCap(750, 1, A, 100)); // small PerfCap must not shrink the vanilla cap
    }

    [Fact]
    public void ZeroOrNegativePlayersAreSolo()
    {
        Assert.Equal(25, PaceMath.EffectiveEnemies(25, 0, A, Cap));
        Assert.Equal(1.0, PaceMath.Weight(0, A));
    }

    [Fact]
    public void PerfCapLimitsBatchAndLowersK()
    {
        // 250 * 3.03 = 758 fits under 1000 - 100; with a PerfCap of 600 the batch is capped to 500 (k = 2.0 instead of 3.03).
        int eff = PaceMath.EffectiveEnemies(250, 4, A, 600);
        Assert.Equal(500, eff);
        Assert.Equal(2.0, PaceMath.Ratio(eff, 250));
    }

    [Fact]
    public void BatchNeverBelowBase()
    {
        // Base already above the cap: keep it (k = 1), do not shrink the player's own purchase.
        Assert.Equal(950, PaceMath.EffectiveEnemies(950, 4, A, Cap));
        Assert.Equal(300, PaceMath.EffectiveEnemies(300, 2, A, 200));
    }

    [Fact]
    public void HeadroomIsConfigurable()
    {
        Assert.Equal(1000, PaceMath.EffectiveEnemies(400, 4, A, 1000, headroom: 0)); // 400*3.03=1212 -> 1000
        Assert.Equal(900, PaceMath.EffectiveEnemies(400, 4, A, 1000));               // default headroom 100
    }

    [Theory]
    [InlineData(2, 1000)] // ceil(750*1.7411) = 1306 -> PerfCap
    [InlineData(3, 1000)]
    [InlineData(4, 1000)]
    public void FieldCapIsClampedByPerfCap(int n, int expected) => Assert.Equal(expected, PaceMath.FieldCap(750, n, A, Cap));

    [Fact]
    public void FieldCapScalesWhenPerfCapIsHigh()
    {
        Assert.Equal(1306, PaceMath.FieldCap(750, 2, A, 5000)); // ceil(750 * 2^0.8)
        Assert.Equal(1807, PaceMath.FieldCap(750, 3, A, 5000)); // ceil(750 * 3^0.8) = ceil(1806.1)
        Assert.Equal(2274, PaceMath.FieldCap(750, 4, A, 5000)); // ceil(750 * 4^0.8) = ceil(2273.6)
    }

    [Fact]
    public void BatchAlwaysFitsFieldCap()
    {
        foreach (int n in new[] { 2, 3, 4 })
            foreach (int e in new[] { 1, 3, 6, 12, 25, 50, 90, 125, 150, 200, 250 })
                Assert.True(PaceMath.EffectiveEnemies(e, n, A, Cap) <= PaceMath.FieldCap(750, n, A, Cap));
    }

    [Fact]
    public void LowerExponentGivesLowerK()
    {
        Assert.True(PaceMath.EffectiveEnemies(100, 4, 0.5, Cap) < PaceMath.EffectiveEnemies(100, 4, 1.0, Cap));
        Assert.Equal(400, PaceMath.EffectiveEnemies(100, 4, 1.0, Cap));
    }

    // ---- carry ----

    [Fact]
    public void Carry_SoloPassesThrough()
    {
        var c = new RatioCarry();
        for (int i = 1; i < 1000; i++) Assert.Equal(i, c.Take(i, 7, 7));
        Assert.Equal(0, c.Remainder);
    }

    [Fact]
    public void Carry_SmallAmountsAreNeverLost()
    {
        // k = 3 (E 1 -> 3): amounts of 1 pay 1 every third call.
        var c = new RatioCarry();
        long paid = 0;
        for (int i = 0; i < 3000; i++) paid += c.Take(1, 1, 3);
        Assert.Equal(1000, paid);
        Assert.Equal(0, c.Remainder);
    }

    [Fact]
    public void Carry_AggregateIsExactOverManyCalls()
    {
        // k = 758/250 (N=4, E=250). Total paid must equal floor(total * 250 / 758) exactly, at every prefix.
        var c = new RatioCarry();
        var rng = new Random(1234);
        long total = 0, paid = 0;
        for (int i = 0; i < 2_000_000; i++)
        {
            int amount = 1 + rng.Next(0, i % 7 == 0 ? 5000 : 40);
            total += amount;
            paid += c.Take(amount, 250, 758);
            if (i % 99_991 == 0) Assert.Equal(total * 250 / 758, paid);
        }
        Assert.Equal(total * 250 / 758, paid);
        Assert.Equal((total * 250) % 758, c.Remainder);
    }

    [Fact]
    public void Carry_NeverPaysMoreThanSoloEquivalent()
    {
        var c = new RatioCarry();
        long total = 0, paid = 0;
        for (int i = 1; i <= 5000; i++)
        {
            total += i;
            paid += c.Take(i, 44, 76); // k = 76/44
            Assert.True(paid * 76 <= total * 44);
        }
    }

    [Fact]
    public void Carry_LargeAmountsDoNotOverflow()
    {
        var c = new RatioCarry();
        long got = c.Take(int.MaxValue, 5000, 5001);
        Assert.Equal((long)int.MaxValue * 5000 / 5001, got);
    }

    [Fact]
    public void Carry_RatioChangeKeepsRemainderBounded()
    {
        var c = new RatioCarry();
        c.Take(10, 1, 3);                 // remainder 1
        c.Take(1, 1, 3);
        long r = c.Take(0, 1, 2);         // den shrinks; remainder must be reduced below the new den
        Assert.InRange(c.Remainder, 0, 1);
        Assert.True(r >= 0);
    }

    [Fact]
    public void Carry_RefundPatternForFirstBlood()
    {
        // F6: per counter increment give back (1 - 1/k) = (num-den)/num with k = num/den. k = 2 -> every second hit refunds one.
        var c = new RatioCarry();
        long refunds = 0;
        for (int i = 0; i < 100; i++) refunds += c.Take(1, 2 - 1, 2);
        Assert.Equal(50, refunds);
    }

    [Fact]
    public void Format_ShowsNAndK()
    {
        Assert.Equal("N=2 k=1.74", PaceMath.Format(2, 435, 250, false));
        Assert.Equal("N=1 k=1.00", PaceMath.Format(1, 1, 1, false));
        Assert.Equal("N=4 k=3.03 (free)", PaceMath.Format(4, 758, 250, true));
    }

    // ---- A1: carry survives a rescale, rebases on ratio change, only Reset clears it ----

    [Fact]
    public void Carry_RatioChangeRebasesTheEarnedFraction()
    {
        var c = new RatioCarry();
        Assert.Equal(0, c.Take(1, 2, 3));            // earned 2/3 of a unit
        Assert.Equal(2, c.Remainder);
        // ratio changes to den 6 (e.g. a MoreEnemies purchase): the 2/3 is now 4/6, not lost and not inflated
        Assert.Equal(0, c.Take(0, 1, 6));
        Assert.Equal(4, c.Remainder);
        // one more call of 1 at num/den = 1/6 adds 1/6 -> 5/6: still below one unit
        Assert.Equal(0, c.Take(1, 1, 6));
        Assert.Equal(5, c.Remainder);
        Assert.Equal(1, c.Take(1, 1, 6));            // 6/6 -> pays one unit, remainder 0
        Assert.Equal(0, c.Remainder);
    }

    [Fact]
    public void Carry_RebaseNeverPaysMoreThanEarned()
    {
        // random ratio changes (what Rescale does at every day start / purchase / sell): paid <= exact real-valued income
        var rng = new Random(7);
        var c = new RatioCarry();
        double exact = 0; long paid = 0; int rebases = 0;
        for (int i = 0; i < 100_000; i++)
        {
            if (i % 50 == 0) { _den = 100 + rng.Next(0, 900); _num = 1 + rng.Next(0, (int)_den); rebases++; }
            int amount = 1 + rng.Next(0, 30);
            exact += (double)amount * _num / _den;
            paid += c.Take(amount, _num, _den);
            Assert.True(paid <= exact + 1e-6);
        }
        // a rebase floors to the new denominator: at most 1/den (<= 1/100 here) of a unit is held back per rebase, never more
        Assert.True(exact - paid < 1.0 + rebases / 100.0 + 1e-6);
    }
    long _num = 1, _den = 1;

    [Fact]
    public void Carry_IdentityRatioKeepsTheRemainderForLater()
    {
        // N=1 days between scaled days must not eat the fraction earned before
        var c = new RatioCarry();
        c.Take(1, 1, 2);                              // remainder 1/2
        Assert.Equal(7, c.Take(7, 5, 5));
        Assert.Equal(1, c.Remainder);
        Assert.Equal(1, c.Take(1, 1, 2));
    }

    [Fact]
    public void Carry_ResetClears()
    {
        var c = new RatioCarry();
        c.Take(1, 1, 3);
        c.Reset();
        Assert.Equal(0, c.Remainder);
        Assert.Equal(0, c.Take(1, 1, 3));
        Assert.Equal(1, c.Remainder);
    }

    // ---- A2: player count ----

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 4)]
    [InlineData(9, 4)]
    [InlineData(-5, 1)]
    public void PlayerCount_IsHostPlusInGameClients(int inGame, int expected) =>
        Assert.Equal(expected, PaceMath.PlayerCount(inGame));

    // ---- A3: k never below 1 ----

    [Fact]
    public void SupplyFraction_NeverBelowOne_Property()
    {
        int[] tiers = { 1, 3, 6, 12, 25, 50, 90, 125, 150, 200, 250 };
        for (int n = 1; n <= 4; n++)
        foreach (int e in tiers)
        for (double a = 0.3; a <= 1.5001; a += 0.1)
        for (int cap = 100; cap <= 5000; cap += 350)
        {
            int eff = PaceMath.EffectiveEnemies(e, n, a, PaceMath.BatchCap(cap, PaceMath.DefaultMaxSpawnBatch));
            PaceMath.SupplyFraction(e, eff, out long num, out long den);
            Assert.True(num >= den, $"k<1 at n={n} e={e} a={a} cap={cap}");
            Assert.Equal(e, den);
            Assert.True(num >= e);
            // a stale E_eff (game raised E_base, rescale not run yet) must also clamp
            PaceMath.SupplyFraction(e + 40, eff, out long n2, out long d2);
            Assert.True(n2 >= d2);
        }
    }

    [Fact]
    public void SupplyFraction_UnknownIsIdentity()
    {
        PaceMath.SupplyFraction(0, 50, out var n, out var d); Assert.Equal((1L, 1L), (n, d));
        PaceMath.SupplyFraction(50, 0, out n, out d); Assert.Equal((1L, 1L), (n, d));
    }

    // ---- A4: spawn batch valve ----

    [Fact]
    public void BatchCap_LimitsTheBatchNotTheBase()
    {
        // 4 players, E_base 250, a=1.0 would want 1000; the valve keeps the batch at 650
        Assert.Equal(650, PaceMath.EffectiveEnemies(250, 4, 1.0, PaceMath.BatchCap(5000, 650)));
        // PerfCap still wins when lower
        Assert.Equal(400, PaceMath.EffectiveEnemies(250, 4, 1.0, PaceMath.BatchCap(500, 650)));
        // E_base above the valve is never reduced (solo behaviour is untouched)
        Assert.Equal(700, PaceMath.EffectiveEnemies(700, 4, 0.5, PaceMath.BatchCap(5000, 650)));
        // the realized ratio follows the capped batch
        Assert.Equal(2.6, PaceMath.Ratio(PaceMath.EffectiveEnemies(250, 4, 1.0, PaceMath.BatchCap(5000, 650)), 250), 6);
    }

    // ---- A5: divisor exponent valve ----

    [Fact]
    public void IncomeDivisor_ExponentOneIsExactRatio()
    {
        PaceMath.IncomeDivisor(758, 250, 1.0, out var n, out var d);
        Assert.Equal((758L, 250L), (n, d));
    }

    [Fact]
    public void IncomeDivisor_ExponentZeroPaysEverything_HalfIsSqrt()
    {
        PaceMath.IncomeDivisor(400, 100, 0.0, out var n, out var d);
        Assert.Equal((1L, 1L), (n, d));
        PaceMath.IncomeDivisor(400, 100, 0.5, out n, out d);
        Assert.Equal(2.0, (double)n / d, 5);
        PaceMath.IncomeDivisor(100, 100, 0.5, out n, out d);
        Assert.Equal((1L, 1L), (n, d));
        PaceMath.IncomeDivisor(50, 100, 1.0, out n, out d);   // k < 1 is impossible: treated as 1
        Assert.Equal((1L, 1L), (n, d));
        PaceMath.IncomeDivisor(400, 100, double.NaN, out n, out d);
        Assert.Equal((1L, 1L), (n, d));
    }

    // ---- A6: change watch ----

    [Fact]
    public void ChangeWatch_ReportsOnlyChanges()
    {
        var w = new ChangeWatch<int>();
        Assert.False(w.Update(1));       // first sight just records
        Assert.False(w.Update(1));
        Assert.True(w.Update(2));
        Assert.False(w.Update(2));
        w.Reset();
        Assert.False(w.Update(3));
    }

    // ---- A5: day stats ----

    [Fact]
    public void DayStats_AccumulatesAndFormats()
    {
        var s = new DayStats();
        s.Begin(7);
        Assert.False(s.HasActivity);
        s.AddTruck(120); s.AddTruck(130);
        s.AddKill(); s.AddKill(); s.AddKill();
        s.AddCanUnits(40);
        s.AddMoney(1000, 400); s.AddMoney(10, 4);
        s.AddSample(300, 0); s.AddSample(500, 30); s.AddSample(420, 10);
        Assert.True(s.HasActivity);
        Assert.Equal(2, s.Trucks);
        Assert.Equal(250, s.Spawned);
        Assert.Equal(3, s.Kills);
        Assert.Equal(1010, s.MoneyIn);
        Assert.Equal(404, s.MoneyOut);
        Assert.Equal(500, s.MaxEnemies);
        Assert.Equal(30, s.QueueMax);
        Assert.Equal(40.0 / 3, s.QueueMean, 6);
        string line = s.Format(2, 250, 435, 1.74, "host");
        Assert.Contains("day=7", line);
        Assert.Contains("eEff=435", line);
        Assert.Contains("trucks=2", line);
        Assert.Contains("spawned=250", line);
        Assert.Contains("kills=3", line);
        Assert.Contains("canUnits=40", line);
        Assert.Contains("moneyIn=1010", line);
        Assert.Contains("moneyOut=404", line);
        Assert.Contains("max=30", line);
        s.Begin(8);
        Assert.False(s.HasActivity);
        Assert.Equal(0, s.MoneyIn);
    }
}
