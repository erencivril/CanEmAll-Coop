using System;
using System.Collections.Generic;
using CanEmAllCoop.Core;
using UnityEngine;

namespace CanEmAllCoop.Flow;
using CanEmAllCoop.Net;
using Net = CanEmAllCoop.Net.Net;

/// <summary>
/// F3: one death must not cancel the team's day.
/// GameManager.Suicide is intercepted: the local player is "down" for RespawnSeconds (input zeroed by the game itself
/// because IsSuiciding stays true, grinder damage ignored), is moved to the day's spawn point and then revived. Only when
/// EVERY player of the running day is down does the host broadcast TeamWipe and everybody runs the original Suicide
/// (day aborted, shop, day++), exactly as in the solo game.
/// </summary>
internal static partial class FlowCore
{
    const float DefaultDelay = 1f;
    const int DefaultMail = 41;               // MailId.YoureNotFood

    static readonly DownTracker _tracker = new();     // host
    static readonly HashSet<ulong> _downSet = new();  // everyone: ids that are down (HUD)
    static readonly List<ulong> _players = new();
    static bool _down;
    static float _respawnAt;
    static float _lastDelay = DefaultDelay;
    static int _lastMail = DefaultMail;
    static GameObject _spawnMarker;

    internal static float DownSecondsLeft => _down ? Mathf.Max(0f, _respawnAt - Time.unscaledTime) : 0f;
    internal static bool IsDown => _down;

    internal static bool IsPlayerDown(ulong id)
    {
        if (id == Net.LocalId) return _down;
        return Net.IsHost ? _tracker.Contains(id) : _downSet.Contains(id);
    }

    internal static bool TryGetSpawn(out Vector3 position, out float yaw)
    {
        position = _spawnPos;
        yaw = _spawnYaw;
        return _spawnValid;
    }

    static void ResetDownState()
    {
        _down = false;
        _tracker.Clear();
        _downSet.Clear();
        _lastDelay = DefaultDelay;
        _lastMail = DefaultMail;
    }

    /// <summary>Prefix of GameManager.Suicide. true = run the original.</summary>
    internal static bool SuicideRequested(GameManager gm, float delay, int mail)
    {
        if (!Online || Bypass || !Playing) return true;
        if (_down) return false;             // the grinder / tube check keeps calling while we are down
        StartDown(gm, delay, mail);
        return false;
    }

    static void StartDown(GameManager gm, float delay, int mail)
    {
        _down = true;
        _respawnAt = Time.unscaledTime + Mathf.Max(0.5f, CoopConfig.RespawnSeconds);
        _lastDelay = delay;
        _lastMail = mail;
        Log.Info($"Flow: local player is down (respawn in {CoopConfig.RespawnSeconds:0.#}s)");

        // IsSuiciding: the game zeroes movement input and ignores Player.Damage / CheckFalling while it is set
        if (gm != null) gm.IsSuiciding = true;
        var p = Player.Instance;
        if (p != null)
        {
            try { if (p.HeldEnemy != null) p.DropHeldBody(); }
            catch (Exception) { /* nothing held */ }
            p.ResetDamage();
        }
        TeleportToSpawn();        // out of the grinder / the pit; the fall would otherwise continue during the downtime

        if (Net.IsHost) HostPlayerDown(Net.LocalPeer, delay, mail);
        else Net.SendToHost(FlowMsg.PlayerDown, w => { w.WriteFloat(delay); w.WriteInt(mail); });
    }

    static void UpdateDown()
    {
        if (_down && Time.unscaledTime >= _respawnAt) Respawn();
    }

    static void Respawn()
    {
        if (!_down) return;
        _down = false;
        var gm = GM;
        if (gm != null) gm.IsSuiciding = false;
        var p = Player.Instance;
        if (p != null) p.ResetDamage();
        TeleportToSpawn();
        Log.Info("Flow: local player respawned");
        if (Net.IsHost) HostPlayerUp(Net.LocalPeer);
        else Net.SendToHost(FlowMsg.PlayerUp, w => { });
    }

    /// <summary>The day ended (or the session did) while we were down: revive without waiting.</summary>
    static void EndDownPhase()
    {
        if (_down) Respawn();
    }

