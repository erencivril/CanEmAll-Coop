using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using CanEmAllCoop.Core;

namespace CanEmAllCoop.Net;

/// <summary>
/// Session facade (DESIGN section 3). Star topology: clients only talk to the host; the host relays on request
/// (PacketHeader.Relay / Directed). Everything runs on the Unity main thread, pumped by Net.Update().
/// Offline: every Send/Broadcast is a no-op, Peers is empty and PlayerCount is 1.
/// </summary>
public static class Net
{
    sealed class Link
    {
        public Peer Peer;                                   // null on a client until Welcome
        public readonly FragmentAssembler Assembler = new();
        public long LastRecvMs;
        public long LastPingMs;
        public int PingMs = -1;
    }

    static readonly Peer OfflinePeer = new() { Id = 0, Name = "Player", IsHost = false, Index = 0 };
    static readonly Dictionary<ushort, Action<Peer, NetReader>> Handlers = new();
    static readonly Dictionary<ulong, Link> ClientLinks = new();   // host: transport id == Peer.Id
    static readonly List<Peer> PeerList = new();                   // ordered by Index
    static readonly Stack<NetWriter> WriterPool = new();
    static readonly List<ulong> TmpTargets = new();
    static readonly Dictionary<int, int> HandlerErrors = new();

    static ITransport _t;
    static Link _hostLink;                // client: link to the host
    static Peer _local;
    static Peer[] _snapshot = Array.Empty<Peer>();
    static bool _connecting;
    static long _connectStartMs;
    static long _helloAtMs;
    static bool _inTransport;
    static string _deferredLeave;         // set when Leave was requested from inside a transport callback
    static bool _leaveDeferred;
    static ushort _nextTransfer;
    static string _detail = "";
    static string _joinTarget = "";
    static readonly SendFailureTracker HostSendFailures = new();
    static readonly StallGuard Stall = new();
    static string _fatalError;            // set from send plumbing, turned into a session error on the next Update
    static long _nextNetStateMs;
    static long _stallEvents;

    // ---------------------------------------------------------------------------------------------- contract

    public static Role Role { get; private set; } = Role.Offline;
    public static bool IsHost => Role == Role.Host;
    public static bool IsClient => Role == Role.Client;
    public static bool IsOnline => Role != Role.Offline;
    public static ulong LocalId => _local?.Id ?? 0;
    public static Peer LocalPeer => _local ?? OfflinePeer;
    public static IReadOnlyList<Peer> Peers => _snapshot;
    public static int PlayerCount => Role == Role.Offline ? 1 : _snapshot.Length;

    /// <summary>True while host state is being applied locally; patches must let the original game code run.</summary>
    public static bool Applying;

    public static event Action<Peer> PeerJoined, PeerLeft;
    public static event Action SessionStarted, SessionEnded;

    /// <summary>A refusal or failure the player should see on screen (version mismatch, full session, join failure, lost connection).</summary>
    public static event Action<string> Notice;

    public static void Register(ushort msgId, Action<Peer, NetReader> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        if (msgId < CoreMsg.FirstUserId) throw new ArgumentOutOfRangeException(nameof(msgId), "ids below 100 are reserved for the core protocol");
        if (msgId > PacketHeader.IdMask) throw new ArgumentOutOfRangeException(nameof(msgId));
        Handlers[msgId] = handler;
    }

    /// <summary>Client -> host. No-op on the host (it has no one to send to; handle the request locally instead).</summary>
    public static void SendToHost(ushort msgId, Action<NetWriter> write, bool reliable = true)
    {
        if (Role != Role.Client || _hostLink == null) return;
        var w = Begin(PacketHeader.Make(msgId, reliable));
        try
        {
            write?.Invoke(w);
            Transmit(_t.HostRemoteId, w, reliable);
        }
        finally { Return(w); }
    }

    /// <summary>
    /// Host: direct to that client. Client: to the host directly, or to another client through the host's relay.
    /// Never loops back to the caller.
    /// </summary>
    public static void SendTo(Peer peer, ushort msgId, Action<NetWriter> write, bool reliable = true)
    {
        if (Role == Role.Offline || peer == null || peer == _local || peer.Id == LocalId) return;

        if (Role == Role.Host)
        {
            if (!ClientLinks.ContainsKey(peer.Id)) return;
            var w = Begin(PacketHeader.Make(msgId, reliable));
            try { write?.Invoke(w); Transmit(peer.Id, w, reliable); }
            finally { Return(w); }
            return;
        }

        if (_hostLink == null) return;
        if (peer.IsHost) { SendToHost(msgId, write, reliable); return; }
        var rw = Begin(PacketHeader.Make(msgId, reliable, directed: true));
        try
        {
            rw.Write((byte)peer.Index);
            write?.Invoke(rw);
            Transmit(_t.HostRemoteId, rw, reliable);
        }
        finally { Return(rw); }
    }

