using System.Collections.Generic;
using CanEmAllCoop.Flow;
using Xunit;

namespace CanEmAllCoop.Tests;

public class LoopStateTests
{
    [Theory]
    [InlineData(5, true), InlineData(6, true), InlineData(4, false), InlineData(7, false)]
    public void OutsideWait(int s, bool expected) => Assert.Equal(expected, LoopStates.IsOutsideWait(s));

    [Theory]
    [InlineData(15, true), InlineData(16, true), InlineData(14, false), InlineData(17, false)]
    public void InsideWait(int s, bool expected) => Assert.Equal(expected, LoopStates.IsInsideWait(s));

    [Theory]
    [InlineData(21, true), InlineData(22, true), InlineData(20, false), InlineData(9, false), InlineData(103, false)]
    public void ShopWait(int s, bool expected) => Assert.Equal(expected, LoopStates.IsShopWait(s));

    [Theory]
    [InlineData(3, true), InlineData(4, true), InlineData(2, false), InlineData(21, false)]
    public void SuicideShopWait(int s, bool expected) => Assert.Equal(expected, LoopStates.IsSuicideShopWait(s));

    [Fact]
    public void OnlyDayAndNightAreTimed()
    {
        for (int s = -1; s < 130; s++)
            Assert.Equal(s == 9 || s == 10, LoopStates.IsTimed(s));
    }

    [Fact]
    public void FlowEncodesSuicideAboveEveryLoopState()
    {
        Assert.Equal(7, LoopStates.Flow(7, -1));
        Assert.Equal(103, LoopStates.Flow(-1, 3));
        Assert.Equal(100, LoopStates.Flow(9, 0));
        Assert.Equal(FlowPhase.Shop, LoopStates.PhaseOfFlow(103));
        Assert.Equal(FlowPhase.Dead, LoopStates.PhaseOfFlow(101));
    }

    [Theory]
    [InlineData(-1, FlowPhase.None), InlineData(0, FlowPhase.ElevatorIn), InlineData(6, FlowPhase.WaitingToStart),
     InlineData(8, FlowPhase.Day), InlineData(9, FlowPhase.Day), InlineData(10, FlowPhase.Night),
     InlineData(14, FlowPhase.DayEnding), InlineData(21, FlowPhase.Shop)]
    public void Phases(int s, FlowPhase expected) => Assert.Equal(expected, LoopStates.PhaseOfLoop(s));

    [Fact]
    public void DayInProgress()
    {
        Assert.False(LoopStates.IsDayInProgress(7));
        Assert.True(LoopStates.IsDayInProgress(8));
        Assert.True(LoopStates.IsDayInProgress(10));
        Assert.False(LoopStates.IsDayInProgress(11));
        Assert.False(LoopStates.IsDayInProgress(109));
    }
}

public class BarrierPhaseTests
{
    [Theory]
    [InlineData(0, BarrierTiming.Open), InlineData(6, BarrierTiming.Open),
     InlineData(7, BarrierTiming.Late), InlineData(9, BarrierTiming.Late), InlineData(10, BarrierTiming.Late),
     InlineData(11, BarrierTiming.Early), InlineData(21, BarrierTiming.Early), InlineData(102, BarrierTiming.Early)]
    public void Outside(int hostFlow, BarrierTiming expected) =>
        Assert.Equal(expected, BarrierPhase.Classify(BarrierKind.Outside, hostFlow));

    [Theory]
    [InlineData(9, BarrierTiming.Early), InlineData(11, BarrierTiming.Open), InlineData(16, BarrierTiming.Open),
     InlineData(17, BarrierTiming.Late), InlineData(21, BarrierTiming.Late), InlineData(103, BarrierTiming.Late)]
    public void Inside(int hostFlow, BarrierTiming expected) =>
        Assert.Equal(expected, BarrierPhase.Classify(BarrierKind.Inside, hostFlow));

    [Theory]
    [InlineData(5, BarrierTiming.Late), InlineData(18, BarrierTiming.Late), InlineData(19, BarrierTiming.Open),
     InlineData(22, BarrierTiming.Open), InlineData(100, BarrierTiming.Early), InlineData(101, BarrierTiming.Early),
     InlineData(102, BarrierTiming.Open), InlineData(104, BarrierTiming.Open), InlineData(105, BarrierTiming.Late)]
    public void Ready(int hostFlow, BarrierTiming expected) =>
        Assert.Equal(expected, BarrierPhase.Classify(BarrierKind.Ready, hostFlow));

