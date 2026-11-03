using CanEmAllCoop.Core;
using Xunit;

namespace CanEmAllCoop.Tests;

// LaunchArgs is static state: keep these tests in one class so xunit runs them sequentially.
public class LaunchArgsTests
{
    [Fact]
    public void NoArgsMeansDefaults()
    {
        LaunchArgs.Parse(new[] { "game.exe", "-batchmode" });
        Assert.False(LaunchArgs.HostDirect);
        Assert.False(LaunchArgs.JoinDirect);
        Assert.False(LaunchArgs.AutoStart);
        Assert.Null(LaunchArgs.StateLogSeconds);
    }

    [Fact]
    public void HostDirectWithAndWithoutPort()
    {
        LaunchArgs.Parse(new[] { "--coop-host-direct" });
        Assert.True(LaunchArgs.HostDirect);
        Assert.Equal(0, LaunchArgs.HostDirectPort);

        LaunchArgs.Parse(new[] { "--coop-host-direct=30000" });
        Assert.True(LaunchArgs.HostDirect);
        Assert.Equal(30000, LaunchArgs.HostDirectPort);
    }

    [Fact]
    public void JoinDirectParsesEndpoint()
    {
        LaunchArgs.Parse(new[] { "--coop-join-direct=192.168.1.20:27555", "--coop-autostart", "--coop-statelog=2.5" });
        Assert.True(LaunchArgs.JoinDirect);
        Assert.Equal("192.168.1.20", LaunchArgs.JoinHost);
        Assert.Equal(27555, LaunchArgs.JoinPort);
        Assert.True(LaunchArgs.AutoStart);
        Assert.Equal(2.5f, LaunchArgs.StateLogSeconds);
    }

    [Theory]
    [InlineData("--coop-join-direct=")]
    [InlineData("--coop-join-direct=host:")]
    [InlineData("--coop-join-direct=host:99999")]
    [InlineData("--coop-join-direct=:27555")]
    public void BadJoinEndpointIsIgnored(string arg)
    {
        LaunchArgs.Parse(new[] { arg });
        Assert.False(LaunchArgs.JoinDirect);
    }

    [Fact]
    public void JoinWithoutPortUsesDefaultPortMarker()
    {
        LaunchArgs.Parse(new[] { "--coop-join-direct=localhost" });
        Assert.True(LaunchArgs.JoinDirect);
        Assert.Equal("localhost", LaunchArgs.JoinHost);
        Assert.Equal(0, LaunchArgs.JoinPort);
    }

    [Fact]
    public void ParseResetsPreviousState()
    {
        LaunchArgs.Parse(new[] { "--coop-autostart" });
        LaunchArgs.Parse(new string[0]);
        Assert.False(LaunchArgs.AutoStart);
    }

    [Fact]
    public void NegativeStateLogIsIgnored()
    {
        LaunchArgs.Parse(new[] { "--coop-statelog=-3" });
        Assert.Null(LaunchArgs.StateLogSeconds);
    }
}
