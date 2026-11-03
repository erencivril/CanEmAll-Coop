using System;
using System.Collections.Generic;
using System.Globalization;

namespace CanEmAllCoop.Net;

// Pure helpers for the Steam transport and the session layer (no Unity / Steam / P/Invoke), so they can be unit tested.

/// <summary>SteamID64 parsing for the "join by id" field and the --coop-join-steam launch argument.</summary>
public static class SteamIds
{
    /// <summary>Universe public, type individual, instance desktop, account 0.</summary>
    public const ulong IndividualBase = 0x0110000100000000UL;
    public const ulong IndividualMax = IndividualBase + 0xFFFFFFFFUL;

    public static bool IsPlausible(ulong id) => id >= IndividualBase && id <= IndividualMax;

    /// <summary>
    /// Accepts a bare 17-digit id, an id with spaces / quotes around it, or a steamcommunity.com/profiles/&lt;id&gt; URL.
    /// Vanity URLs (/id/name) cannot be resolved offline and are rejected.
    /// </summary>
    public static bool TryParse(string text, out ulong id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] < '0' || text[i] > '9') { i++; continue; }
            int start = i;
            while (i < text.Length && text[i] >= '0' && text[i] <= '9') i++;
            if (i - start != 17) continue;
            if (ulong.TryParse(text.AsSpan(start, 17), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && IsPlausible(v))
            {
                id = v;
                return true;
            }
        }
        return false;
    }
}

/// <summary>Human readable names for the numeric states Steam reports (kept short; the numeric code is always shown too).</summary>
public static class SteamStates
{
    // ESteamNetworkingConnectionState
    public static string ConnState(int s) => s switch
    {
        0 => "no session yet",
        1 => "connecting",
        2 => "finding a route",
        3 => "connected",
        4 => "closed by the other side",
        5 => "problem detected locally",
        -1 => "unknown",
        -2 => "lingering",
        _ => "state " + s.ToString(CultureInfo.InvariantCulture),
    };

    // ESteamNetConnectionEnd (only the values worth explaining to a player)
    public static string EndReason(int r)
    {
        string text = r switch
        {
            0 => null,
            3001 => "Steam is in offline mode",
            3002 => "cannot reach enough Steam relays",
            3004 => "Steam network configuration unavailable",
            3005 => "not allowed to connect",
            3006 => "no public address (ICE)",
            4001 => "remote side timed out",
            4002 or 4003 => "authentication failed",
            4006 => "remote Steam networking version mismatch",
            4007 => "remote side has no public address (ICE)",
            5001 => "generic failure",
            5002 => "internal Steam error",
            5003 => "timed out",
            5005 => "cannot reach Steam",
            5006 => "no relay sessions available",
            5008 => "rendezvous failed",
            5009 => "NAT / firewall blocked the connection",
            5010 => "the remote side never answered the connection request",
            _ => r >= 1000 && r < 2000 ? "closed by the application" : r >= 2000 && r < 3000 ? "application error" : null,
        };
        string code = r.ToString(CultureInfo.InvariantCulture);
        return text == null ? "code " + code : text + " (" + code + ")";
    }

    // EResult as returned by ISteamNetworkingMessages::SendMessageToUser
    public static string EResult(int r) => r switch
    {
        1 => "OK",
        2 => "generic failure",
        3 => "no connection",
        8 => "invalid parameter",
        15 => "access denied",
        16 => "timeout",
        20 => "service unavailable",
        25 => "send buffer full",
        _ => "EResult " + r.ToString(CultureInfo.InvariantCulture),
    };

