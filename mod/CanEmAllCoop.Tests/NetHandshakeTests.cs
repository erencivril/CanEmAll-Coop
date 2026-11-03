using CanEmAllCoop.Net;
using Xunit;

namespace CanEmAllCoop.Tests;

public class NetHandshakeTests
{
    [Fact]
    public void SameVersionsAreAccepted()
    {
        Assert.Null(Handshake.Check(2, "0.1.0", 2, "0.1.0"));
    }

    [Fact]
    public void DifferentModVersionIsRefusedWithBothVersionsInTheText()
    {
        var msg = Handshake.Check(2, "0.1.0", 2, "0.2.3");
        Assert.NotNull(msg);
        Assert.Equal("Version mismatch: host has 0.1.0, you have 0.2.3 - install the same mod version", msg);
    }

    [Fact]
    public void DifferentProtocolIsRefusedEvenWithEqualModVersion()
    {
        var msg = Handshake.Check(2, "0.1.0", 1, "0.1.0");
        Assert.NotNull(msg);
        Assert.StartsWith("Version mismatch", msg);
        Assert.Contains("host 2, you 1", msg);
    }

    [Fact]
    public void ProtocolIsCheckedBeforeTheModVersion()
    {
        // an old client's Hello has no mod version; the host passes a placeholder and must still report the protocol
        var msg = Handshake.Check(2, "0.1.0", 1, "unknown");
        Assert.Contains("protocol", msg);
    }

    [Fact]
    public void ModVersionComparisonIsExact()
    {
        Assert.NotNull(Handshake.Check(2, "0.1.0", 2, "0.1.0 "));
        Assert.NotNull(Handshake.Check(2, "0.1.0", 2, "0.1.00"));
        Assert.NotNull(Handshake.Check(2, "0.1.0", 2, null));
    }

    [Fact]
    public void ClientShowsVersionRefusalsVerbatimAndPrefixesOthers()
    {
        var v = Handshake.Check(2, "0.1.0", 2, "9.9");
        Assert.Equal(v, Handshake.ClientRefuseMessage(v));
        Assert.Equal("Host refused: session is full", Handshake.ClientRefuseMessage("session is full"));
    }
}
