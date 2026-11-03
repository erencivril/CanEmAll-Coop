using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using CanEmAllCoop.Core;

namespace CanEmAllCoop.Net;

/// <summary>
/// TCP transport (host listens, client connects), uint32 length-prefixed frames, non-blocking sockets polled from
/// Update(). Unreliable sends travel over the same stream. No reconnect: a dropped link ends the session.
/// </summary>
internal sealed class DirectTransport : ITransport
{
    public const ulong DirectHostId = 1;

    sealed class Conn
    {
        public ulong Id;
        public Socket Sock;
        public readonly StreamFrameDecoder Decoder = new();
        public readonly Queue<byte[]> SendQueue = new();
        public int SendOffset;
        public bool Dead;
        public bool Silent; // removed by us (peer left): no RemoteDisconnected event
    }

    readonly bool _isHost;
    readonly List<Conn> _conns = new();
    readonly byte[] _recvBuf = new byte[64 * 1024];
    TcpListener _listener;
    ulong _nextClientId = 2;

    // client connect state
    readonly string _host;
    readonly int _port;
    readonly long _giveUpAtMs;
    Socket _connecting;
    Task _connectTask;
    long _retryAtMs;

    public string Kind => "Direct";
    public ulong HostRemoteId => DirectHostId;
    public ulong LocalId => _isHost ? DirectHostId : 0;
    public bool Ready => _isHost || _conns.Count > 0;
    public bool NeedsHelloRetry => false;
    public string Error { get; private set; }
    public double ConnectTimeoutSeconds => Protocol.DirectConnectTimeoutSeconds;
    public string StatusDetail => "";
    public int LastSendResult { get; private set; } = 1;
    public int Backlog
    {
        get
        {
            foreach (var c in _conns) if (c.Decoder.HasCompleteFrame) return 1;
            return 0;
        }
    }
    public int Port { get; }

    public event Action<ulong, byte[]> PacketReceived;
    public event Action<ulong> RemoteDisconnected;

    DirectTransport(bool isHost, string host, int port, int retrySeconds)
    {
        _isHost = isHost;
        _host = host;
        Port = port;
        _port = port;
        _giveUpAtMs = Environment.TickCount64 + retrySeconds * 1000L;
    }

    public static DirectTransport Host(int port)
    {
        var t = new DirectTransport(true, null, port, 0);
        t._listener = new TcpListener(IPAddress.Any, port);
        t._listener.Server.NoDelay = true;
        t._listener.Start();
        return t;
    }

    /// <summary>retrySeconds: keep retrying refused / failed connects for that long (for two instances started together).</summary>
    public static DirectTransport Client(string host, int port, int retrySeconds)
    {
        var t = new DirectTransport(false, host, port, retrySeconds);
        t.BeginConnect();
        return t;
    }

    void BeginConnect()
    {
        try
        {
            var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            _connecting = s;
            _connectTask = s.ConnectAsync(_host, _port);
        }
        catch (Exception e)
        {
            OnConnectFailed(e.Message);
        }
    }

    void OnConnectFailed(string reason)
    {
        try { _connecting?.Dispose(); } catch (Exception) { }
        _connecting = null;
        _connectTask = null;
        if (Environment.TickCount64 < _giveUpAtMs) _retryAtMs = Environment.TickCount64 + 1000;
        else Error = $"Could not connect to {_host}:{_port} ({reason})";
    }

    public void Update()
    {
        if (_isHost) AcceptPending();
        else PollConnect();

        for (int i = _conns.Count - 1; i >= 0; i--)
        {
            var c = _conns[i];
            if (!c.Dead) { Receive(c); Flush(c); }
            if (c.Dead)
            {
                _conns.RemoveAt(i);
                try { c.Sock.Dispose(); } catch (Exception) { }
                if (c.Silent) continue;
                if (!_isHost && Error == null) Error = "Connection to host lost";
                RemoteDisconnected?.Invoke(c.Id);
            }
        }
    }

    void AcceptPending()
    {
        try
        {
            while (_listener.Pending())
            {
                var s = _listener.AcceptSocket();
                s.Blocking = false;
                s.NoDelay = true;
                _conns.Add(new Conn { Id = _nextClientId++, Sock = s });
            }
        }
        catch (Exception e) { Log.Error("DirectTransport accept failed", e); }
    }

    void PollConnect()
    {
        if (_connecting == null)
        {
            if (Error == null && _conns.Count == 0 && Environment.TickCount64 >= _retryAtMs) BeginConnect();
            return;
        }
        if (!_connectTask.IsCompleted) return;
        if (_connectTask.IsFaulted || _connectTask.IsCanceled || !_connecting.Connected)
        {
            OnConnectFailed(_connectTask.Exception?.GetBaseException().Message ?? "refused");
            return;
        }
        var s = _connecting;
        _connecting = null;
        _connectTask = null;
        s.Blocking = false;
        _conns.Add(new Conn { Id = DirectHostId, Sock = s });
    }

