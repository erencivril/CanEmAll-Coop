using System.IO;
using CanEmAllCoop.Economy;
using CanEmAllCoop.Net;
using Xunit;

namespace CanEmAllCoop.Tests;

public class EconomyTests
{
    // ------------------------------------------------------------------------------------------ MaxBet

    [Theory]
    [InlineData(1000L, 1.0f, 1000L)]
    [InlineData(1000L, 1.5f, 1000L)]
    [InlineData(1000L, 0.5f, 500L)]
    [InlineData(1000L, 0.333f, 333L)]
    [InlineData(3L, 0.1f, 1L)]      // never below 1 while there is money
    [InlineData(1000L, 0f, 1L)]
    [InlineData(0L, 0.5f, 0L)]
    [InlineData(-5L, 1f, 0L)]
    public void MaxBet(long money, float fraction, long expected) =>
        Assert.Equal(expected, EconomyRules.MaxBet(money, fraction));

    [Fact]
    public void MaxBetNaNMeansUnlimited() => Assert.Equal(77L, EconomyRules.MaxBet(77, float.NaN));

    [Fact]
    public void MaxBetNeverExceedsMoney()
    {
        for (long m = 1; m < 200; m++)
            for (float f = 0.01f; f < 1.2f; f += 0.07f)
                Assert.InRange(EconomyRules.MaxBet(m, f), 1, m);
    }

    // ------------------------------------------------------------------------------------------ validation

    [Fact]
    public void ValidateBuyOrder()
    {
        Assert.Equal(RejectReason.Unknown, EconomyRules.ValidateBuy(false, false, false, 100, 1));
        Assert.Equal(RejectReason.AlreadyPurchased, EconomyRules.ValidateBuy(true, true, true, 0, 99));
        Assert.Equal(RejectReason.Locked, EconomyRules.ValidateBuy(true, false, true, 0, 99));
        Assert.Equal(RejectReason.CannotAfford, EconomyRules.ValidateBuy(true, false, false, 98, 99));
        Assert.Equal(RejectReason.None, EconomyRules.ValidateBuy(true, false, false, 99, 99)); // exact price is affordable
    }

    [Fact]
    public void ValidatePermanent()
    {
        Assert.Equal(RejectReason.Unknown, EconomyRules.ValidatePermanent(false, false, 10, 1));
        Assert.Equal(RejectReason.AlreadyPurchased, EconomyRules.ValidatePermanent(true, true, 10, 1));
        Assert.Equal(RejectReason.CannotAfford, EconomyRules.ValidatePermanent(true, false, 4, 5));
        Assert.Equal(RejectReason.None, EconomyRules.ValidatePermanent(true, false, 5, 5));
    }

    [Fact]
    public void StartingCashIds()
    {
        Assert.False(EconomyRules.IsStartingCash(231));
        Assert.True(EconomyRules.IsStartingCash(232));
        Assert.True(EconomyRules.IsStartingCash(234));
        Assert.False(EconomyRules.IsStartingCash(235));
        Assert.False(EconomyRules.IsStartingCash(1));
    }

    [Fact]
    public void SameSequenceIsOrderSensitive()
    {
        Assert.True(EconomyRules.SameSequence(new[] { 1, 2, 3 }, new[] { 1, 2, 3 }));
        Assert.False(EconomyRules.SameSequence(new[] { 1, 2, 3 }, new[] { 1, 3, 2 })); // SET-type skills depend on order
        Assert.False(EconomyRules.SameSequence(new[] { 1, 2 }, new[] { 1, 2, 3 }));
        Assert.True(EconomyRules.SameSequence(new int[0], new int[0]));
    }

    // ------------------------------------------------------------------------------------------ coalescer / gate

    [Fact]
    public void CoalescerSendsFirstThenRateLimitsThenForces()
    {
        var c = new WalletCoalescer(100);
        Assert.True(c.ShouldSend(10, 10, 1000, false));      // never sent yet
        c.MarkSent(10, 10, 1000);
        Assert.False(c.ShouldSend(10, 10, 5000, false));     // unchanged: silent
        Assert.False(c.ShouldSend(11, 11, 1050, false));     // changed but within 100 ms
        Assert.True(c.ShouldSend(11, 11, 1100, false));      // interval elapsed
        Assert.True(c.ShouldSend(10, 10, 1001, true));       // forced even if unchanged and recent
        c.Reset();
        Assert.True(c.ShouldSend(10, 10, 1002, false));
    }

