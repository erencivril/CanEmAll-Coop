using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using CanEmAllCoop.Core;

namespace CanEmAllCoop.Net;

/// <summary>
/// ISteamNetworkingMessages transport. There is no connection object: a "peer" is just a SteamID64 we send to.
/// The host learns about joiners through the global MessagesSessionRequest callback (the game's Facepunch dispatcher
/// swallows the normal one) and, as a fallback, by calling AcceptSessionWithUser once a second for every immediate
/// friend and every known peer. Receiving is bounded per frame; surplus messages wait in a queue for the next frame.
/// </summary>
internal sealed class SteamTransport : ITransport
{
    const int Channel = 42;
    const int PullLimit = 1024;          // messages copied out of Steam while fewer than this wait in _pending
    const int MinPerFrame = 32;          // always delivered per frame, whatever the time budget says
    const int MaxPerFrame = 2048;
    static readonly long BudgetTicks = Stopwatch.Frequency * 6 / 1000;   // ~6 ms of delivery work per frame
    const long AcceptPollMs = 1000;
    const long CloseDelayMs = 1000;
    const long InfoPollConnectingMs = 500;
    const long InfoPollConnectedMs = 5000;

    /// <summary>Set when the application is quitting: sessions are not closed explicitly (process teardown does it).</summary>
    public static volatile bool Quitting;

    static readonly DeferredCloseSchedule Deferred = new();
    static readonly List<ulong> DueTmp = new();

    readonly bool _isHost;
    readonly ulong _hostId;
    readonly HashSet<ulong> _known = new();
    readonly RemovedPeerGuard _removed = new(3000);
    readonly Queue<SteamNative.Inbound> _pending = new();
    readonly List<SteamNative.Inbound> _inbox = new();
    readonly List<ulong> _heldRequests = new();
    long _nextPollMs, _nextInfoMs;
    int _sendFailureLogs;
    bool _gotHostPacket;
    int _lastState = -99, _lastReason = -99;
    string _detail = "";
    bool _disposed;

    public string Kind => "Steam";
    public ulong HostRemoteId => _hostId;
    public ulong LocalId => SteamNative.LocalSteamId;
    public bool Ready => true;
    public bool NeedsHelloRetry => true;
    public string Error { get; private set; }
    public double ConnectTimeoutSeconds => CoopConfig.SteamConnectTimeoutSeconds;
    public string StatusDetail => _detail;
    public int Backlog => _pending.Count;
    public int LastSendResult { get; private set; } = SteamNative.EResultOK;

    public event Action<ulong, byte[]> PacketReceived;
    public event Action<ulong> RemoteDisconnected { add { } remove { } } // Steam has no link state; Bye / timeouts cover it

    SteamTransport(bool isHost, ulong hostId)
    {
        _isHost = isHost;
        _hostId = hostId;
        if (!isHost) { _known.Add(hostId); Deferred.Cancel(hostId); }
    }

    public static SteamTransport Host()
    {
        if (!SteamNative.Init(force: true)) throw new InvalidOperationException("Steam unavailable: " + (SteamNative.InitError ?? "not ready yet, try again"));
        var t = new SteamTransport(true, SteamNative.LocalSteamId);
        bool cb = SteamNative.SetSessionRequestCallback(true);
        SteamNative.SetHostPresence(true);
        Log.Info($"Steam host ready: id {SteamNative.LocalSteamId}, session request callback {(cb ? "registered" : "NOT registered (friend poll only)")}, relay {SteamStates.Availability(SteamNative.RelayAvailability())}");
        return t;
    }

    public static SteamTransport Client(ulong hostSteamId)
    {
        if (!SteamNative.Init(force: true)) throw new InvalidOperationException("Steam unavailable: " + (SteamNative.InitError ?? "not ready yet, try again"));
        var t = new SteamTransport(false, hostSteamId);
        SteamNative.AcceptSession(hostSteamId);
        Log.Info($"Steam join: host {hostSteamId}, relay {SteamStates.Availability(SteamNative.RelayAvailability())}");
        return t;
    }

    /// <summary>Executes postponed CloseSessionWithUser calls. Net calls this every frame, also while no transport exists.</summary>
    public static void PumpDeferredCloses()
    {
        if (Deferred.Count == 0) return;
        DueTmp.Clear();
        Deferred.CollectDue(Environment.TickCount64, DueTmp);
        foreach (var id in DueTmp)
            try { if (!Quitting) SteamNative.CloseSession(id); } catch (Exception) { }
        DueTmp.Clear();
    }

    public void Update()
    {
        long now = Environment.TickCount64;
        PumpDeferredCloses();

        if (now >= _nextPollMs)
        {
            _nextPollMs = now + AcceptPollMs;
            PollAccept(now);
        }
        if (_isHost) AcceptRequested(now);
        else PollSessionInfo(now);

        if (_pending.Count < PullLimit)
        {
            _inbox.Clear();
            try { SteamNative.Receive(Channel, PullLimit - _pending.Count, _inbox); }
            catch (Exception e) { Error ??= "Steam receive failed: " + e.Message; return; }
            foreach (var m in _inbox) _pending.Enqueue(m);
            _inbox.Clear();
        }

        Deliver(now);
    }