    /// <summary>
    /// Host: to every client except `except`. Client: to the host AND (through its relay) to every other client;
    /// `except` is ignored on a client. Never loops back to the caller.
    /// </summary>
    public static void Broadcast(ushort msgId, Action<NetWriter> write, bool reliable = true, Peer except = null)
    {
        if (Role == Role.Offline) return;

        if (Role == Role.Client)
        {
            if (_hostLink == null) return;
            var cw = Begin(PacketHeader.Make(msgId, reliable, relay: true));
            try { write?.Invoke(cw); Transmit(_t.HostRemoteId, cw, reliable); }
            finally { Return(cw); }
            return;
        }

        if (ClientLinks.Count == 0) return;
        var w = Begin(PacketHeader.Make(msgId, reliable));
        try
        {
            write?.Invoke(w);
            TmpTargets.Clear();
            foreach (var id in ClientLinks.Keys)
                if (except == null || except.Id != id) TmpTargets.Add(id);
            TransmitMany(TmpTargets, w, reliable);
        }
        finally { Return(w); }
    }

    // ---------------------------------------------------------------------------------------------- session control

    /// <summary>Human readable one-liner for the menu.</summary>
    public static string Status
    {
        get
        {
            if (_connecting)
            {
                string d = _t?.StatusDetail;
                int secs = (int)((Environment.TickCount64 - _connectStartMs) / 1000);
                int limit = (int)(_t?.ConnectTimeoutSeconds ?? 0);
                return $"Connecting to {_joinTarget}... {secs}/{limit} s" + (string.IsNullOrEmpty(d) ? "" : " - " + d);
            }
            if (Role == Role.Host) return $"Hosting ({_detail}) - {PlayerCount} player(s){SendNote()}";
            if (Role == Role.Client) return $"Connected to {_joinTarget} - {PlayerCount} player(s){SendNote()}";
            return string.IsNullOrEmpty(LastError) ? "Offline" : "Offline - " + LastError;
        }
    }

    static string SendNote() =>
        SendFailures == 0 ? "" : $" - {SendFailures} send failure(s), last {(_t != null && _t.Kind == "Steam" ? SteamStates.EResult(LastSendResult) : LastSendResult.ToString())}";

    /// <summary>Why the last start/join/session failed or ended (cleared by the next start).</summary>
    public static string LastError { get; private set; }
    public static bool Connecting => _connecting;
    /// <summary>Host side: why the last joiner was turned away (version mismatch). Cleared by the next start.</summary>
    public static string LastRefusal { get; private set; }
    public static string TransportName => _t?.Kind ?? "none";

    public static long BytesSent { get; private set; }
    public static long BytesReceived { get; private set; }
    public static long PacketsSent { get; private set; }
    public static long PacketsReceived { get; private set; }
    /// <summary>Packets the transport refused or could not queue (all targets, all kinds).</summary>
    public static long SendFailures { get; private set; }
    /// <summary>EResult-style code of the most recent send (1 = OK).</summary>
    public static int LastSendResult { get; private set; } = 1;
    /// <summary>Sends to the host that failed in a row (client only).</summary>
    public static int ConsecutiveHostSendFailures => HostSendFailures.Consecutive;

    public static Peer HostPeer
    {
        get
        {
            foreach (var p in _snapshot) if (p.IsHost) return p;
            return null;
        }
    }

    public static Peer GetPeer(ulong id)
    {
        foreach (var p in _snapshot) if (p.Id == id) return p;
        return null;
    }

    /// <summary>Round-trip time to a peer in ms as measured by this machine (host: every client, client: the host only), -1 unknown.</summary>
    public static int GetPingMs(Peer peer)
    {
        if (peer == null) return -1;
        if (Role == Role.Host) return ClientLinks.TryGetValue(peer.Id, out var l) ? l.PingMs : -1;
        if (Role == Role.Client && peer.IsHost) return _hostLink?.PingMs ?? -1;
        return -1;
    }

    public static bool StartHostSteam()
    {
        if (!PrepareStart()) return false;
        try
        {
            return BeginHost(SteamTransport.Host(), "Steam");
        }
        catch (Exception e) { return Fail("Steam host failed: " + e.Message); }
    }