    [Fact]
    public void CoalescerSeesTotalMoneyOnlyChange()
    {
        var c = new WalletCoalescer(100);
        c.MarkSent(5, 5, 0);
        Assert.True(c.ShouldSend(5, 6, 200, false));
    }

    [Fact]
    public void RequestGateAllowsOnePerKeyUntilReleased()
    {
        var g = new RequestGate();
        Assert.True(g.TryEnter(7, 1000, 1500));
        Assert.False(g.TryEnter(7, 1200, 1500));
        Assert.True(g.TryEnter(8, 1200, 1500));      // other key independent
        g.Release(7);
        Assert.True(g.TryEnter(7, 1201, 1500));
        Assert.True(g.TryEnter(8, 2800, 1500));      // cooldown elapsed
        g.Clear();
        Assert.True(g.TryEnter(7, 1202, 1500));
    }

    // ------------------------------------------------------------------------------------------ codecs

    [Fact]
    public void WalletRoundTrip()
    {
        var w = new NetWriter();
        new WalletMsg { Money = long.MaxValue - 3, TotalMoney = 12345678901L }.Write(w);
        var m = WalletMsg.Read(new NetReader(w.ToArray()));
        Assert.Equal(long.MaxValue - 3, m.Money);
        Assert.Equal(12345678901L, m.TotalMoney);
    }

    [Fact]
    public void PurchaseRoundTrip()
    {
        var w = new NetWriter();
        new PurchaseMsg { SkillId = 233, Money = 5, TotalMoney = 6, Points = 77, Buyer = 2 }.Write(w);
        var m = PurchaseMsg.Read(new NetReader(w.ToArray()));
        Assert.Equal((233, 5L, 6L, 77, 2), (m.SkillId, m.Money, m.TotalMoney, m.Points, m.Buyer));
    }

    [Fact]
    public void SpinGoRoundTrip()
    {
        var w = new NetWriter();
        new SpinGoMsg { Spinner = 3, Bet = 999, Before = 1000, R0 = 1, R1 = 1, R2 = 4 }.Write(w);
        var m = SpinGoMsg.Read(new NetReader(w.ToArray()));
        Assert.Equal((3, 999L, 1000L, 1, 1, 4), (m.Spinner, m.Bet, m.Before, m.R0, m.R1, m.R2));
    }

    [Fact]
    public void SyncRoundTripKeepsOrder()
    {
        var w = new NetWriter();
        new SyncMsg { Points = 12, Day = 4, Skills = new[] { 5, 3, 9 }, Perms = new int[0] }.Write(w);
        var r = new NetReader(w.ToArray());
        var m = SyncMsg.Read(r);
        Assert.Equal(0, r.Remaining);
        Assert.Equal(12, m.Points);
        Assert.Equal(4, m.Day);
        Assert.Equal(new[] { 5, 3, 9 }, m.Skills);
        Assert.Empty(m.Perms);
    }

    [Fact]
    public void SyncWithNullListsWritesEmpty()
    {
        var w = new NetWriter();
        new SyncMsg { Points = 1, Day = 2 }.Write(w);
        var m = SyncMsg.Read(new NetReader(w.ToArray()));
        Assert.Empty(m.Skills);
        Assert.Empty(m.Perms);
    }

    [Fact]
    public void SyncRejectsAbsurdListLength()
    {
        var w = new NetWriter();
        w.Write(0); w.Write(0); w.Write(int.MaxValue);
        Assert.Throws<InvalidDataException>(() => SyncMsg.Read(new NetReader(w.ToArray())));
    }

    [Fact]
    public void MessageIdsStayInEconomyRange()
    {
        foreach (var id in new[]
                 {
                     EconMsg.Wallet, EconMsg.BuyReq, EconMsg.Purchased, EconMsg.Rejected, EconMsg.PermReq, EconMsg.PermPurchased,
                     EconMsg.SellReq, EconMsg.Sold, EconMsg.SpinReq, EconMsg.SpinGo, EconMsg.Snapshot, EconMsg.SnapReq, EconMsg.Sync,
                 })
            Assert.InRange(id, (ushort)100, (ushort)199);
    }
}