    static void TeleportToSpawn()
    {
        var p = Player.Instance;
        if (p == null) return;
        Transform target = null;
        if (_spawnValid)
        {
            if (_spawnMarker == null) _spawnMarker = new GameObject("CanEmAllCoop.Spawn");
            _spawnMarker.transform.position = _spawnPos;
            _spawnMarker.transform.rotation = Quaternion.Euler(0f, _spawnYaw, 0f);
            target = _spawnMarker.transform;
        }
        else
        {
            var gm = GM;
            if (gm != null) target = gm.elevatorStartPoint;      // vanilla respawn point; may be inside the closed elevator
        }
        if (target != null) p.Teleport(target);
    }

    // ------------------------------------------------------------------------------------------------ host side

    static void HostPlayerDown(Peer peer, float delay, int mail)
    {
        if (peer == null) return;
        _lastDelay = delay;
        _lastMail = mail;
        if (_tracker.MarkDown(peer.Id)) BroadcastDownList();
        HostEvaluateWipe();
    }

    static void HostPlayerUp(Peer peer)
    {
        if (peer == null) return;
        if (_tracker.MarkUp(peer.Id)) BroadcastDownList();
    }

    static void HostPeerGone(ulong id)
    {
        if (_tracker.MarkUp(id)) BroadcastDownList();
        HostEvaluateWipe();       // the remaining players may all be down
    }

    static void HostEvaluateWipe()
    {
        if (!Net.IsHost || _suicideActive || LoopState < 0 || _tracker.Count == 0) return;
        _players.Clear();
        var peers = Net.Peers;
        for (int i = 0; i < peers.Count; i++)
        {
            var p = peers[i];
            if (p.IsHost ? GameStarted : _syncedIds.Contains(p.Id)) _players.Add(p.Id);
        }
        if (!_tracker.TeamWiped(_players)) return;

        int mail = _lastMail;
        float delay = _lastDelay;
        Log.Info($"Flow: every player is down ({_players.Count}) -> team wipe");
        _tracker.Clear();
        BroadcastDownList();
        Net.Broadcast(FlowMsg.TeamWipe, w => { w.WriteFloat(delay); w.WriteInt(mail); });
        RunTeamWipe(delay, mail);
    }

    static void BroadcastDownList()
    {
        _downSet.Clear();
        var ids = new List<ulong>(_tracker.Ids);
        foreach (var id in ids) _downSet.Add(id);
        Net.Broadcast(FlowMsg.DownList, w =>
        {
            w.WriteByte((byte)ids.Count);
            foreach (var id in ids) w.WriteULong(id);
        });
    }

    // ------------------------------------------------------------------------------------------------ all roles

    /// <summary>Everybody runs the original day abort once.</summary>
    static void RunTeamWipe(float delay, int mail)
    {
        if (!Net.IsHost && !_inSync) return;          // still in the first elevator: not part of this day
        var gm = GM;
        if (gm == null) return;
        _down = false;
        _downSet.Clear();
        gm.IsSuiciding = false;                       // the original Suicide is a no-op while the flag is set
        Bypass = true;
        try { gm.Suicide(delay, (MailId)mail); }
        finally { Bypass = false; }
    }

    internal static void OnPlayerDown(Peer from, NetReader r)
    {
        if (!Net.IsHost) return;
        float delay = r.ReadFloat();
        int mail = r.ReadInt();
        HostPlayerDown(from, delay, mail);
    }

    internal static void OnPlayerUp(Peer from, NetReader r)
    {
        if (!Net.IsHost) return;
        HostPlayerUp(from);
    }

    internal static void OnTeamWipe(Peer from, NetReader r)
    {
        if (Net.IsHost) return;
        float delay = r.ReadFloat();
        int mail = r.ReadInt();
        RunTeamWipe(delay, mail);
    }

    internal static void OnDownList(Peer from, NetReader r)
    {
        if (Net.IsHost) return;
        int n = r.ReadByte();
        _downSet.Clear();
        for (int i = 0; i < n; i++) _downSet.Add(r.ReadULong());
    }
}
