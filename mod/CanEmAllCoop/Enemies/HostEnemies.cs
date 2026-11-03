using System;
using System.Collections.Generic;
using CanEmAllCoop.Core;
using UnityEngine;

namespace CanEmAllCoop.Enemies;
using CanEmAllCoop.Net;
using Net = CanEmAllCoop.Net.Net;

/// <summary>
/// Host side of enemy replication: assigns net ids when the truck spawns enemies, batches spawn / release / return /
/// damaged / death events and streams the transforms of awake or moving bodies.
/// </summary>
internal static class HostEnemies
{
    sealed class Tracked
    {
        public Enemy E;
        public Transform Tr;   // cached: Component.transform allocates a new interop proxy on every call (read for every enemy every tick)
        public uint Id;
        public SentState S;
        public bool DeathSent;
    }

    const int SpawnChunk = 200;
    const int PosesPerPacket = 70;      // ~14 bytes each -> ~1 KB, below a typical MTU
    const int PosesPerTick = 360;       // bandwidth valve: 360 x 14 B x 12 Hz = ~60 KB/s worst case
    const float PosEps = 0.02f;         // 2 cm
    const float RotEpsDeg = 1.5f;
    const float KeyframeSeconds = 3f;

    static readonly Dictionary<uint, Tracked> ById = new();
    static readonly List<Tracked> Ring = new();
    static bool _ringDirty;
    static readonly RoundRobin Rr = new();
    static readonly List<int> Picked = new();
    static readonly List<PoseEntry> Out = new();

    static readonly List<uint> PendingReturn = new();
    static readonly List<uint> PendingDamaged = new();
    static readonly List<uint> PendingRelease = new();
    static Vector3 _relDir;
    static float _relSpeed, _relRoam;

    static int _bulk;
    static float _nextTransforms;

    public static int Tracking => ById.Count;
    public static long PosesSent, SpawnsSent, DeathsSent, ReturnsSent;

    // ---- lifecycle --------------------------------------------------------------------------------------------

    public static void Reset()
    {
        ById.Clear();
        Ring.Clear();
        _ringDirty = false;
        PendingReturn.Clear();
        PendingDamaged.Clear();
        PendingRelease.Clear();
        _bulk = 0;
        _nextTransforms = 0f;
        Rr.Reset();
    }

    static bool HasClients => Net.IsHost && Net.Peers.Count > 1;

    // ---- spawn ------------------------------------------------------------------------------------------------

