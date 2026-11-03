using System.Collections.Generic;
using CanEmAllCoop.Core;
using Xunit;

namespace CanEmAllCoop.Tests;

public class LogGateTests
{
    [Fact]
    public void First_events_pass_then_one_per_interval()
    {
        var g = new LogGate(burst: 2, interval: 10f);
        Assert.True(g.Allow("k", 0f, out var s0));
        Assert.Equal(0, s0);
        Assert.True(g.Allow("k", 0.1f, out _));
        Assert.False(g.Allow("k", 1f, out _));
        Assert.False(g.Allow("k", 9f, out _));
        Assert.True(g.Allow("k", 10.2f, out var sup));
        Assert.Equal(2, sup);
        Assert.False(g.Allow("k", 11f, out _));
    }

    [Fact]
    public void Keys_are_independent()
    {
        var g = new LogGate(burst: 1, interval: 100f);
        Assert.True(g.Allow("a", 0f, out _));
        Assert.False(g.Allow("a", 1f, out _));
        Assert.True(g.Allow("b", 1f, out _));
    }

    [Fact]
    public void Clear_forgets_everything()
    {
        var g = new LogGate(burst: 1, interval: 100f);
        Assert.True(g.Allow("a", 0f, out _));
        g.Clear();
        Assert.True(g.Allow("a", 1f, out _));
    }
}

public class DwellTrackerTests
{
    [Fact]
    public void Reports_once_after_the_threshold()
    {
        var d = new DwellTracker();
        Assert.False(d.Inside(7, 0f, 4f));
        Assert.False(d.Inside(7, 3.9f, 4f));
        Assert.True(d.Inside(7, 4f, 4f));
        Assert.False(d.Inside(7, 8f, 4f));
    }

    [Fact]
    public void Leaving_resets_the_stay()
    {
        var d = new DwellTracker();
        d.Inside(1, 0f, 4f);
        d.Prune(new HashSet<uint>());           // left the area
        Assert.Equal(0, d.Count);
        Assert.False(d.Inside(1, 5f, 4f));      // a new stay starts now
        Assert.True(d.Inside(1, 9.5f, 4f));
    }

    [Fact]
    public void Prune_keeps_ids_that_are_still_inside()
    {
        var d = new DwellTracker();
        d.Inside(1, 0f, 4f);
        d.Inside(2, 0f, 4f);
        d.Prune(new HashSet<uint> { 2 });
        Assert.Equal(1, d.Count);
        Assert.True(d.Inside(2, 5f, 4f));
    }

    [Fact]
    public void Forget_allows_a_second_report()
    {
        var d = new DwellTracker();
        d.Inside(3, 0f, 1f);
        Assert.True(d.Inside(3, 2f, 1f));
        d.Forget(3);
        d.Inside(3, 3f, 1f);
        Assert.True(d.Inside(3, 5f, 1f));
    }
}
