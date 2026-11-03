namespace CanEmAllCoop.Net;

/// <summary>Core message ids (0-99 belong to foundation; feature modules own the other ranges, see DESIGN section 2).</summary>
public static class CoreMsg
{
    public const ushort Hello = 0;     // client -> host: ushort protocol version, string name, string mod version
    public const ushort Welcome = 1;   // host -> client: byte index, ulong yourId, peer list
    public const ushort PeerList = 2;  // host -> clients: peer list
    public const ushort Bye = 3;       // either way: leaving
    public const ushort Ping = 4;      // long stamp
    public const ushort Pong = 5;      // long stamp (echo)
    public const ushort Fragment = 6;  // see Framing
    public const ushort Refuse = 7;    // host -> client: string reason (version mismatch, session full)

    public const ushort FirstUserId = 100;
}

/// <summary>
/// Every packet starts with a ushort header: low 13 bits = message id, high bits = routing flags.
/// Client -> host: Relay = "deliver to you AND forward to every other client", Directed = "forward to the client
/// with the byte index that follows the header". Host -> client: Relay = "originated from the peer whose byte
/// index follows the header" (otherwise the packet is from the host itself).
/// </summary>
public static class PacketHeader
{
    public const ushort IdMask = 0x1FFF;
    public const ushort Unreliable = 0x2000;
    public const ushort Directed = 0x4000;
    public const ushort Relay = 0x8000;

    public const int Size = 2;

    public static ushort Id(ushort header) => (ushort)(header & IdMask);
    public static bool IsUnreliable(ushort header) => (header & Unreliable) != 0;
    public static bool IsDirected(ushort header) => (header & Directed) != 0;
    public static bool IsRelay(ushort header) => (header & Relay) != 0;

    public static ushort Make(ushort msgId, bool reliable, bool relay = false, bool directed = false)
    {
        int h = msgId & IdMask;
        if (!reliable) h |= Unreliable;
        if (relay) h |= Relay;
        if (directed) h |= Directed;
        return (ushort)h;
    }
}

public static class Protocol
{
    /// <summary>Bumped whenever the wire format of core messages changes; Hello with another version is refused.</summary>
    public const ushort Version = 2;   // 2: Hello carries the mod version string after the name

    public const int MaxPlayers = 4;
    /// <summary>Defaults; the live values come from CoopConfig [Network] (PeerTimeoutSeconds / SteamConnectTimeoutSeconds).</summary>
    public const double PeerTimeoutSeconds = 30.0;
    public const double SteamConnectTimeoutSeconds = 25.0;
    public const double DirectConnectTimeoutSeconds = 15.0;
    public const double PingIntervalSeconds = 2.0;
    /// <summary>A joining client repeats Hello this often until the Welcome arrives (Steam sessions can drop early messages).</summary>
    public const double HelloRetrySeconds = 2.5;
}

/// <summary>Host-side handshake check. Returns null when the client may join, else the text for the Refuse message.</summary>
public static class Handshake
{
    public static string Check(ushort hostProtocol, string hostMod, ushort clientProtocol, string clientMod)
    {
        if (clientProtocol != hostProtocol)
            return $"Version mismatch: network protocol differs (host {hostProtocol}, you {clientProtocol}) - install the same mod version as the host (host has {hostMod})";
        if (!string.Equals(hostMod, clientMod, System.StringComparison.Ordinal))
            return $"Version mismatch: host has {hostMod}, you have {clientMod} - install the same mod version";
        return null;
    }

    /// <summary>What the client shows for a Refuse: version texts are already complete sentences, other reasons get a prefix.</summary>
    public static string ClientRefuseMessage(string reason) =>
        reason != null && reason.StartsWith("Version mismatch", System.StringComparison.Ordinal) ? reason : "Host refused: " + reason;
}