    [Fact]
    public void EndingIsAlwaysOpen()
    {
        foreach (var f in new[] { -1, 0, 9, 21, 103 })
            Assert.Equal(BarrierTiming.Open, BarrierPhase.Classify(BarrierKind.Ending, f));
    }
}

public class QuorumTests
{
    [Theory]
    [InlineData(QuorumPolicy.All, 1, 1), InlineData(QuorumPolicy.All, 4, 4),
     InlineData(QuorumPolicy.Majority, 2, 2), InlineData(QuorumPolicy.Majority, 3, 2), InlineData(QuorumPolicy.Majority, 4, 3),
     InlineData(QuorumPolicy.AllWithTimeout, 3, 3), InlineData(QuorumPolicy.All, 0, 1)]
    public void Required(QuorumPolicy policy, int expected, int required) =>
        Assert.Equal(required, Quorum.Required(policy, expected));

    [Fact]
    public void AllNeedsEveryone()
    {
        Assert.False(Quorum.IsMet(QuorumPolicy.All, 3, 2, 1000, 60));
        Assert.True(Quorum.IsMet(QuorumPolicy.All, 3, 3, 0, 60));
    }

    [Fact]
    public void MajorityNeedsMoreThanHalf()
    {
        Assert.False(Quorum.IsMet(QuorumPolicy.Majority, 4, 2, 0, 60));
        Assert.True(Quorum.IsMet(QuorumPolicy.Majority, 4, 3, 0, 60));
        Assert.False(Quorum.IsMet(QuorumPolicy.Majority, 2, 1, 0, 60));
    }

    [Fact]
    public void TimeoutStartsAtTheFirstReportAndNeedsAtLeastOne()
    {
        Assert.False(Quorum.IsMet(QuorumPolicy.AllWithTimeout, 3, 1, 59.9, 60));
        Assert.True(Quorum.IsMet(QuorumPolicy.AllWithTimeout, 3, 1, 60, 60));
        Assert.False(Quorum.IsMet(QuorumPolicy.AllWithTimeout, 3, 0, 600, 60));
    }

    [Fact]
    public void BoardCountsOnlyExpectedPeersAndResets()
    {
        var board = new BarrierBoard();
        var expected = new List<ulong> { 1, 2, 3 };
        board.Report(BarrierKind.Ready, 1, 10.0);
        board.Report(BarrierKind.Ready, 99, 11.0);        // not expected: ignored
        Assert.Equal(1, board.CountIn(BarrierKind.Ready, expected));
        Assert.False(board.IsMet(BarrierKind.Ready, QuorumPolicy.All, expected, 12.0, 60));
        board.Report(BarrierKind.Ready, 2, 12.0);
        board.Report(BarrierKind.Ready, 3, 13.0);
        Assert.True(board.IsMet(BarrierKind.Ready, QuorumPolicy.All, expected, 13.0, 60));
        board.Reset(BarrierKind.Ready);
        Assert.False(board.Any(BarrierKind.Ready));
    }

    [Fact]
    public void BoardTimeoutIsMeasuredFromTheFirstReport()
    {
        var board = new BarrierBoard();
        var expected = new List<ulong> { 1, 2 };
        board.Report(BarrierKind.Ready, 1, 100.0);
        Assert.False(board.IsMet(BarrierKind.Ready, QuorumPolicy.AllWithTimeout, expected, 159.0, 60));
        Assert.True(board.IsMet(BarrierKind.Ready, QuorumPolicy.AllWithTimeout, expected, 160.0, 60));
    }

    [Fact]
    public void BoardForgetsALeavingPeerEverywhere()
    {
        var board = new BarrierBoard();
        board.Report(BarrierKind.Inside, 7, 1.0);
        board.Report(BarrierKind.Ready, 7, 1.0);
        board.Remove(7);
        Assert.False(board.Has(BarrierKind.Inside, 7));
        Assert.False(board.Has(BarrierKind.Ready, 7));
    }

    [Fact]
    public void BoardKindsAreIndependent()
    {
        var board = new BarrierBoard();
        board.Report(BarrierKind.Outside, 1, 0);
        Assert.True(board.Any(BarrierKind.Outside));
        Assert.False(board.Any(BarrierKind.Inside));
        board.ResetAll();
        Assert.False(board.Any(BarrierKind.Outside));
    }
}