    /// <summary>port 0 = CoopConfig.DirectPort.</summary>
    public static bool StartHostDirect(int port = 0)
    {
        if (!PrepareStart()) return false;
        if (port <= 0) port = CoopConfig.DirectPort;
        try
        {
            return BeginHost(DirectTransport.Host(port), $"Direct :{port}");
        }
        catch (Exception e) { return Fail($"Direct host on port {port} failed: " + e.Message); }
    }

    public static bool JoinSteam(ulong hostSteamId)
    {
        if (!PrepareStart()) return false;
        if (!SteamIds.IsPlausible(hostSteamId)) return Fail("That is not a valid SteamID64 (17 digits, starting with 7656119)");
        if (SteamNative.Init(force: true) && hostSteamId == SteamNative.LocalSteamId) return Fail("That is your own SteamID64 - enter the host's");
        try
        {
            var peerName = SteamNative.FriendName(hostSteamId) ?? hostSteamId.ToString();
            return BeginJoin(SteamTransport.Client(hostSteamId), peerName);
        }
        catch (Exception e) { return Fail("Steam join failed: " + e.Message); }
    }

    /// <summary>port 0 = CoopConfig.DirectPort. retrySeconds: keep retrying a refused connection (two instances launched together).</summary>
    public static bool JoinDirect(string host, int port = 0, int retrySeconds = 0)
    {
        if (!PrepareStart()) return false;
        if (string.IsNullOrWhiteSpace(host)) return Fail("No host address given");
        if (port <= 0) port = CoopConfig.DirectPort;
        try
        {
            return BeginJoin(DirectTransport.Client(host.Trim(), port, retrySeconds), $"{host.Trim()}:{port}");
        }
        catch (Exception e) { return Fail($"Direct join to {host}:{port} failed: " + e.Message); }
    }

    /// <summary>Leave / cancel the current session or connection attempt. Safe to call when offline.</summary>
    public static void Leave() => LeaveInternal(null, true);

    /// <summary>Application quit: like Leave, but Steam sessions are not closed explicitly (process teardown does that) and presence is cleared.</summary>
    public static void Quit()
    {
        SteamTransport.Quitting = true;
        LeaveInternal(null, true);
    }

    /// <summary>Pump the transport, dispatch messages, run pings and timeouts. Called every frame by CoopBehaviour.</summary>
    public static void Update()
    {
        SteamTransport.PumpDeferredCloses();
        if (_t == null) return;
        try
        {
            long frameNow = Environment.TickCount64;
            long stalled = Stall.Tick(frameNow);
            if (stalled > 0) CreditStall(stalled, frameNow);

            _inTransport = true;
            try { _t.Update(); }
            finally { _inTransport = false; }

            if (_leaveDeferred) { var reason = _deferredLeave; _leaveDeferred = false; _deferredLeave = null; LeaveInternal(reason, true); return; }
            if (_t == null) return;

            if (_t.Error != null) { LeaveInternal(_t.Error, false); return; }
            if (_fatalError != null) { var why = _fatalError; _fatalError = null; LeaveInternal(why, false); return; }

            long now = Environment.TickCount64;
            LogNetState(now);
            if (_connecting) UpdateConnecting(now);
            else if (Role == Role.Host) UpdateHost(now);
            else if (Role == Role.Client) UpdateClient(now);
        }
        catch (Exception e)
        {
            _inTransport = false;
            Log.Error("Net.Update failed", e);
        }
    }

    // ---------------------------------------------------------------------------------------------- start / stop

    static bool PrepareStart()
    {
        if (Role != Role.Offline || _connecting) LeaveInternal(null, true);
        LastError = null;
        return true;
    }

    static bool Fail(string error)
    {
        LastError = error;
        Log.Warn(error);
        return false;
    }

    static string LocalName()
    {
        if (SteamNative.Init() && !string.IsNullOrEmpty(SteamNative.LocalName)) return Sanitize(SteamNative.LocalName);
        return Sanitize(Environment.UserName);
    }