    // ESteamNetworkingAvailability
    public static string Availability(int a) => a switch
    {
        100 => "ready",
        3 => "attempting",
        2 => "waiting",
        1 => "not tried yet",
        0 => "unknown",
        -10 => "retrying",
        -100 => "was ready before",
        -101 => "failed",
        -102 => "cannot try",
        _ => "availability " + a.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>Byte offsets inside SteamNetConnectionInfo_t (x64, Steamworks.NET Pack=8 layout). Verified against a mirror struct in the tests.</summary>
public static class SteamConnInfoLayout
{
    public const int Size = 696;
    public const int IdentityRemote = 0;       // SteamNetworkingIdentity, 136 bytes
    public const int UserData = 136;           // int64
    public const int ListenSocket = 144;       // uint32
    public const int AddrRemote = 148;         // SteamNetworkingIPAddr, 18 bytes
    public const int PopRemote = 168;          // uint32 (after a 2-byte pad)
    public const int PopRelay = 172;           // uint32
    public const int State = 176;              // int, ESteamNetworkingConnectionState
    public const int EndReason = 180;          // int, ESteamNetConnectionEnd
    public const int EndDebug = 184;           // char[128]
    public const int EndDebugSize = 128;
    public const int Description = 312;        // char[128]
    public const int Flags = 440;              // int
}

/// <summary>Counts consecutive send failures; trips when there were at least `threshold` in a row spanning at least `minSpanMs`.</summary>
public sealed class SendFailureTracker
{
    readonly int _threshold;
    readonly long _minSpanMs;
    long _firstFailMs;

    public SendFailureTracker(int threshold = 40, long minSpanMs = 3000)
    {
        _threshold = threshold;
        _minSpanMs = minSpanMs;
    }

    public int Consecutive { get; private set; }

    /// <summary>Returns true while the failure streak is long enough to call the link dead.</summary>
    public bool Record(bool ok, long nowMs)
    {
        if (ok) { Consecutive = 0; return false; }
        if (Consecutive == 0) _firstFailMs = nowMs;
        Consecutive++;
        return Consecutive >= _threshold && nowMs - _firstFailMs >= _minSpanMs;
    }

    public void Reset() => Consecutive = 0;
}

/// <summary>
/// Detects that the main thread did not run for a while (a hitch). The caller credits that time to every receive
/// timeout so that our own stall is never mistaken for the other side going silent.
/// </summary>
public sealed class StallGuard
{
    readonly long _thresholdMs;
    long _lastMs;

    public StallGuard(long thresholdMs = 1000) => _thresholdMs = thresholdMs;

    public void Reset() => _lastMs = 0;

    /// <summary>Call once per frame. Returns the length of the stall in ms when the gap since the last call exceeded the threshold, else 0.</summary>
    public long Tick(long nowMs)
    {
        long gap = _lastMs == 0 ? 0 : nowMs - _lastMs;
        _lastMs = nowMs;
        return gap > _thresholdMs ? gap : 0;
    }
}

/// <summary>Peers that were just removed: their late packets must not count as a fresh first contact.</summary>
public sealed class RemovedPeerGuard
{
    readonly Dictionary<ulong, long> _until = new();
    readonly long _graceMs;

    public RemovedPeerGuard(long graceMs = 5000) => _graceMs = graceMs;

    public void Removed(ulong id, long nowMs) => _until[id] = nowMs + _graceMs;
    public void Readded(ulong id) => _until.Remove(id);
    public void Clear() => _until.Clear();

    public bool IsBlocked(ulong id, long nowMs)
    {
        if (!_until.TryGetValue(id, out var until)) return false;
        if (nowMs < until) return true;
        _until.Remove(id);
        return false;
    }
}

/// <summary>Session closes that are postponed so the last reliable message (Bye) can still leave. A new session with the same id cancels the close.</summary>
public sealed class DeferredCloseSchedule
{
    readonly Dictionary<ulong, long> _due = new();
    readonly List<ulong> _tmp = new();

    public int Count => _due.Count;

    public void Schedule(ulong id, long dueMs) => _due[id] = dueMs;
    public void Cancel(ulong id) => _due.Remove(id);

    /// <summary>Moves every close that is due into `into` (and forgets it).</summary>
    public void CollectDue(long nowMs, List<ulong> into)
    {
        if (_due.Count == 0) return;
        _tmp.Clear();
        foreach (var kv in _due) if (nowMs >= kv.Value) _tmp.Add(kv.Key);
        foreach (var id in _tmp) { _due.Remove(id); into.Add(id); }
    }
}

/// <summary>Fires at most once per interval.</summary>
public sealed class RetryGate
{
    readonly long _intervalMs;
    long _nextMs;

    public RetryGate(long intervalMs) => _intervalMs = intervalMs;

    public bool Ready(long nowMs)
    {
        if (nowMs < _nextMs) return false;
        _nextMs = nowMs + _intervalMs;
        return true;
    }

    public void Reset() => _nextMs = 0;
}

/// <summary>k_nSteamNetworkingSend_* flag choice.</summary>
public static class SteamSendFlags
{
    public const int AutoRestartBrokenSession = 32;
    public const int Reliable = 8;
    public const int ReliableNoNagle = 9;
    public const int UnreliableNoDelay = 5;

    /// <summary>Hello / Welcome / PeerList / Bye / Ping / Pong / Refuse: small, latency sensitive, must arrive.</summary>
    public static bool IsCoreControl(ushort msgId) => msgId < CoreMsg.FirstUserId && msgId != CoreMsg.Fragment;

    public static int For(ushort packetHeader, bool reliable)
    {
        if (!reliable) return UnreliableNoDelay | AutoRestartBrokenSession;
        return (IsCoreControl(PacketHeader.Id(packetHeader)) ? ReliableNoNagle : Reliable) | AutoRestartBrokenSession;
    }
}

/// <summary>Order of the friend list: hosting first, then in-game, then online, then the rest; alphabetical inside a group.</summary>
public static class FriendOrder
{
    public static int Rank(bool hosting, bool inGame, int personaState) =>
        hosting ? 0 : inGame ? 1 : personaState > 0 ? 2 : 3;

    public static int Compare(int rankA, string nameA, int rankB, string nameB)
    {
        int c = rankA.CompareTo(rankB);
        return c != 0 ? c : string.Compare(nameA, nameB, StringComparison.OrdinalIgnoreCase);
    }
}
