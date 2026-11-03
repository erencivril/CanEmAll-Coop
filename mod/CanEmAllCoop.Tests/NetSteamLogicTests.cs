using System.Collections.Generic;
using System.Runtime.InteropServices;
using CanEmAllCoop.Net;
using Xunit;

namespace CanEmAllCoop.Tests;

public class NetSteamLogicTests
{
    // ------------------------------------------------------------------ SteamIds

    // Synthetic ids: the 7656119 prefix is split off so no full 17-digit id sits in the source.
    const string P = "7656119";
    const ulong Synthetic = 7656119_8000000042UL;

    [Theory]
    [InlineData(P + "8000000042", Synthetic)]
    [InlineData("  " + P + "8000000042  ", Synthetic)]
    [InlineData("\"" + P + "8000000042\"", Synthetic)]
    [InlineData("https://steamcommunity.com/profiles/" + P + "8000000042", Synthetic)]
    [InlineData("https://steamcommunity.com/profiles/" + P + "8000000042/", Synthetic)]
    [InlineData("steamid: " + P + "7960265729", 7656119_7960265729UL)]
    public void ParsesSteamId64(string text, ulong expected)
    {
        Assert.True(SteamIds.TryParse(text, out var id));
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello")]
    [InlineData("1234567")]
    [InlineData("765611981074256")]                        // 15 digits
    [InlineData(P + "80000000421")]                        // 18 digits
    [InlineData("12345678901234567")]                      // 17 digits but not a SteamID64
    [InlineData("https://steamcommunity.com/id/somebody")] // vanity URL cannot be resolved offline
    public void RejectsWhatIsNotASteamId(string text)
    {
        Assert.False(SteamIds.TryParse(text, out var id));
        Assert.Equal(0UL, id);
    }

    [Fact]
    public void PlausibleRangeIsTheIndividualAccountRange()
    {
        Assert.False(SteamIds.IsPlausible(0));
        Assert.False(SteamIds.IsPlausible(SteamIds.IndividualBase - 1));
        Assert.True(SteamIds.IsPlausible(SteamIds.IndividualBase));
        Assert.True(SteamIds.IsPlausible(SteamIds.IndividualMax));
        Assert.False(SteamIds.IsPlausible(SteamIds.IndividualMax + 1));
    }

    // ------------------------------------------------------------------ state names

    [Fact]
    public void ConnectionStatesHaveNamesAndUnknownOnesKeepTheirNumber()
    {
        Assert.Equal("finding a route", SteamStates.ConnState(2));
        Assert.Equal("connected", SteamStates.ConnState(3));
        Assert.Equal("closed by the other side", SteamStates.ConnState(4));
        Assert.Equal("state 77", SteamStates.ConnState(77));
    }

    [Fact]
    public void EndReasonsAlwaysCarryTheirCode()
    {
        Assert.Equal("code 0", SteamStates.EndReason(0));
        Assert.Contains("(5003)", SteamStates.EndReason(5003));
        Assert.Contains("timed out", SteamStates.EndReason(5003));
        Assert.Contains("closed by the application", SteamStates.EndReason(1042));
        Assert.Equal("code 9999", SteamStates.EndReason(9999));
    }

    [Fact]
    public void SendResultsAreReadable()
    {
        Assert.Equal("OK", SteamStates.EResult(1));
        Assert.Equal("send buffer full", SteamStates.EResult(25));
        Assert.Equal("EResult 999", SteamStates.EResult(999));
        Assert.Equal("ready", SteamStates.Availability(100));
        Assert.Equal("failed", SteamStates.Availability(-101));
    }

    // ------------------------------------------------------------------ SteamNetConnectionInfo_t layout

    // Transcription of Steamworks.NET's SteamNetConnectionInfo_t (x64, Pack = 8; SteamNetworkingIPAddr is Pack = 1, 18 bytes).
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    struct MirrorIdentity
    {
        public int Type;
        public int Size;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] Data;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct MirrorIpAddr
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Ip;
        public ushort Port;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    struct MirrorConnInfo
    {
        public MirrorIdentity IdentityRemote;
        public long UserData;
        public uint ListenSocket;
        public MirrorIpAddr AddrRemote;
        public ushort Pad1;
        public uint IdPopRemote;
        public uint IdPopRelay;
        public int State;
        public int EndReason;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] EndDebug;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] Description;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 63)] public uint[] Reserved;
    }

    [Fact]
    public void ConnectionInfoOffsetsMatchTheSteamworksNetLayout()
    {
        int Off(string f) => (int)Marshal.OffsetOf<MirrorConnInfo>(f);
        Assert.Equal(SteamConnInfoLayout.Size, Marshal.SizeOf<MirrorConnInfo>());
        Assert.Equal(SteamConnInfoLayout.IdentityRemote, Off(nameof(MirrorConnInfo.IdentityRemote)));
        Assert.Equal(SteamConnInfoLayout.UserData, Off(nameof(MirrorConnInfo.UserData)));
        Assert.Equal(SteamConnInfoLayout.ListenSocket, Off(nameof(MirrorConnInfo.ListenSocket)));
        Assert.Equal(SteamConnInfoLayout.AddrRemote, Off(nameof(MirrorConnInfo.AddrRemote)));
        Assert.Equal(SteamConnInfoLayout.PopRemote, Off(nameof(MirrorConnInfo.IdPopRemote)));
        Assert.Equal(SteamConnInfoLayout.PopRelay, Off(nameof(MirrorConnInfo.IdPopRelay)));
        Assert.Equal(SteamConnInfoLayout.State, Off(nameof(MirrorConnInfo.State)));
        Assert.Equal(SteamConnInfoLayout.EndReason, Off(nameof(MirrorConnInfo.EndReason)));
        Assert.Equal(SteamConnInfoLayout.EndDebug, Off(nameof(MirrorConnInfo.EndDebug)));
        Assert.Equal(SteamConnInfoLayout.Description, Off(nameof(MirrorConnInfo.Description)));
        Assert.Equal(SteamConnInfoLayout.Flags, Off(nameof(MirrorConnInfo.Flags)));
    }

    // ------------------------------------------------------------------ send failures

    [Fact]
    public void FailuresTripOnlyAfterEnoughInARowAndLongEnough()
    {
        var t = new SendFailureTracker(threshold: 5, minSpanMs: 1000);
        long now = 10_000;
        for (int i = 0; i < 4; i++) Assert.False(t.Record(false, now += 100));
        Assert.False(t.Record(false, now += 100));     // 5 in a row but only 500 ms
        Assert.False(t.Record(false, now += 100));
        Assert.True(t.Record(false, now += 600));      // >= 1000 ms since the first failure
        Assert.True(t.Consecutive >= 5);
    }

    [Fact]
    public void ASuccessResetsTheStreak()
    {
        var t = new SendFailureTracker(threshold: 3, minSpanMs: 0);
        t.Record(false, 1); t.Record(false, 2);
        Assert.False(t.Record(true, 3));
        Assert.Equal(0, t.Consecutive);
        Assert.False(t.Record(false, 4));
        Assert.False(t.Record(false, 5));
        Assert.True(t.Record(false, 6));
    }

    // ------------------------------------------------------------------ stall guard

    [Fact]
    public void StallGuardReportsLongGapsOnly()
    {
        var g = new StallGuard(1000);
        Assert.Equal(0, g.Tick(5000));       // first call: nothing to compare with
        Assert.Equal(0, g.Tick(5016));       // normal frame
        Assert.Equal(0, g.Tick(5900));       // 884 ms: below the threshold
        Assert.Equal(12_000, g.Tick(17_900)); // 12 s hitch
        Assert.Equal(0, g.Tick(17_916));
        g.Reset();
        Assert.Equal(0, g.Tick(60_000));
    }

    // ------------------------------------------------------------------ removed peers

    [Fact]
    public void RemovedPeerIsBlockedOnlyForTheGracePeriod()
    {
        var g = new RemovedPeerGuard(3000);
        Assert.False(g.IsBlocked(42, 1000));
        g.Removed(42, 1000);
        Assert.True(g.IsBlocked(42, 1000));
        Assert.True(g.IsBlocked(42, 3999));
        Assert.False(g.IsBlocked(42, 4000));
        Assert.False(g.IsBlocked(42, 4001));
        Assert.False(g.IsBlocked(7, 1500));
    }

    [Fact]
    public void ReaddedPeerIsNoLongerBlocked()
    {
        var g = new RemovedPeerGuard(3000);
        g.Removed(42, 0);
        g.Readded(42);
        Assert.False(g.IsBlocked(42, 1));
    }

    // ------------------------------------------------------------------ deferred close

    [Fact]
    public void DeferredCloseFiresWhenDueAndOnlyOnce()
    {
        var d = new DeferredCloseSchedule();
        var due = new List<ulong>();
        d.Schedule(1, 1000);
        d.Schedule(2, 2000);
        d.CollectDue(999, due);
        Assert.Empty(due);
        d.CollectDue(1000, due);
        Assert.Equal(new ulong[] { 1 }, due);
        due.Clear();
        d.CollectDue(1500, due);
        Assert.Empty(due);
        d.CollectDue(2500, due);
        Assert.Equal(new ulong[] { 2 }, due);
        Assert.Equal(0, d.Count);
    }

    [Fact]
    public void ARejoinCancelsThePendingClose()
    {
        var d = new DeferredCloseSchedule();
        var due = new List<ulong>();
        d.Schedule(5, 1000);
        d.Cancel(5);
        d.CollectDue(5000, due);
        Assert.Empty(due);
    }

    // ------------------------------------------------------------------ retry gate

    [Fact]
    public void RetryGateFiresOncePerInterval()
    {
        var g = new RetryGate(2000);
        Assert.True(g.Ready(100));
        Assert.False(g.Ready(1000));
        Assert.False(g.Ready(2099));
        Assert.True(g.Ready(2100));
        g.Reset();
        Assert.True(g.Ready(2101));
    }

    // ------------------------------------------------------------------ send flags

    [Fact]
    public void CoreControlMessagesUseReliableNoNagleWithAutoRestart()
    {
        foreach (var id in new[] { CoreMsg.Hello, CoreMsg.Welcome, CoreMsg.PeerList, CoreMsg.Bye, CoreMsg.Ping, CoreMsg.Pong, CoreMsg.Refuse })
            Assert.Equal(9 | 32, SteamSendFlags.For(PacketHeader.Make(id, true), true));
    }

    [Fact]
    public void FragmentsAndUserMessagesUsePlainReliable()
    {
        Assert.Equal(8 | 32, SteamSendFlags.For(PacketHeader.Make(CoreMsg.Fragment, true), true));
        Assert.Equal(8 | 32, SteamSendFlags.For(PacketHeader.Make(CoreMsg.FirstUserId, true), true));
        Assert.Equal(8 | 32, SteamSendFlags.For(PacketHeader.Make(1234, true, relay: true), true));
    }

    [Fact]
    public void UnreliableStreamsUseNoDelayWithAutoRestart()
    {
        Assert.Equal(5 | 32, SteamSendFlags.For(PacketHeader.Make(150, false), false));
        // an unreliable flag wins even for a (never sent that way) core id
        Assert.Equal(5 | 32, SteamSendFlags.For(PacketHeader.Make(CoreMsg.Ping, false), false));
    }

    // ------------------------------------------------------------------ friend order

    [Fact]
    public void FriendsAreRankedHostingThenInGameThenOnlineThenRest()
    {
        Assert.Equal(0, FriendOrder.Rank(true, true, 1));
        Assert.Equal(0, FriendOrder.Rank(true, false, 0));
        Assert.Equal(1, FriendOrder.Rank(false, true, 1));
        Assert.Equal(2, FriendOrder.Rank(false, false, 1));
        Assert.Equal(3, FriendOrder.Rank(false, false, 0));
    }

    [Fact]
    public void FriendsSortByRankThenName()
    {
        var list = new List<(int rank, string name)>
        {
            (3, "Anna"), (0, "Zed"), (2, "bob"), (0, "amy"), (1, "Carl"), (2, "Alice"),
        };
        list.Sort((a, b) => FriendOrder.Compare(a.rank, a.name, b.rank, b.name));
        Assert.Equal(new[] { "amy", "Zed", "Carl", "Alice", "bob", "Anna" }, list.ConvertAll(x => x.name));
    }
}