    static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Player";
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            if (!char.IsControl(ch)) sb.Append(ch);
        var s = sb.ToString().Trim();
        if (s.Length > 24) s = s.Substring(0, 24);
        return s.Length == 0 ? "Player" : s;
    }

    static bool BeginHost(ITransport t, string detail)
    {
        _t = t;
        _t.PacketReceived += OnPacket;
        _t.RemoteDisconnected += OnRemoteDisconnected;
        _detail = detail;
        _joinTarget = "";
        ResetLinkStats();
        _local = new Peer { Id = t.LocalId, Name = LocalName(), IsHost = true, Index = 0 };
        PeerList.Clear();
        PeerList.Add(_local);
        RebuildSnapshot();
        Role = Role.Host;
        Log.Info($"Hosting session over {detail} as {_local}");
        Fire(SessionStarted);
        return true;
    }

    static bool BeginJoin(ITransport t, string target)
    {
        _t = t;
        _t.PacketReceived += OnPacket;
        _t.RemoteDisconnected += OnRemoteDisconnected;
        _hostLink = new Link { LastRecvMs = Environment.TickCount64 };
        _joinTarget = target;
        _connecting = true;
        _connectStartMs = Environment.TickCount64;
        _helloAtMs = 0;
        ResetLinkStats();
        Log.Info($"Joining {target} over {t.Kind}");
        return true;
    }

    static void LeaveInternal(string reason, bool sayBye)
    {
        if (_t == null && Role == Role.Offline && !_connecting) { if (reason != null) LastError = reason; return; }
        if (_inTransport) { _leaveDeferred = true; _deferredLeave = reason; return; }

        var transport = _t;
        var wasRole = Role;
        bool wasConnecting = _connecting;
        var remotePeers = new List<Peer>();
        foreach (var p in _snapshot) if (p != _local) remotePeers.Add(p);

        if (sayBye && transport != null)
        {
            try
            {
                if (wasRole == Role.Host)
                {
                    var ids = new List<ulong>(ClientLinks.Keys);
                    foreach (var id in ids) SendCore(id, CoreMsg.Bye, null);
                }
                else if (_hostLink != null) SendCore(transport.HostRemoteId, CoreMsg.Bye, null);
            }
            catch (Exception e) { Log.Error("Bye failed", e); }
        }

        // Peers leave while the session is still formally alive so modules can clean up per peer.
        foreach (var p in remotePeers) Fire(PeerLeft, p);

        try { transport?.Dispose(); } catch (Exception e) { Log.Error("Transport dispose failed", e); }
        if (transport != null)
        {
            transport.PacketReceived -= OnPacket;
            transport.RemoteDisconnected -= OnRemoteDisconnected;
        }
        _t = null;
        _hostLink = null;
        _local = null;
        _connecting = false;
        Role = Role.Offline;
        ClientLinks.Clear();
        PeerList.Clear();
        RebuildSnapshot();
        Applying = false;
        _detail = "";
        if (reason != null) { LastError = reason; Log.Info("Session ended: " + reason); }
        else Log.Info("Session ended");

        NetIds.Clear();
        if (wasRole != Role.Offline) Fire(SessionEnded);
        if (reason != null && (wasConnecting || wasRole != Role.Offline)) Raise(Notice, reason);
    }

    // ---------------------------------------------------------------------------------------------- housekeeping

    static long PeerTimeoutMs => (long)(CoopConfig.PeerTimeoutSeconds * 1000);

    static void ResetLinkStats()
    {
        HostSendFailures.Reset();
        Stall.Reset();
        SendFailures = 0;
        LastSendResult = 1;
        _fatalError = null;
        LastRefusal = null;
        _nextNetStateMs = 0;
    }

    /// <summary>The main thread did not run for `stalledMs`: that silence is ours, not the other side's.</summary>
    static void CreditStall(long stalledMs, long now)
    {
        _stallEvents++;
        foreach (var l in ClientLinks.Values) l.LastRecvMs = Math.Min(now, l.LastRecvMs + stalledMs);
        if (_hostLink != null) _hostLink.LastRecvMs = Math.Min(now, _hostLink.LastRecvMs + stalledMs);
        _connectStartMs = Math.Min(now, _connectStartMs + stalledMs);
        Log.Info($"Net: the main thread stalled for {stalledMs} ms; receive timeouts were extended by that time");
    }

    static void UpdateConnecting(long now)
    {
        if (!_t.Ready) { _connectStartMs = now; return; }
        double limit = _t.ConnectTimeoutSeconds;
        if (_t.Backlog == 0 && now - _connectStartMs > (long)(limit * 1000))
        {
            string detail = _t.StatusDetail;
            string hint = _t.Kind == "Steam"
                ? " Check that the host pressed 'Host (Steam)' and is in the game, that you are friends on Steam, then try again - or use a Direct (TCP) connection."
                : " Check the address and port and that the host is hosting.";
            LeaveInternal($"The host did not answer within {limit:0} s." + (string.IsNullOrEmpty(detail) ? "" : $" Steam session: {detail}.") + hint, false);
            return;
        }
        if (_helloAtMs == 0 || (_t.NeedsHelloRetry && now >= _helloAtMs))
        {
            _helloAtMs = now + (long)(Protocol.HelloRetrySeconds * 1000);
            SendCore(_t.HostRemoteId, CoreMsg.Hello, w => { w.Write(Protocol.Version); w.Write(LocalName()); w.Write(Plugin.Version); });
        }
    }

    static void UpdateHost(long now)
    {
        List<Link> stale = null;
        bool draining = _t.Backlog > 0;   // undelivered packets are still queued: silence cannot be judged yet
        long timeout = PeerTimeoutMs;
        foreach (var l in ClientLinks.Values)
        {
            if (!draining && now - l.LastRecvMs > timeout) (stale ??= new List<Link>()).Add(l);
            else if (now - l.LastPingMs >= (long)(Protocol.PingIntervalSeconds * 1000)) SendPing(l, l.Peer.Id, now);
        }
        if (stale == null) return;
        foreach (var l in stale) RemovePeer(l.Peer, "timed out");
    }

    static void UpdateClient(long now)
    {
        if (_t.Backlog == 0 && now - _hostLink.LastRecvMs > PeerTimeoutMs)
        {
            LeaveInternal($"Connection to the host timed out (nothing received for {CoopConfig.PeerTimeoutSeconds:0} s)", false);
            return;
        }
        if (now - _hostLink.LastPingMs >= (long)(Protocol.PingIntervalSeconds * 1000)) SendPing(_hostLink, _t.HostRemoteId, now);
    }

    static void SendPing(Link link, ulong transportId, long now)
    {
        link.LastPingMs = now;
        SendCore(transportId, CoreMsg.Ping, w => w.Write(now));
    }

    static void OnRemoteDisconnected(ulong transportId)
    {
        if (Role == Role.Host)
        {
            if (ClientLinks.TryGetValue(transportId, out var l)) RemovePeer(l.Peer, "disconnected");
        }
        else if (transportId == _t?.HostRemoteId)
        {
            LeaveInternal("Connection to the host was lost", false);
        }
    }

    static void RemovePeer(Peer peer, string why)
    {
        if (peer == null || !ClientLinks.Remove(peer.Id)) return;
        PeerList.Remove(peer);
        RebuildSnapshot();
        _t.OnPeerRemoved(peer.Id);
        Log.Info($"Peer {peer} {why}");
        BroadcastPeerList();
        Fire(PeerLeft, peer);
    }

    static int FreeIndex()
    {
        for (int i = 1; i < Protocol.MaxPlayers; i++)
        {
            bool used = false;
            foreach (var p in PeerList) if (p.Index == i) { used = true; break; }
            if (!used) return i;
        }
        return -1;
    }

    static void RebuildSnapshot()
    {
        PeerList.Sort((a, b) => a.Index.CompareTo(b.Index));
        _snapshot = PeerList.ToArray();
    }

    // ---------------------------------------------------------------------------------------------- receive

    static void OnPacket(ulong senderId, byte[] packet)
    {
        BytesReceived += packet.Length;
        PacketsReceived++;
        try { Process(senderId, packet, true); }
        catch (Exception e) { Log.Error($"Packet from {senderId} failed", e); }
    }

    static Link LinkFor(ulong transportId)
    {
        if (Role == Role.Host) return ClientLinks.TryGetValue(transportId, out var l) ? l : null;
        return _t != null && transportId == _t.HostRemoteId ? _hostLink : null;
    }

    static void Process(ulong senderId, byte[] packet, bool allowFragment)
    {
        if (packet.Length < PacketHeader.Size) return;
        var link = LinkFor(senderId);
        if (link != null) link.LastRecvMs = Environment.TickCount64;

        ushort header = BinaryPrimitives.ReadUInt16LittleEndian(packet);
        ushort id = PacketHeader.Id(header);

        if (id == CoreMsg.Fragment)
        {
            if (link == null || !allowFragment) return;
            if (link.Assembler.Accept(packet, out var full)) Process(senderId, full, false);
            return;
        }

        if (id < CoreMsg.FirstUserId)
        {
            HandleCore(senderId, link, id, new NetReader(packet, PacketHeader.Size, packet.Length - PacketHeader.Size));
            return;
        }

        if (link == null || link.Peer == null) return; // not part of the session
        Peer from = link.Peer;
        int offset = PacketHeader.Size;

        if (Role == Role.Host)
        {
            if (PacketHeader.IsDirected(header))
            {
                if (packet.Length < offset + 1) return;
                ForwardTo(packet[offset], from, header, packet, offset + 1);
                return;
            }
            if (PacketHeader.IsRelay(header)) ForwardToOthers(from, header, packet, offset);
        }
        else if (PacketHeader.IsRelay(header))
        {
            if (packet.Length < offset + 1) return;
            from = FindByIndex(packet[offset]);
            offset++;
            if (from == null) return;
        }

        Dispatch(from, id, packet, offset);
    }

    static Peer FindByIndex(int index)
    {
        foreach (var p in _snapshot) if (p.Index == index) return p;
        return null;
    }

    static void Dispatch(Peer from, ushort id, byte[] packet, int offset)
    {
        if (!Handlers.TryGetValue(id, out var handler)) return;
        try { handler(from, new NetReader(packet, offset, packet.Length - offset)); }
        catch (Exception e)
        {
            HandlerErrors.TryGetValue(id, out var n);
            HandlerErrors[id] = n + 1;
            if (n < 5 || n % 500 == 0) Log.Error($"Handler for message {id} from {from} failed (x{n + 1})", e);
        }
    }

    /// <summary>Host: re-send a client's relay packet to every other client, marked with the origin peer index.</summary>
    static void ForwardToOthers(Peer origin, ushort header, byte[] packet, int payloadOffset)
    {
        if (ClientLinks.Count < 2) return;
        var w = Begin((ushort)((header & ~PacketHeader.Directed) | PacketHeader.Relay));
        try
        {
            w.Write((byte)origin.Index);
            w.WriteBytes(packet, payloadOffset, packet.Length - payloadOffset);
            TmpTargets.Clear();
            foreach (var id in ClientLinks.Keys) if (id != origin.Id) TmpTargets.Add(id);
            TransmitMany(TmpTargets, w, !PacketHeader.IsUnreliable(header));
        }
        finally { Return(w); }
    }

    static void ForwardTo(int targetIndex, Peer origin, ushort header, byte[] packet, int payloadOffset)
    {
        var target = FindByIndex(targetIndex);
        if (target == null || target == _local || !ClientLinks.ContainsKey(target.Id)) return;
        var w = Begin((ushort)((header & ~PacketHeader.Directed) | PacketHeader.Relay));
        try
        {
            w.Write((byte)origin.Index);
            w.WriteBytes(packet, payloadOffset, packet.Length - payloadOffset);
            Transmit(target.Id, w, !PacketHeader.IsUnreliable(header));
        }
        finally { Return(w); }
    }

    // ---------------------------------------------------------------------------------------------- core messages

    static void HandleCore(ulong senderId, Link link, ushort id, NetReader r)
    {
        switch (id)
        {
            case CoreMsg.Hello: HandleHello(senderId, link, r); break;
            case CoreMsg.Welcome: if (link == _hostLink && link != null) HandleWelcome(r); break;
            case CoreMsg.PeerList:
                if (Role == Role.Client && link == _hostLink) ApplyPeerListFromHost(r);
                break;
            case CoreMsg.Refuse:
                if (_connecting && link == _hostLink) LeaveInternal(Handshake.ClientRefuseMessage(r.ReadString()), false);
                break;
            case CoreMsg.Bye:
                if (link == null) break;
                if (Role == Role.Host) RemovePeer(link.Peer, "left");
                else if (link == _hostLink) LeaveInternal("The host closed the session", false);
                break;
            case CoreMsg.Ping:
                if (link != null) { long stamp = r.ReadLong(); SendCore(senderId, CoreMsg.Pong, w => w.Write(stamp)); }
                break;
            case CoreMsg.Pong:
                if (link != null) link.PingMs = (int)Math.Min(int.MaxValue, Math.Max(0, Environment.TickCount64 - r.ReadLong()));
                break;
        }
    }

    static void HandleHello(ulong senderId, Link link, NetReader r)
    {
        if (Role != Role.Host) return;
        ushort version = r.ReadUShort();
        if (version != Protocol.Version)
        {
            // An older client's Hello has no mod version field: only the protocol check applies.
            string why = Handshake.Check(Protocol.Version, Plugin.Version, version, "unknown");
            Log.Warn($"Refusing {senderId}: protocol {version} != {Protocol.Version}");
            LastRefusal = $"Refused {senderId}: " + why;
            Raise(Notice, LastRefusal);
            SendCore(senderId, CoreMsg.Refuse, w => w.Write(why));
            return;
        }
        string name = Sanitize(r.ReadString());
        string clientMod = r.ReadString();
        string mismatch = Handshake.Check(Protocol.Version, Plugin.Version, version, clientMod);
        if (mismatch != null)
        {
            Log.Warn($"Refusing {name} ({senderId}): mod version {clientMod} != {Plugin.Version}");
            LastRefusal = $"Refused {name}: they have mod version {clientMod}, you have {Plugin.Version}. Both must install the same version.";
            Raise(Notice, LastRefusal);
            SendCore(senderId, CoreMsg.Refuse, w => w.Write(mismatch));
            return;
        }

        if (link != null)
        {
            // Duplicate Hello (retry): the Welcome was probably lost.
            SendWelcome(link.Peer);
            return;
        }

        int index = FreeIndex();
        if (index < 0)
        {
            LastRefusal = $"Refused {name}: the session is full ({PlayerCount} players).";
            Raise(Notice, LastRefusal);
            SendCore(senderId, CoreMsg.Refuse, w => w.Write("session is full"));
            return;
        }

        var peer = new Peer { Id = senderId, Name = name, IsHost = false, Index = index };
        var newLink = new Link { Peer = peer, LastRecvMs = Environment.TickCount64, LastPingMs = Environment.TickCount64 };
        ClientLinks[senderId] = newLink;
        PeerList.Add(peer);
        RebuildSnapshot();
        _t.OnPeerAdded(senderId);
        Log.Info($"Peer joined: {peer}");

        SendWelcome(peer);
        BroadcastPeerList(except: peer);
        Fire(PeerJoined, peer);
    }

    static void WritePeers(NetWriter w)
    {
        w.Write((byte)PeerList.Count);
        foreach (var p in PeerList)
        {
            w.Write(p.Id);
            w.Write((byte)p.Index);
            w.Write(p.IsHost);
            w.Write(p.Name);
        }
    }

    static void SendWelcome(Peer to) =>
        SendCore(to.Id, CoreMsg.Welcome, w => { w.Write((byte)to.Index); w.Write(to.Id); WritePeers(w); });

    static void BroadcastPeerList(Peer except = null)
    {
        foreach (var id in new List<ulong>(ClientLinks.Keys))
            if (except == null || except.Id != id) SendCore(id, CoreMsg.PeerList, WritePeers);
    }

    static void HandleWelcome(NetReader r)
    {
        int index = r.ReadByte();
        ulong myId = r.ReadULong();

        if (!_connecting)
        {
            // Repeated Welcome while connected: just refresh the list.
            if (Role == Role.Client) ApplyPeerListFromHost(r);
            return;
        }

        _local = new Peer { Id = myId, Name = LocalName(), IsHost = false, Index = index };
        _connecting = false;
        Role = Role.Client;
        _hostLink.LastRecvMs = Environment.TickCount64;
        PeerList.Clear();
        PeerList.Add(_local);

        var added = new List<Peer>();
        var removed = new List<Peer>();
        ApplyPeers(r, added, removed);
        Log.Info($"Joined session as {_local}, {PlayerCount} player(s)");

        Fire(SessionStarted);
        foreach (var p in added) Fire(PeerJoined, p);
    }

    static void ApplyPeerListFromHost(NetReader r)
    {
        var added = new List<Peer>();
        var removed = new List<Peer>();
        ApplyPeers(r, added, removed);
        foreach (var p in removed) Fire(PeerLeft, p);
        foreach (var p in added) Fire(PeerJoined, p);
    }

    /// <summary>Client: diff the host's peer list against ours, keeping Peer instances stable per id.</summary>
    static void ApplyPeers(NetReader r, List<Peer> added, List<Peer> removed)
    {
        int count = r.ReadByte();
        var incoming = new List<Peer>(count);
        for (int i = 0; i < count; i++)
        {
            ulong id = r.ReadULong();
            int index = r.ReadByte();
            bool isHost = r.ReadBool();
            string name = r.ReadString();

            if (id == _local.Id)
            {
                _local.Index = index;
                incoming.Add(_local);
                continue;
            }
            Peer existing = null;
            foreach (var p in PeerList) if (p.Id == id) { existing = p; break; }
            if (existing == null) { existing = new Peer { Id = id }; added.Add(existing); }
            existing.Index = index;
            existing.IsHost = isHost;
            existing.Name = name;
            incoming.Add(existing);
            if (isHost && _hostLink != null) _hostLink.Peer = existing;
        }

        foreach (var p in PeerList)
            if (!incoming.Contains(p)) removed.Add(p);

        PeerList.Clear();
        PeerList.AddRange(incoming);
        RebuildSnapshot();
    }

    // ---------------------------------------------------------------------------------------------- send plumbing

    static NetWriter Begin(ushort header)
    {
        var w = WriterPool.Count > 0 ? WriterPool.Pop() : new NetWriter(512);
        w.Reset();
        w.Write(header);
        return w;
    }

    static void Return(NetWriter w)
    {
        if (w.Buffer.Length <= 1 << 20) WriterPool.Push(w);
    }

    static void SendCore(ulong transportId, ushort msgId, Action<NetWriter> write)
    {
        if (_t == null) return;
        var w = Begin(PacketHeader.Make(msgId, true));
        try { write?.Invoke(w); Transmit(transportId, w, true); }
        finally { Return(w); }
    }

    static readonly List<ulong> Single = new(1);

    static void Transmit(ulong transportId, NetWriter w, bool reliable)
    {
        Single.Clear();
        Single.Add(transportId);
        TransmitMany(Single, w, reliable);
    }

    /// <summary>Sends one built packet to several transport ids, fragmenting it once when it is too large.</summary>
    static void TransmitMany(List<ulong> targets, NetWriter w, bool reliable)
    {
        if (_t == null || targets.Count == 0) return;
        int len = w.Length;
        if (Framing.NeedsSplit(len))
        {
            var frags = Framing.Split(w.Buffer, len, _nextTransfer++);
            foreach (var id in targets)
                foreach (var f in frags)
                    if (!SendPacket(id, f, f.Length, true)) break;   // a transfer with a missing fragment is useless
            return;
        }
        foreach (var id in targets) SendPacket(id, w.Buffer, len, reliable);
    }

    static bool SendPacket(ulong id, byte[] buffer, int len, bool reliable)
    {
        bool ok = _t.Send(id, buffer, len, reliable);
        LastSendResult = _t.LastSendResult;
        if (ok) { BytesSent += len; PacketsSent++; }
        else SendFailures++;

        if (Role != Role.Host && id == _t.HostRemoteId)
        {
            if (HostSendFailures.Record(ok, Environment.TickCount64) && _fatalError == null)
            {
                string why = _t.Kind == "Steam" ? SteamStates.EResult(LastSendResult) : "connection lost";
                _fatalError = $"Sending to the host keeps failing ({HostSendFailures.Consecutive} times in a row, last error: {why}). The host is probably gone or Steam lost its connection.";
            }
        }
        return ok;
    }

    // ---------------------------------------------------------------------------------------------- diagnostics

    /// <summary>One line of link diagnostics ("key=value ..."), logged as [NETSTATE] and usable by any state probe.</summary>
    public static string DebugState()
    {
        var sb = new StringBuilder(160);
        sb.Append("transport=").Append(TransportName)
          .Append(" lastSend=").Append(LastSendResult)
          .Append(" sendFail=").Append(SendFailures)
          .Append(" hostFailRun=").Append(HostSendFailures.Consecutive)
          .Append(" pkts=").Append(PacketsSent).Append('/').Append(PacketsReceived)
          .Append(" backlog=").Append(_t?.Backlog ?? 0)
          .Append(" stalls=").Append(_stallEvents);
        if (_t != null && _t.Kind == "Steam")
            sb.Append(" relay=").Append(SteamStates.Availability(SteamNative.RelayAvailability()));
        string d = _t?.StatusDetail;
        if (!string.IsNullOrEmpty(d)) sb.Append(" steam=\"").Append(d).Append('"');
        return sb.ToString();
    }

    static void LogNetState(long now)
    {
        float seconds = LaunchArgs.StateLogSeconds ?? CoopConfig.StateLogSeconds;
        if (seconds <= 0f || now < _nextNetStateMs) return;
        _nextNetStateMs = now + (long)(seconds * 1000f);
        Log.Info("[NETSTATE] " + DebugState());
    }

    // ---------------------------------------------------------------------------------------------- events

    static void Fire(Action ev)
    {
        if (ev == null) return;
        foreach (Action d in ev.GetInvocationList())
        {
            try { d(); } catch (Exception e) { Log.Error("Net event handler failed", e); }
        }
    }

    static void Raise(Action<string> ev, string text)
    {
        if (ev == null) return;
        foreach (Action<string> d in ev.GetInvocationList())
        {
            try { d(text); } catch (Exception e) { Log.Error("Net event handler failed", e); }
        }
    }

    static void Fire(Action<Peer> ev, Peer peer)
    {
        if (ev == null) return;
        foreach (Action<Peer> d in ev.GetInvocationList())
        {
            try { d(peer); } catch (Exception e) { Log.Error("Net event handler failed", e); }
        }
    }
}