    void Deliver(long now)
    {
        long start = Stopwatch.GetTimestamp();
        int done = 0;
        while (_pending.Count > 0 && done < MaxPerFrame)
        {
            if (done >= MinPerFrame && Stopwatch.GetTimestamp() - start > BudgetTicks) break;
            var msg = _pending.Dequeue();
            done++;
            ulong sender = msg.Sender;
            if (!_known.Contains(sender))
            {
                if (!_isHost) continue;                          // a client only talks to its host
                if (_removed.IsBlocked(sender, now)) continue;   // late packets of a peer we just removed are not a new join
                SteamNative.AcceptSession(sender);               // first contact: make sure replies get through
            }
            else if (sender == _hostId) _gotHostPacket = true;
            try { PacketReceived?.Invoke(sender, msg.Data); }
            catch (Exception e) { Log.Error($"Steam: packet handler from {sender} failed", e); }
        }
    }

    /// <summary>Fallback accept for friends the callback did not report, and keep-alive for the known peers' sessions.</summary>
    void PollAccept(long now)
    {
        try
        {
            foreach (var id in _known) SteamNative.AcceptSession(id);
            if (_isHost) SteamNative.AcceptImmediateFriends(_removed, now);
        }
        catch (Exception e) { Log.Error("Steam accept poll failed", e); }
    }

    /// <summary>Host: accept the session requests the global callback queued (any thread -> here, the main thread).</summary>
    void AcceptRequested(long now)
    {
        try
        {
            for (int i = _heldRequests.Count - 1; i >= 0; i--)
            {
                ulong held = _heldRequests[i];
                if (_removed.IsBlocked(held, now)) continue;
                _heldRequests.RemoveAt(i);
                AcceptRequest(held);
            }
            while (SteamNative.TryDequeueSessionRequest(out var id))
            {
                if (id == SteamNative.LocalSteamId) continue;
                if (_removed.IsBlocked(id, now)) { if (!_heldRequests.Contains(id)) _heldRequests.Add(id); continue; }
                AcceptRequest(id);
            }
        }
        catch (Exception e) { Log.Error("Steam session request handling failed", e); }
    }

    void AcceptRequest(ulong id)
    {
        string who = SteamNative.FriendName(id) ?? "unknown user";
        if (CoopConfig.SteamFriendsOnly && !_known.Contains(id) && !SteamNative.IsFriend(id))
        {
            Log.Warn($"Steam: ignoring session request from {who} ({id}): not on your friends list (SteamFriendsOnly=true)");
            return;
        }
        bool ok = SteamNative.AcceptSession(id);
        Log.Info($"Steam: session request from {who} ({id}) -> accepted={ok}");
    }

    /// <summary>Client: poll the session state with the host so the menu can say what is going on, and log the transitions.</summary>
    void PollSessionInfo(long now)
    {
        if (now < _nextInfoMs) return;
        _nextInfoMs = now + (_gotHostPacket ? InfoPollConnectedMs : InfoPollConnectingMs);
        if (!SteamNative.GetSessionInfo(_hostId, out var si)) return;

        string detail = SteamStates.ConnState(si.State);
        if (si.EndReason != 0) detail += ", " + SteamStates.EndReason(si.EndReason);
        if (!string.IsNullOrEmpty(si.EndDebug)) detail += " [" + si.EndDebug + "]";
        detail += "; relay " + SteamStates.Availability(SteamNative.RelayAvailability());
        _detail = detail;

        if (si.State != _lastState || si.EndReason != _lastReason)
        {
            _lastState = si.State;
            _lastReason = si.EndReason;
            Log.Info($"Steam session with host {_hostId}: {detail}");
        }
    }

    public bool Send(ulong remoteId, byte[] data, int length, bool reliable)
    {
        if (_disposed) return false;
        int flags = SteamSendFlags.For(length >= PacketHeader.Size ? BinaryPrimitives.ReadUInt16LittleEndian(data) : (ushort)0, reliable);
        int r;
        try { r = SteamNative.Send(remoteId, data, length, flags, Channel); }
        catch (Exception e)
        {
            Error ??= "Steam send failed: " + e.Message;
            LastSendResult = 0;
            return false;
        }
        LastSendResult = r;
        if (r == SteamNative.EResultOK) return true;
        if (_sendFailureLogs++ < 20 || _sendFailureLogs % 500 == 0)
            Log.Warn($"Steam send to {remoteId} failed: {SteamStates.EResult(r)} (EResult {r}), {length} bytes, flags {flags}");
        return false;
    }

    // A host keeps polling AcceptSession for the peers it knows; unknown senders are accepted on first contact.
    public void OnPeerAdded(ulong remoteId)
    {
        _known.Add(remoteId);
        _removed.Readded(remoteId);
        Deferred.Cancel(remoteId);   // a rejoin must not be killed by the close of its previous session
    }

    public void OnPeerRemoved(ulong remoteId)
    {
        _known.Remove(remoteId);
        _removed.Removed(remoteId, Environment.TickCount64);
        ScheduleClose(remoteId);
    }

    static void ScheduleClose(ulong id)
    {
        if (!Quitting) Deferred.Schedule(id, Environment.TickCount64 + CloseDelayMs);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_isHost)
        {
            SteamNative.SetHostPresence(false);
            SteamNative.SetSessionRequestCallback(false);
        }
        // The Bye was just sent: closing at once could discard it, so the close is postponed (and skipped on quit).
        foreach (var id in _known) ScheduleClose(id);
        _known.Clear();
        _pending.Clear();
    }
}