    // Bounded work per frame: at most ~256 KB are read from a socket and delivery stops after ~6 ms (at least 32 frames
    // always go through). What is left stays in the decoder / socket and is handled next frame.
    const int ReadBudgetBytes = 256 * 1024;
    const int MaxBufferedBytes = 2 * 1024 * 1024;
    const int MinFramesPerFrame = 32;
    const int MaxFramesPerFrame = 4096;
    static readonly long DeliverBudgetTicks = Stopwatch.Frequency * 6 / 1000;

    void Receive(Conn c)
    {
        try
        {
            int budget = ReadBudgetBytes;
            while (budget > 0 && c.Decoder.Buffered < MaxBufferedBytes && c.Sock.Available > 0)
            {
                int n = c.Sock.Receive(_recvBuf, 0, _recvBuf.Length, SocketFlags.None);
                if (n <= 0) { Log.Warn($"Direct: receive returned {n} from {c.Id}, closing"); c.Dead = true; return; }
                budget -= n;
                c.Decoder.Append(_recvBuf, 0, n);
            }
            // readable without data = closed. Poll first, Available second: data arriving between the two calls must not look like a close.
            bool eof = c.Sock.Poll(0, SelectMode.SelectRead) && c.Sock.Available == 0;

            long start = Stopwatch.GetTimestamp();
            int frames = 0;
            while (frames < MaxFramesPerFrame && c.Decoder.TryRead(out var frame))
            {
                frames++;
                PacketReceived?.Invoke(c.Id, frame);
                if (c.Dead) return;
                if (frames >= MinFramesPerFrame && Stopwatch.GetTimestamp() - start > DeliverBudgetTicks) break;
            }
            if (c.Decoder.Corrupt) { Log.Warn($"Direct: corrupt stream from {c.Id}, dropping"); c.Dead = true; }
            // A closed link is only final once everything it sent has been delivered (the last frame is often the Bye).
            else if (eof && !c.Decoder.HasCompleteFrame) { Log.Warn($"Direct: connection {c.Id} closed by the remote end"); c.Dead = true; }
        }
        catch (SocketException e) { Log.Warn($"Direct: receive from {c.Id} failed: {e.SocketErrorCode}"); c.Dead = true; }
        catch (ObjectDisposedException) { c.Dead = true; }
    }

    void Flush(Conn c)
    {
        try
        {
            while (c.SendQueue.Count > 0)
            {
                var head = c.SendQueue.Peek();
                int n = c.Sock.Send(head, c.SendOffset, head.Length - c.SendOffset, SocketFlags.None, out var err);
                c.SendOffset += n;
                if (err == SocketError.WouldBlock) return;
                if (err != SocketError.Success) { Log.Warn($"Direct: send to {c.Id} failed: {err}"); c.Dead = true; return; }
                if (c.SendOffset >= head.Length) { c.SendQueue.Dequeue(); c.SendOffset = 0; }
            }
        }
        catch (SocketException) { c.Dead = true; }
        catch (ObjectDisposedException) { c.Dead = true; }
    }

    public bool Send(ulong remoteId, byte[] data, int length, bool reliable)
    {
        foreach (var c in _conns)
        {
            if (c.Id != remoteId || c.Dead) continue;
            if (c.SendQueue.Count > 20000) { Log.Warn($"Direct: send queue overflow for {remoteId}, dropping peer"); c.Dead = true; LastSendResult = 25; return false; }
            c.SendQueue.Enqueue(Framing.EncodeFrame(data, length));
            Flush(c);
            LastSendResult = c.Dead ? 3 : 1;
            return !c.Dead;
        }
        LastSendResult = 3;   // no such connection
        return false;
    }

    public void OnPeerAdded(ulong remoteId) { }
    public void OnPeerRemoved(ulong remoteId)
    {
        // Peer left (Bye / timeout): flush what is queued and let Update() close the socket quietly.
        foreach (var c in _conns)
        {
            if (c.Id != remoteId || c.Dead) continue;
            Flush(c);
            c.Dead = true;
            c.Silent = true;
            return;
        }
    }

    public void Dispose()
    {
        foreach (var c in _conns)
        {
            try { Flush(c); c.Sock.Shutdown(SocketShutdown.Both); } catch (Exception) { }
            try { c.Sock.Dispose(); } catch (Exception) { }
        }
        _conns.Clear();
        try { _connecting?.Dispose(); } catch (Exception) { }
        try { _listener?.Stop(); } catch (Exception) { }
    }
}
