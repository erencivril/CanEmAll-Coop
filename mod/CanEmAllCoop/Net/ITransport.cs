using System;

namespace CanEmAllCoop.Net;

/// <summary>
/// Moves opaque packets between machines. All members are called on the Unity main thread; received packets and
/// disconnects are only raised from inside Update().
/// </summary>
internal interface ITransport : IDisposable
{
    string Kind { get; }

    /// <summary>Remote id of the host as seen by a joining client (Steam: the host's SteamID64, Direct: 1).</summary>
    ulong HostRemoteId { get; }

    /// <summary>Local id when the transport knows it (Steam: own SteamID64, Direct host: 1, Direct client: 0 until Welcome).</summary>
    ulong LocalId { get; }

    /// <summary>False while a client link is still being established (TCP connect). Hello is sent once this is true.</summary>
    bool Ready { get; }

    /// <summary>True when Hello must be re-sent until Welcome arrives (Steam sessions can silently drop early messages).</summary>
    bool NeedsHelloRetry { get; }

    /// <summary>Set when the link cannot be established / was lost for good. Net turns this into an error status.</summary>
    string Error { get; }

    /// <summary>Seconds a joining client waits for the Welcome (counted from the moment Ready turns true).</summary>
    double ConnectTimeoutSeconds { get; }

    /// <summary>Short diagnostics of the link while connecting (Steam: session state, end reason, relay status). Empty when there is nothing to add.</summary>
    string StatusDetail { get; }

    /// <summary>Packets/messages already received but not yet delivered (the per-frame drain is bounded). Receive timeouts are not judged while this is above 0.</summary>
    int Backlog { get; }

    /// <summary>Result of the most recent Send in EResult terms (1 = OK); Direct maps success to 1 and failure to 2.</summary>
    int LastSendResult { get; }

    void Update();

    /// <summary>True when the packet was accepted for transmission (queued / handed to Steam).</summary>
    bool Send(ulong remoteId, byte[] data, int length, bool reliable);

    /// <summary>Host side hint: this remote is part of the session (Steam keeps accepting its session).</summary>
    void OnPeerAdded(ulong remoteId);
    void OnPeerRemoved(ulong remoteId);

    event Action<ulong, byte[]> PacketReceived;
    event Action<ulong> RemoteDisconnected;
}