    /// <summary>Postfix of Truck.SpawnEnemiesInsideTruck (host).</summary>
    public static void OnSpawned(Truck truck)
    {
        var list = truck.spawnedEnemies;
        if (list == null) return;
        var entries = new List<SpawnEntry>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            var e = list[i];
            if (e == null) continue;
            var t = Track(e);
            if (t == null) continue;
            entries.Add(MakeSpawn(t, truck));
        }
        SendSpawns(entries, null);
    }

    static Tracked Track(Enemy e)
    {
        uint id = NetIds.Assign(e);
        if (id == 0) return null;
        if (!ById.TryGetValue(id, out var t))
        {
            t = new Tracked { E = e, Id = id };
            ById[id] = t;
            _ringDirty = true;
        }
        return t;
    }

    static SpawnEntry MakeSpawn(Tracked t, Truck truck)
    {
        var e = t.E;
        var tr = e.transform;
        var p = tr.position;
        var q = tr.rotation;
        float max = Mathf.Max(e.maxHealth, 1f);
        return new SpawnEntry
        {
            Id = t.Id,
            Prefab = PrefabIndex(truck, e),
            Shiny = e.IsShiny,
            Dead = e.IsDead,
            State = (byte)(int)e.state,
            HealthPct = (byte)Mathf.Clamp(Mathf.RoundToInt(e.health / max * 100f), 0, 100),
            X = p.x, Y = p.y, Z = p.z,
            Rot = new Quat4(q.x, q.y, q.z, q.w),
        };
    }

    static ushort PrefabIndex(Truck truck, Enemy e)
    {
        try
        {
            var prefabs = truck.allEnemyPrefabs;
            Enemy prefab = null;
            var map = truck.enemyPrefabByInstance;
            if (map != null) map.TryGetValue(e, out prefab);
            if (prefab == null || prefabs == null) return ushort.MaxValue;
            var pp = prefab.Pointer;
            for (int i = 0; i < prefabs.Count; i++)
            {
                var c = prefabs[i];
                if (c != null && c.Pointer == pp) return (ushort)i;
            }
        }
        catch (Exception ex) { Log.Error("Enemies: prefab index lookup failed", ex); }
        return ushort.MaxValue;
    }

    static void SendSpawns(List<SpawnEntry> entries, Peer only)
    {
        for (int start = 0; start < entries.Count; start += SpawnChunk)
        {
            int n = Math.Min(SpawnChunk, entries.Count - start);
            int s0 = start;
            Action<NetWriter> write = w => PoseCodec.WriteSpawnBatch(w, entries, s0, n);
            if (only != null) Net.SendTo(only, EnemyMsg.Spawn, write);
            else Net.Broadcast(EnemyMsg.Spawn, write);
            SpawnsSent += n;
        }
    }

    /// <summary>Late join: every live enemy (host only).</summary>
    public static void SendAllTo(Peer peer)
    {
        try
        {
            AdoptUntracked();
            var truck = Truck.Instance;
            if (truck == null) return;
            var entries = new List<SpawnEntry>(ById.Count);
            foreach (var t in ById.Values)
                if (t.E != null) entries.Add(MakeSpawn(t, truck));
            if (entries.Count > 0) SendSpawns(entries, peer);
        }
        catch (Exception e) { Log.Error("Enemies: late-join enemy snapshot failed", e); }
    }

    // Enemies registered with the game but never seen by our spawn hook (session started mid-day).
    static void AdoptUntracked()
    {
        var gm = GameManager.Instance;
        var list = gm?.enemies;
        if (list == null) return;
        for (int i = 0; i < list.Count; i++)
        {
            var e = list[i];
            if (e == null || !e.gameObject.activeInHierarchy) continue;
            Track(e);
        }
    }

    // ---- release / damaged / return ---------------------------------------------------------------------------

    public static void OnReleased(Enemy e, Vector3 dir, float speed, float roam)
    {
        uint id = NetIds.IdOf(e);
        if (id == 0) return;
        if (PendingRelease.Count > 0 && (dir != _relDir || speed != _relSpeed || roam != _relRoam)) FlushRelease();
        _relDir = dir; _relSpeed = speed; _relRoam = roam;
        PendingRelease.Add(id);
    }

    public static void OnDamagedDelivery(Enemy e)
    {
        uint id = NetIds.IdOf(e);
        if (id != 0) PendingDamaged.Add(id);
    }

    /// <summary>Postfix of Truck.ReturnEnemy (host). Releases the id so a pooled instance gets a fresh one later.</summary>
    public static void OnReturned(Enemy e)
    {
        uint id = NetIds.IdOf(e);
        if (id == 0) return;
        if (ById.Remove(id)) _ringDirty = true;
        NetIds.Release(id);
        if (_bulk == 0) PendingReturn.Add(id);
    }

    public static void BeginBulk() => _bulk++;

    public static void EndBulk()
    {
        if (_bulk > 0) _bulk--;
        if (_bulk != 0) return;
        FlushPending();
        var remaining = new List<uint>(ById.Keys);
        Net.Broadcast(EnemyMsg.CleanAll, w => PoseCodec.WriteIds(w, remaining, 0, remaining.Count));
    }

    static void FlushRelease()
    {
        if (PendingRelease.Count == 0) return;
        var ids = new List<uint>(PendingRelease);
        PendingRelease.Clear();
        float dx = _relDir.x, dy = _relDir.y, dz = _relDir.z, sp = _relSpeed, ro = _relRoam;
        Net.Broadcast(EnemyMsg.Release, w => PoseCodec.WriteRelease(w, dx, dy, dz, sp, ro, ids));
    }

    static void FlushIds(List<uint> pending, ushort msg)
    {
        if (pending.Count == 0) return;
        var ids = new List<uint>(pending);
        pending.Clear();
        for (int start = 0; start < ids.Count; start += 1000)
        {
            int s0 = start, n = Math.Min(1000, ids.Count - start);
            Net.Broadcast(msg, w => PoseCodec.WriteIds(w, ids, s0, n));
        }
    }

    static void FlushPending()
    {
        FlushRelease();
        FlushIds(PendingDamaged, EnemyMsg.Damaged);
        if (PendingReturn.Count > 0) ReturnsSent += PendingReturn.Count;
        FlushIds(PendingReturn, EnemyMsg.Return);
    }

    // ---- death ------------------------------------------------------------------------------------------------

    /// <summary>Postfix of GameManager.OnEnemyDeath (host).</summary>
    public static void OnDeath(Enemy e)
    {
        uint id = NetIds.IdOf(e);
        if (id != 0 && ById.TryGetValue(id, out var t)) SendDeath(t);
    }

    static void SendDeath(Tracked t)
    {
        if (t.DeathSent) return;
        t.DeathSent = true;
        var e = t.E;
        var tr = e.transform;
        var p = tr.position;
        var q = tr.rotation;
        Vector3 v = Vector3.zero, av = Vector3.zero;
        try
        {
            var rb = e.rb;
            if (rb != null) { v = rb.linearVelocity; av = rb.angularVelocity; }
        }
        catch (Exception) { /* velocity is only an extrapolation hint */ }
        var d = new DeathEntry
        {
            Id = t.Id, X = p.x, Y = p.y, Z = p.z, Rot = new Quat4(q.x, q.y, q.z, q.w),
            Vx = v.x, Vy = v.y, Vz = v.z, Wx = av.x, Wy = av.y, Wz = av.z,
        };
        Net.Broadcast(EnemyMsg.Death, w => PoseCodec.WriteDeath(w, in d));
        DeathsSent++;
    }

    // ---- per-frame --------------------------------------------------------------------------------------------

    public static void Tick(float now)
    {
        if (!HasClients) { PendingReturn.Clear(); PendingDamaged.Clear(); PendingRelease.Clear(); return; }
        FlushPending();
        if (now < _nextTransforms) return;
        _nextTransforms = now + 1f / Mathf.Max(1, CoopConfig.EnemyRateHz);
        SendTransforms(now);
    }

    static void RebuildRing()
    {
        _ringDirty = false;
        Ring.Clear();
        List<uint> dead = null;
        foreach (var t in ById.Values)
        {
            if (t.E == null) { (dead ??= new List<uint>()).Add(t.Id); continue; }
            Ring.Add(t);
        }
        if (dead != null)
            foreach (var id in dead) { ById.Remove(id); NetIds.Release(id); }
    }

    static void SendTransforms(float now)
    {
        if (_ringDirty) RebuildRing();
        if (Ring.Count == 0) return;

        Out.Clear();
        Picked.Clear();
        Rr.Pick(Ring.Count, PosesPerTick, i => Wants(Ring[i], now), Picked);
        PosesSent += Out.Count;

        for (int start = 0; start < Out.Count; start += PosesPerPacket)
        {
            int s0 = start, n = Math.Min(PosesPerPacket, Out.Count - start);
            Net.Broadcast(EnemyMsg.Transforms, w => PoseCodec.WritePoseBatch(w, Out, s0, n), reliable: false);
        }
    }

    static bool Wants(Tracked t, float now)
    {
        var e = t.E;
        if (e == null) { _ringDirty = true; return false; }
        bool dead = e.IsDead;
        if (dead && !t.DeathSent) SendDeath(t);   // deaths that bypassed GameManager.OnEnemyDeath
        if (!dead && (int)e.state == 0) return false;   // still waiting in the truck: spawn pose is final
        if (CanEmAllCoop.Cans.GrabSync.IsHeldByRemote(t.Id)) return false;   // the holder streams it (cans module HeldXform)

        var tr = t.Tr ??= e.transform;
        var p = tr.position;
        var qq = tr.rotation;
        var q = new Quat4(qq.x, qq.y, qq.z, qq.w);
        if (!SentState.NeedsSend(in t.S, p.x, p.y, p.z, q, dead, now, PosEps, RotEpsDeg, KeyframeSeconds)) return false;

        t.S.MarkSent(p.x, p.y, p.z, q, dead, now);
        Out.Add(new PoseEntry { Id = t.Id, X = p.x, Y = p.y, Z = p.z, Rot = q, Dead = dead });
        return true;
    }

    public static string Debug() =>
        $"hostTracked={ById.Count} posesSent={PosesSent} spawnsSent={SpawnsSent} deathsSent={DeathsSent} returnsSent={ReturnsSent}";
}
