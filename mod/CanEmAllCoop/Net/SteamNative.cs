using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using CanEmAllCoop.Core;

namespace CanEmAllCoop.Net;

/// <summary>
/// Raw P/Invoke into the game's already-initialised steam_api64.dll (flat C API, SDK 1.6x interface versions:
/// ISteamNetworkingMessages v002, ISteamFriends v017, ISteamUser v023). SteamAPI_Init is never called here.
/// Struct layouts follow the C headers for x64 Windows (verified against Steamworks.NET's Pack=8 layouts); the ones
/// that matter are declared with explicit offsets so a wrong Pack assumption cannot shift them.
/// </summary>
internal static unsafe class SteamNative
{
    public const uint GameAppId = 5133900;
    const string Lib = "steam_api64";

    // ESteamNetworkingIdentityType
    const int IdentityTypeSteamId = 16;
    public const int EResultOK = 1;
    public const string HostPresenceKey = "coop_host";
    // ESteamNetworkingConfigValue
    const int ConfigSendBufferSize = 9, ConfigTimeoutInitial = 24, ConfigTimeoutConnected = 25;
    // EFriendFlags.k_EFriendFlagImmediate
    const int FriendFlagImmediate = 0x04;

    /// <summary>SteamNetworkingIdentity: int type, int cbSize, 128-byte union (steamID64 at its start) = 136 bytes.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 136)]
    public struct SteamNetworkingIdentity
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(4)] public int CbSize;
        [FieldOffset(8)] public ulong SteamId64;

        public static SteamNetworkingIdentity FromSteamId(ulong id) =>
            new SteamNetworkingIdentity { Type = IdentityTypeSteamId, CbSize = 8, SteamId64 = id };
    }

    /// <summary>
    /// SteamNetworkingMessage_t (x64): m_pData @0, m_cbSize @8, m_conn @12, m_identityPeer @16 (136 bytes),
    /// m_nConnUserData @152, m_usecTimeReceived @160, m_nMessageNumber @168, m_pfnFreeData @176, m_pfnRelease @184,
    /// m_nChannel @192, m_nFlags @196, m_nUserData @200, m_idxLane @208. Only the first fields are read.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 216)]
    public struct SteamNetworkingMessage
    {
        [FieldOffset(0)] public IntPtr Data;
        [FieldOffset(8)] public int Size;
        [FieldOffset(12)] public uint Conn;
        [FieldOffset(16)] public SteamNetworkingIdentity IdentityPeer;
        [FieldOffset(192)] public int Channel;
    }

    /// <summary>FriendGameInfo_t: CGameID m_gameID @0 (bits 0-23 app id, 24-31 type, 32-63 mod id), uint ip @8, ushort ports @12/@14, CSteamID lobby @16.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct FriendGameInfo
    {
        [FieldOffset(0)] public ulong GameId;
        [FieldOffset(8)] public uint GameIp;
        [FieldOffset(12)] public ushort GamePort;
        [FieldOffset(14)] public ushort QueryPort;
        [FieldOffset(16)] public ulong LobbySteamId;
    }

    public struct SteamFriend
    {
        public ulong Id;
        public string Name;
        /// <summary>Currently playing this game (full CGameID equals the app id).</summary>
        public bool InGame;
        /// <summary>Rich presence "coop_host" = "1".</summary>
        public bool Hosting;
        /// <summary>EPersonaState: 0 offline, 1 online, ...</summary>
        public int PersonaState;
    }
    public struct SessionInfo { public int State; public int EndReason; public string EndDebug; }
    public struct Inbound { public ulong Sender; public byte[] Data; }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr SteamAPI_SteamFriends_v017();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr SteamAPI_SteamUser_v023();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr SteamAPI_SteamNetworkingUtils_SteamAPI_v004();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern void SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(IntPtr self);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int SteamAPI_ISteamNetworkingUtils_GetRelayNetworkStatus(IntPtr self, byte* outStatus);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern void SteamAPI_ISteamNetworkingUtils_SetGlobalCallback_MessagesSessionRequest(IntPtr self, IntPtr fnCallback);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32(IntPtr self, int configValue, int value);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    static extern int SteamAPI_ISteamNetworkingMessages_SendMessageToUser(IntPtr self, ref SteamNetworkingIdentity identityRemote, byte* data, uint cbData, int sendFlags, int remoteChannel);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    static extern int SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(IntPtr self, int localChannel, IntPtr* outMessages, int maxMessages);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser(IntPtr self, ref SteamNetworkingIdentity identityRemote);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser(IntPtr self, ref SteamNetworkingIdentity identityRemote);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    static extern int SteamAPI_ISteamNetworkingMessages_GetSessionConnectionInfo(IntPtr self, ref SteamNetworkingIdentity identityRemote, byte* connectionInfo, IntPtr quickStatus);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    static extern void SteamAPI_SteamNetworkingMessage_t_Release(IntPtr message);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern ulong SteamAPI_ISteamUser_GetSteamID(IntPtr self);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr SteamAPI_ISteamFriends_GetPersonaName(IntPtr self);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr SteamAPI_ISteamFriends_GetFriendPersonaName(IntPtr self, ulong steamId);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int SteamAPI_ISteamFriends_GetFriendCount(IntPtr self, int friendFlags);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern ulong SteamAPI_ISteamFriends_GetFriendByIndex(IntPtr self, int index, int friendFlags);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int SteamAPI_ISteamFriends_GetFriendRelationship(IntPtr self, ulong steamId);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int SteamAPI_ISteamFriends_GetFriendPersonaState(IntPtr self, ulong steamId);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool SteamAPI_ISteamFriends_GetFriendGamePlayed(IntPtr self, ulong steamId, out FriendGameInfo info);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool SteamAPI_ISteamFriends_SetRichPresence(IntPtr self, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr SteamAPI_ISteamFriends_GetFriendRichPresence(IntPtr self, ulong steamId, [MarshalAs(UnmanagedType.LPUTF8Str)] string key);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern void SteamAPI_ISteamFriends_RequestFriendRichPresence(IntPtr self, ulong steamId);

    static IntPtr _messages, _friends, _user, _utils;
    static string _initError;
    static bool _resolverSet;
    static IntPtr* _recv;
    static byte* _scratch;          // connection info / relay status out-buffers, zeroed before each use
    const int ScratchSize = 1024;
    static readonly RetryGate InitGate = new(2000);
    static readonly ConcurrentQueue<ulong> SessionRequests = new();
    static bool _callbackSet;
    static bool _layoutWarned;

    /// <summary>Why Init failed (null while available or not yet tried).</summary>
    public static string InitError => _initError;
    public static bool Available { get; private set; }
    public static ulong LocalSteamId { get; private set; }
    public static string LocalName { get; private set; }

    /// <summary>Diagnostics for --coop-debug-steamcheck.</summary>
    public static IntPtr DebugMessagesPtr() => _messages;
    public static int DebugFriendCount() => Available ? SteamAPI_ISteamFriends_GetFriendCount(_friends, FriendFlagImmediate) : -1;
    /// <summary>Our own value of the host rich presence key as Steam reports it back (null when unset or Steam is unavailable).</summary>
    public static string DebugOwnHostPresence() => Available ? PtrToUtf8(SteamAPI_ISteamFriends_GetFriendRichPresence(_friends, LocalSteamId, HostPresenceKey)) : null;

    /// <summary>
    /// Binds the interfaces of the already-running Steam client. Only success is cached: a failure (Steam not yet
    /// initialised by the game, or not logged in) is retried at most every 2 s, so a too-early first call cannot latch.
    /// </summary>
    public static bool Init(bool force = false)
    {
        if (Available) return true;
        if (force) InitGate.Reset();
        if (!InitGate.Ready(Environment.TickCount64)) return false;
        try
        {
            SetResolver();
            _messages = SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
            _friends = SteamAPI_SteamFriends_v017();
            _user = SteamAPI_SteamUser_v023();
            if (_messages == IntPtr.Zero || _friends == IntPtr.Zero || _user == IntPtr.Zero)
                throw new InvalidOperationException("Steam interfaces unavailable (Steam not initialised / not running)");

            ulong id = SteamAPI_ISteamUser_GetSteamID(_user);
            if (id == 0) throw new InvalidOperationException("GetSteamID returned 0 (not logged in)");
            LocalSteamId = id;
            LocalName = PtrToUtf8(SteamAPI_ISteamFriends_GetPersonaName(_friends));

            _utils = SteamAPI_SteamNetworkingUtils_SteamAPI_v004();
            try
            {
                if (_utils != IntPtr.Zero)
                {
                    SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(_utils);
                    // Snapshot fragments are sent reliably in one burst: a 512 KB default send buffer could reject them.
                    SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32(_utils, ConfigSendBufferSize, 4 * 1024 * 1024);
                    SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32(_utils, ConfigTimeoutInitial, (int)(CoopConfig.SteamConnectTimeoutSeconds * 1000));
                    SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32(_utils, ConfigTimeoutConnected, (int)(CoopConfig.PeerTimeoutSeconds * 1000));
                }
            }
            catch (Exception e) { Log.Warn($"Steam: relay / config setup failed (continuing): {e.Message}"); }

            if (_recv == null) _recv = (IntPtr*)Marshal.AllocHGlobal(sizeof(IntPtr) * 256);
            if (_scratch == null) _scratch = (byte*)Marshal.AllocHGlobal(ScratchSize);
            Available = true;
            _initError = null;
            Log.Info($"Steam transport available: {LocalName} ({LocalSteamId})");
        }
        catch (Exception e)
        {
            Available = false;
            _messages = _friends = _user = _utils = IntPtr.Zero;
            LocalSteamId = 0;
            if (e.Message != _initError) Log.Warn($"Steam transport unavailable (will retry): {e.Message}");
            _initError = e.Message;
        }
        return Available;
    }

    // steam_api64.dll is already loaded by the game; make [DllImport("steam_api64")] bind to that module instead of probing.
    static void SetResolver()
    {
        if (_resolverSet) return;
        NativeLibrary.SetDllImportResolver(typeof(SteamNative).Assembly, ResolveSteam);
        _resolverSet = true;
    }

    static IntPtr ResolveSteam(string name, Assembly asm, DllImportSearchPath? path) =>
        string.Equals(name, Lib, StringComparison.OrdinalIgnoreCase) ? GetModuleHandle("steam_api64.dll") : IntPtr.Zero;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string name);

    static string PtrToUtf8(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);

    static string BoundedUtf8(byte* p, int max)
    {
        int n = 0;
        while (n < max && p[n] != 0) n++;
        return n == 0 ? "" : System.Text.Encoding.UTF8.GetString(p, n);
    }

    // ------------------------------------------------------------------------------------------ session requests

    // The reverse-P/Invoke entry point is a delegate thunk rather than an [UnmanagedCallersOnly] method: the interop
    // assemblies of this game also define UnityEngine's UnmanagedCallersOnlyAttribute, which makes the attribute
    // ambiguous in this project. The thunk behaves the same (any native thread may call it).
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void SessionRequestFn(IntPtr request);
    static SessionRequestFn _sessionRequestFn;   // kept alive for as long as Steam may call it
    static IntPtr _sessionRequestPtr;

    /// <summary>
    /// Called by Steam when a user we have no session with sends us a message (global callback, may arrive on any
    /// thread). Only queues the id; the transport accepts it on the main thread. Must never throw.
    /// </summary>
    static void OnSessionRequest(IntPtr requestPtr)
    {
        try
        {
            var request = (SteamNetworkingIdentity*)requestPtr;   // SteamNetworkingMessagesSessionRequest_t starts with the identity
            if (request == null || request->Type != IdentityTypeSteamId || request->SteamId64 == 0) return;
            if (SessionRequests.Count < 256) SessionRequests.Enqueue(request->SteamId64);
        }
        catch (Exception) { }
    }

    /// <summary>
    /// Registers (or removes) the global MessagesSessionRequest callback. The game's Facepunch dispatcher does not
    /// forward this callback, so without it a host would never learn who wants to talk to it.
    /// </summary>
    public static bool SetSessionRequestCallback(bool on)
    {
        if (!Available || _utils == IntPtr.Zero) return false;
        try
        {
            if (on && _sessionRequestPtr == IntPtr.Zero)
            {
                _sessionRequestFn = OnSessionRequest;
                _sessionRequestPtr = Marshal.GetFunctionPointerForDelegate(_sessionRequestFn);
            }
            SteamAPI_ISteamNetworkingUtils_SetGlobalCallback_MessagesSessionRequest(_utils, on ? _sessionRequestPtr : IntPtr.Zero);
            _callbackSet = on;
            if (!on) while (SessionRequests.TryDequeue(out _)) { }
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"Steam: session request callback {(on ? "registration" : "removal")} failed (the friend poll still works): {e.Message}");
            return false;
        }
    }

    public static bool SessionRequestCallbackSet => _callbackSet;

    public static bool TryDequeueSessionRequest(out ulong id) => SessionRequests.TryDequeue(out id);

    // ------------------------------------------------------------------------------------------ messages

    /// <summary>Returns the EResult of SendMessageToUser (1 = OK).</summary>
    public static int Send(ulong steamId, byte[] data, int length, int sendFlags, int channel)
    {
        var id = SteamNetworkingIdentity.FromSteamId(steamId);
        fixed (byte* p = data)
            return SteamAPI_ISteamNetworkingMessages_SendMessageToUser(_messages, ref id, p, (uint)length, sendFlags, channel);
    }

    public static bool AcceptSession(ulong steamId)
    {
        var id = SteamNetworkingIdentity.FromSteamId(steamId);
        return SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser(_messages, ref id);
    }

    public static void CloseSession(ulong steamId)
    {
        var id = SteamNetworkingIdentity.FromSteamId(steamId);
        SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser(_messages, ref id);
    }

    /// <summary>
    /// Drains up to maxMessages from the channel; each payload is copied and every native message is released, also
    /// when copying one of them throws.
    /// </summary>
    public static int Receive(int channel, int maxMessages, List<Inbound> into)
    {
        int total = 0;
        while (total < maxMessages)
        {
            int n = SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(_messages, channel, _recv, Math.Min(256, maxMessages - total));
            if (n <= 0) break;
            for (int i = 0; i < n; i++)
            {
                var msg = _recv[i];
                try
                {
                    var m = (SteamNetworkingMessage*)msg;
                    if (m->IdentityPeer.Type == IdentityTypeSteamId && m->Size > 0)
                    {
                        var copy = new byte[m->Size];
                        Marshal.Copy(m->Data, copy, 0, m->Size);
                        into.Add(new Inbound { Sender = m->IdentityPeer.SteamId64, Data = copy });
                    }
                }
                catch (Exception e) { Log.Warn($"Steam: dropping a message that could not be read: {e.Message}"); }
                finally { SteamAPI_SteamNetworkingMessage_t_Release(msg); }
            }
            total += n;
            if (n < 256) break;
        }
        return total;
    }

    // ------------------------------------------------------------------------------------------ diagnostics

    /// <summary>State of the messages session with a user (ESteamNetworkingConnectionState, end reason, Steam's debug text).</summary>
    public static bool GetSessionInfo(ulong steamId, out SessionInfo info)
    {
        info = default;
        if (!Available || _scratch == null) return false;
        try
        {
            new Span<byte>(_scratch, ScratchSize).Clear();
            var id = SteamNetworkingIdentity.FromSteamId(steamId);
            int state = SteamAPI_ISteamNetworkingMessages_GetSessionConnectionInfo(_messages, ref id, _scratch, IntPtr.Zero);
            info.State = state;
            // Self-check of the struct layout: the state at its documented offset must equal the call's return value.
            if (*(int*)(_scratch + SteamConnInfoLayout.State) != state)
            {
                if (!_layoutWarned) { _layoutWarned = true; Log.Warn("Steam: SteamNetConnectionInfo_t layout does not match this Steam version; end reason / debug text are not shown"); }
                return true;
            }
            info.EndReason = *(int*)(_scratch + SteamConnInfoLayout.EndReason);
            info.EndDebug = BoundedUtf8(_scratch + SteamConnInfoLayout.EndDebug, SteamConnInfoLayout.EndDebugSize);
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>ESteamNetworkingAvailability of the Steam relay network (100 = ready); -999 when unknown.</summary>
    public static int RelayAvailability()
    {
        if (!Available || _utils == IntPtr.Zero || _scratch == null) return -999;
        try
        {
            new Span<byte>(_scratch, ScratchSize).Clear();
            return SteamAPI_ISteamNetworkingUtils_GetRelayNetworkStatus(_utils, _scratch);
        }
        catch (Exception) { return -999; }
    }

    // ------------------------------------------------------------------------------------------ friends / presence

    /// <summary>Advertises (or withdraws) "this player is hosting" to friends. Only our own key is touched; null deletes it.</summary>
    public static void SetHostPresence(bool hosting)
    {
        if (!Available) return;
        try { SteamAPI_ISteamFriends_SetRichPresence(_friends, HostPresenceKey, hosting ? "1" : null); }
        catch (Exception e) { Log.Warn($"Steam: SetRichPresence failed: {e.Message}"); }
    }

    /// <summary>True for users on our friends list (k_EFriendRelationshipFriend = 3).</summary>
    public static bool IsFriend(ulong steamId)
    {
        if (!Available) return false;
        try { return SteamAPI_ISteamFriends_GetFriendRelationship(_friends, steamId) == 3; }
        catch (Exception) { return false; }
    }

    /// <summary>Persona name when Steam knows it, else null.</summary>
    public static string FriendName(ulong steamId)
    {
        if (!Available) return null;
        try
        {
            var name = PtrToUtf8(SteamAPI_ISteamFriends_GetFriendPersonaName(_friends, steamId));
            return string.IsNullOrEmpty(name) || name == "[unknown]" ? null : name;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Every immediate friend, sorted hosting first, then playing this game, then online, then the rest.
    /// requestPresence asks Steam for the rich presence of friends that are not in this game (they may be hosting
    /// with their game details hidden).
    /// </summary>
    public static void GetFriends(List<SteamFriend> into, bool requestPresence)
    {
        into.Clear();
        if (!Available) return;
        try
        {
            int count = SteamAPI_ISteamFriends_GetFriendCount(_friends, FriendFlagImmediate);
            for (int i = 0; i < count; i++)
            {
                ulong id = SteamAPI_ISteamFriends_GetFriendByIndex(_friends, i, FriendFlagImmediate);
                if (id == 0) continue;
                // Full CGameID compare: low 24 bits app id, then type and mod id must be zero too.
                bool inGame = SteamAPI_ISteamFriends_GetFriendGamePlayed(_friends, id, out var info) && info.GameId == GameAppId;
                if (!inGame && requestPresence) SteamAPI_ISteamFriends_RequestFriendRichPresence(_friends, id);
                bool hosting = PtrToUtf8(SteamAPI_ISteamFriends_GetFriendRichPresence(_friends, id, HostPresenceKey)) == "1";
                into.Add(new SteamFriend
                {
                    Id = id,
                    Name = PtrToUtf8(SteamAPI_ISteamFriends_GetFriendPersonaName(_friends, id)) ?? id.ToString(),
                    InGame = inGame,
                    Hosting = hosting,
                    PersonaState = SteamAPI_ISteamFriends_GetFriendPersonaState(_friends, id),
                });
            }
            into.Sort((a, b) => FriendOrder.Compare(
                FriendOrder.Rank(a.Hosting, a.InGame, a.PersonaState), a.Name,
                FriendOrder.Rank(b.Hosting, b.InGame, b.PersonaState), b.Name));
        }
        catch (Exception e) { Log.Error("Steam friend scan failed", e); }
    }

    static readonly List<SteamFriend> ScanTmp = new();

    /// <summary>Immediate friends whose current game is this one (appid 5133900), with persona names.</summary>
    public static void GetFriendsInGame(List<SteamFriend> into)
    {
        GetFriends(ScanTmp, false);
        into.Clear();
        foreach (var f in ScanTmp) if (f.InGame) into.Add(f);
    }

    /// <summary>
    /// Fallback accept: AcceptSessionWithUser for every immediate friend, whatever game Steam says they play (private
    /// game details, invisible status and slow presence must not lock a friend out). Cheap: ids only, no names.
    /// </summary>
    public static void AcceptImmediateFriends(RemovedPeerGuard recentlyRemoved, long nowMs)
    {
        if (!Available) return;
        int count = SteamAPI_ISteamFriends_GetFriendCount(_friends, FriendFlagImmediate);
        for (int i = 0; i < count; i++)
        {
            ulong id = SteamAPI_ISteamFriends_GetFriendByIndex(_friends, i, FriendFlagImmediate);
            if (id == 0 || recentlyRemoved.IsBlocked(id, nowMs)) continue;
            AcceptSession(id);
        }
    }
}