public class DownTrackerTests
{
    [Fact]
    public void TeamWipeNeedsEveryPlayerOfTheDay()
    {
        var t = new DownTracker();
        var players = new List<ulong> { 1, 2, 3 };
        Assert.True(t.MarkDown(1));
        Assert.False(t.MarkDown(1));
        Assert.False(t.TeamWiped(players));
        t.MarkDown(2);
        Assert.False(t.TeamWiped(players));
        t.MarkDown(3);
        Assert.True(t.TeamWiped(players));
        Assert.True(t.MarkUp(2));
        Assert.False(t.TeamWiped(players));
    }

    [Fact]
    public void SoloPlayerDyingIsATeamWipe()
    {
        var t = new DownTracker();
        t.MarkDown(5);
        Assert.True(t.TeamWiped(new List<ulong> { 5 }));
    }

    [Fact]
    public void NoPlayersIsNeverAWipe()
    {
        var t = new DownTracker();
        Assert.False(t.TeamWiped(new List<ulong>()));
    }

    [Fact]
    public void PlayersWhoAreNotInTheDayDoNotCount()
    {
        var t = new DownTracker();
        t.MarkDown(1);
        t.MarkDown(99);                                   // a late joiner that is not part of this day
        Assert.True(t.TeamWiped(new List<ulong> { 1 }));
    }

    [Fact]
    public void ClearForgetsEveryone()
    {
        var t = new DownTracker();
        t.MarkDown(1);
        t.Clear();
        Assert.Equal(0, t.Count);
    }
}

public class TimerSlaveTests
{
    [Fact]
    public void NonTimedStatesAreLeftAlone()
    {
        var d = TimerSlave.Decide(7, 9, 0f, true, 9, 100f);
        Assert.Equal(TimerAction.None, d.Action);
        Assert.Equal(TimerAction.None, TimerSlave.Decide(21, 21, 0f, false, -1, 0f).Action);
    }

    [Fact]
    public void SameStateAdoptsTheFreshHostTimer()
    {
        var d = TimerSlave.Decide(9, 9, 50f, true, 9, 73.5f);
        Assert.Equal(TimerAction.Set, d.Action);
        Assert.Equal(73.5f, d.Value);
    }

    [Fact]
    public void HostTimerIsNeverAllowedBelowTheFloor()
    {
        var d = TimerSlave.Decide(9, 9, 5f, true, 9, 0f);
        Assert.Equal(TimerAction.Set, d.Action);
        Assert.Equal(TimerSlave.Floor, d.Value);
    }

    [Fact]
    public void StaleTimerFromAnotherStateIsIgnored()
    {
        // host timer belongs to the day (9) but we are already in the night (10) and the host is too
        var d = TimerSlave.Decide(10, 10, 30f, true, 9, 3f);
        Assert.Equal(TimerAction.None, d.Action);
    }

    [Fact]
    public void LocalClockIsHeldAboveZeroWhileTheHostIsStillInThePhase()
    {
        var d = TimerSlave.Decide(9, 9, 0.1f, false, -1, 0f);
        Assert.Equal(TimerAction.Set, d.Action);
        Assert.Equal(TimerSlave.Floor, d.Value);
        Assert.Equal(TimerAction.None, TimerSlave.Decide(9, 9, 20f, false, -1, 0f).Action);
    }

    [Fact]
    public void HostAheadForcesTheLocalPhaseToEnd()
    {
        var d = TimerSlave.Decide(9, 10, 12f, false, -1, 0f);
        Assert.Equal(TimerAction.ForceZero, d.Action);
        Assert.Equal(TimerAction.None, TimerSlave.Decide(9, 10, 0f, false, -1, 0f).Action);
        Assert.Equal(TimerAction.ForceZero, TimerSlave.Decide(10, 21, 9f, false, -1, 0f).Action);
        Assert.Equal(TimerAction.ForceZero, TimerSlave.Decide(9, 103, 9f, false, -1, 0f).Action);
    }

    [Fact]
    public void UnknownHostKeepsTheClockAlive()
    {
        var d = TimerSlave.Decide(9, -1, 0f, false, -1, 0f);
        Assert.Equal(TimerAction.Set, d.Action);
    }
}
