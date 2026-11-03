using System;
using System.Collections.Generic;
using System.Diagnostics;
using CanEmAllCoop.Core;
using UnityEngine;

namespace CanEmAllCoop.Enemies;
using CanEmAllCoop.Net;
using Net = CanEmAllCoop.Net.Net;

/// <summary>
/// Client side of enemy replication. A puppet is a real <see cref="Enemy"/> taken from the game's own pool
/// (Truck.GetEnemy), kept kinematic, with its AI/physics methods patched off (see EnemyPatches) and its transform
/// driven by interpolated host snapshots.
/// </summary>
internal static class ClientPuppets
{
    public sealed class Puppet
    {
        public Enemy E;
        public uint Id;
        public IntPtr Ptr;
        public Transform Tr;                 // cached: Component.transform builds a new interop proxy on every call
        public Rigidbody Rb;
        public Transform Vis;                // Enemy.visual: the transform Enemy.Update turns towards the walking direction
        public float SimUntil;               // the local player let go of the body: real physics until then (unscaled time)
        public bool HasApplied;              // AX..ARot are what was last written to the transform
        public float AX, AY, AZ;
        public Quat4 ARot;
        public readonly PoseBuffer Buf = new();
        public RigidbodyInterpolation SavedInterp;
        public bool Dead;
        public bool Applied;
        public float Speed;
    }

    /// <summary>Render delay behind the newest snapshot: ~1.5 snapshot intervals.</summary>
    static float InterpDelay => 1.5f / Mathf.Max(1, CoopConfig.EnemyRateHz);

    // Enemy.ExitTruck gives the capsule its walking shape once an enemy is out of the truck (PrepareInsideTruck shrinks it). That
    // method is part of the patched-off FixedUpdate, so the release handler has to do it. Constants read from ExitTruck.
    const float WalkRadius = 0.3f, WalkCenterY = 0.6f, WalkHeight = 1.2f;
    const float ThrowSimSeconds = 0.4f;

    static readonly Dictionary<uint, Puppet> ById = new();
    static readonly Dictionary<IntPtr, Puppet> ByPtr = new();
    static readonly List<uint> Scratch = new();
    static int _animFailures;

    // A truck delivery creates up to several hundred puppets. Doing them all in the frame the Spawn message arrives hitches
    // the client, so creation is spread over frames (time budget below) and every other message that touches puppets queues
    // behind the pending spawns so the host's order is kept.
    static readonly WorkQueue Work = new();
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    const double FrameBudgetMs = 6.0;
    const int MinSpawnsPerFrame = 8;
    static long _budgetEnd;
    static int _doneThisFrame;
    public static int PendingWork => Work.Count;

    static bool OutOfBudget() => Clock.ElapsedTicks >= _budgetEnd && _doneThisFrame >= MinSpawnsPerFrame;

    /// <summary>Applies at once when nothing is queued ahead of it, otherwise after the queued spawns.</summary>
    static void Ordered(Action apply)
    {
        if (!Work.HasWork) { apply(); return; }
        Work.Enqueue(_ => { apply(); return true; });
    }

    static void PumpWork()
    {
        if (!Work.HasWork) return;
        _budgetEnd = Clock.ElapsedTicks + (long)(FrameBudgetMs * Stopwatch.Frequency / 1000.0);
        _doneThisFrame = 0;
        Work.Pump(OutOfBudget);
    }

    /// <summary>
    /// Net ids whose transform is driven by someone else (e.g. a dead body the local player holds - the cans module's
    /// grab protocol). Snapshots for these are ignored until the id is removed again.
    /// </summary>
    public static readonly HashSet<uint> ExternallyControlled = new();

    public static long PosesApplied, SpawnsApplied, DeathsApplied, Failures;
    public static int Count => ById.Count;
    public static IEnumerable<Puppet> All => ById.Values;

    public static bool IsPuppet(Enemy e)
    {
        if (ByPtr.Count == 0 || e == null) return false;
        try { return ByPtr.ContainsKey(e.Pointer); }
        catch (Exception) { return false; }
    }

    public static bool TryGet(uint id, out Puppet p) => ById.TryGetValue(id, out p);

    /// <summary>Pose of a body another player holds (cans module HeldXform): goes through the same interpolation buffer.</summary>
    public static void OnRemoteHeldPose(uint id, Vector3 pos, Quaternion rot)
    {
        if (!Net.IsClient || !ById.TryGetValue(id, out var p)) return;
        p.Buf.Add(Time.unscaledTime, pos.x, pos.y, pos.z, new Quat4(rot.x, rot.y, rot.z, rot.w));
        p.Applied = false;
    }

    public static bool TryGet(Enemy e, out Puppet p)
    {
        p = null;
        if (ByPtr.Count == 0 || e == null) return false;
        try { return ByPtr.TryGetValue(e.Pointer, out p); }
        catch (Exception) { return false; }
    }

    // ---- spawn ------------------------------------------------------------------------------------------------

    public static void HandleSpawn(NetReader r)
    {
        var list = new List<SpawnEntry>();
        PoseCodec.ReadSpawnBatch(r, list);
        if (list.Count == 0) return;
        Work.Enqueue(WorkQueue.ForEachItem(list.Count, i =>
        {
            if (Truck.Instance == null) return false;      // the game scene is not up yet: wait, do not lose the entry
            _doneThisFrame++;
            var s = list[i];
            try { Create(s, Time.unscaledTime); }
            catch (Exception e)
            {
                Failures++;
                if (Failures <= 5) Log.Error($"Enemies: puppet {s.Id} could not be created", e);
            }
            return true;
        }));
        PumpWork();                                         // a small delivery (and the first slice of a big one) lands this frame
    }

    static void Create(in SpawnEntry s, float now)
    {
        if (ById.TryGetValue(s.Id, out var existing))
        {
            // Duplicate (late-join snapshot racing a live spawn): just refresh the pose.
            existing.Buf.Add(now, s.X, s.Y, s.Z, s.Rot);
            return;
        }

        var truck = Truck.Instance;
        if (truck == null) throw new InvalidOperationException("no Truck.Instance");
        var prefabs = truck.allEnemyPrefabs;
        if (prefabs == null || s.Prefab >= prefabs.Count) throw new InvalidOperationException($"unknown prefab index {s.Prefab}");
        var prefab = prefabs[s.Prefab];

        var pos = new Vector3(s.X, s.Y, s.Z);
        var rot = new Quaternion(s.Rot.X, s.Rot.Y, s.Rot.Z, s.Rot.W);
        var e = truck.GetEnemy(prefab, pos, rot);
        if (e == null) throw new InvalidOperationException("Truck.GetEnemy returned null");
        // GetEnemy hands out a deactivated instance; vanilla's SpawnEnemiesInsideTruck activates it, which never runs on a client
        if (!e.gameObject.activeSelf) e.gameObject.SetActive(true);

        // GetEnemy registers with the GameManager itself (the truck's spawn loop relies on that); be defensive.
        var gm = GameManager.Instance;
        if (gm != null && gm.registeredEnemies != null && !gm.registeredEnemies.Contains(e)) gm.RegisterEnemy(e);

        e.PrepareInsideTruck();
        e.IsShiny = s.Shiny;

        var rb = e.rb;
        var p = new Puppet { E = e, Id = s.Id, Ptr = e.Pointer, Tr = e.transform, Rb = rb, Vis = e.visual };
        if (rb != null) p.SavedInterp = rb.interpolation;

        if (s.State != 0)
        {
            // Late join: walk the same code path a live release would take.
            if (rb != null) rb.isKinematic = false;
            e.ReleaseFromTruck(Vector3.forward, truck.exitSpeed, truck.roamRadius);
            e.state = Enemy.State.Roaming;
            RestoreWalkCollider(e);
        }

        ById[s.Id] = p;
        ByPtr[p.Ptr] = p;
        NetIds.Bind(s.Id, e);
        MakeKinematic(p);

        e.transform.SetPositionAndRotation(pos, rot);
        if (!s.Dead && p.Vis != null) p.Vis.rotation = rot;   // the host sends the facing of a live enemy (see HostEnemies.Wants)
        p.Buf.Add(now, s.X, s.Y, s.Z, s.Rot);

        if (s.HealthPct < 100 && !s.Dead)
        {
            e.ApplyDamagedDelivery();
            e.health = e.maxHealth * (s.HealthPct / 100f);
        }
        if (s.Dead) MarkDead(p, Vector3.zero, Vector3.zero);
        SpawnsApplied++;
    }

    static int _colliderLogs;

    static void RestoreWalkCollider(Enemy e)
    {
        try
        {
            var cap = e.mainCollider?.TryCast<CapsuleCollider>();
            if (cap == null) return;
            if (_colliderLogs < 2)
            {
                _colliderLogs++;
                Log.Info($"Enemies: puppet capsule restored: radius {cap.radius:0.00} -> {WalkRadius:0.00}, height {cap.height:0.00} -> {WalkHeight:0.00}, center y {cap.center.y:0.00} -> {WalkCenterY:0.00}");
            }
            cap.radius = WalkRadius;
            cap.center = new Vector3(0f, WalkCenterY, 0f);
            cap.height = WalkHeight;
        }
        catch (Exception ex)
        {
            Failures++;
            if (Failures <= 5) Log.Error("Enemies: puppet collider restore failed", ex);
        }
    }

    static void MakeKinematic(Puppet p)
    {
        var rb = p.E.rb;
        if (rb == null) return;
        rb.interpolation = RigidbodyInterpolation.None;
        if (!rb.isKinematic) rb.isKinematic = true;
    }

    // ---- release / damaged ------------------------------------------------------------------------------------

    public static void HandleRelease(NetReader r)
    {
        var ids = new List<uint>();
        PoseCodec.ReadRelease(r, out var dx, out var dy, out var dz, out var speed, out var roam, ids);
        var dir = new Vector3(dx, dy, dz);
        Ordered(() =>
        {
            foreach (var id in ids)
            {
                if (!ById.TryGetValue(id, out var p) || p.E == null) continue;
                try
                {
                    var rb = p.E.rb;
                    if (rb != null) rb.isKinematic = false;   // ReleaseFromTruck writes velocities
                    p.E.ReleaseFromTruck(dir, speed, roam);
                    p.E.state = Enemy.State.Roaming;
                    RestoreWalkCollider(p.E);
                    MakeKinematic(p);
                    p.HasApplied = false;
                }
                catch (Exception e) { Failures++; if (Failures <= 5) Log.Error("Enemies: puppet release failed", e); }
            }
        });
    }

    public static void HandleDamaged(NetReader r)
    {
        var ids = new List<uint>();
        PoseCodec.ReadIds(r, ids);
        Ordered(() =>
        {
            foreach (var id in ids)
            {
                if (!ById.TryGetValue(id, out var p) || p.E == null) continue;
                try { p.E.ApplyDamagedDelivery(); }
                catch (Exception e) { Failures++; if (Failures <= 5) Log.Error("Enemies: puppet damaged-delivery failed", e); }
            }
        });
    }

    // ---- transforms -------------------------------------------------------------------------------------------

    public static void HandleTransforms(NetReader r)
    {
        var list = new List<PoseEntry>();
        PoseCodec.ReadPoseBatch(r, list);
        float now = Time.unscaledTime;
        foreach (var e in list)
        {
            if (!ById.TryGetValue(e.Id, out var p)) continue;
            p.Buf.Add(now, e.X, e.Y, e.Z, e.Rot);
            p.Applied = false;
        }
    }

    /// <summary>Every frame: place puppets at their interpolated snapshot pose and drive the walk animation.</summary>
    public static void Apply(float now, float dt)
    {
        PumpWork();
        if (ById.Count == 0) return;
        float renderTime = now - InterpDelay;
        Scratch.Clear();

        foreach (var p in ById.Values)
        {
            var e = p.E;
            if (e == null) { Scratch.Add(p.Id); continue; }
            if (ExternallyControlled.Contains(p.Id) || CanEmAllCoop.Cans.GrabSync.IsHeldByLocal(p.Id)) { p.HasApplied = false; continue; }
            if (p.SimUntil != 0f)
            {
                if (now < p.SimUntil) { p.HasApplied = false; continue; }   // thrown / dropped by the local player: let the body fly
                EndSim(p, now);
            }
            if (!p.Buf.TrySample(renderTime, out var s)) continue;

            // Resting puppet (same pose as last frame, no velocity, walk cycle already settled): no engine call at all.
            // The old test (Buf.Count > 1) stayed true forever once two samples had arrived, so every puppet was re-posed
            // and re-animated on every frame even when it stood still.
            if (p.HasApplied && p.Speed < 0.005f && s.IsRestAt(p.AX, p.AY, p.AZ, p.ARot))
            {
                p.Applied = true;
                continue;
            }

            var tr = p.Tr;
            if (tr == null) tr = p.Tr = e.transform;
            var pos = new Vector3(s.X, s.Y, s.Z);
            var rot = new Quaternion(s.Rot.X, s.Rot.Y, s.Rot.Z, s.Rot.W);
            // A live enemy faces its walking direction through Enemy.visual (the root keeps its spawn rotation) and the host
            // streams that rotation for live enemies; only a dead body is oriented through its root.
            var vis = p.Dead ? null : p.Vis;
            if (vis != null) { tr.position = pos; vis.rotation = rot; }
            else tr.SetPositionAndRotation(pos, rot);
            p.AX = s.X; p.AY = s.Y; p.AZ = s.Z; p.ARot = s.Rot; p.HasApplied = true;
            p.Applied = true;
            PosesApplied++;

            var rb = p.Rb;
            if (rb != null && !rb.isKinematic) rb.isKinematic = true;   // a grab/drop in the cans module may have restored physics

            if (!p.Dead) Animate(p, s, dt);
        }

        foreach (var id in Scratch) Remove(id, returnToPool: false);
    }

    // The walk cycle is a vertex-animation shader fed by Enemy.UpdateAnimationBlend (from currentMoveSpeed), which the
    // patched-off Update would normally call.
    static void Animate(Puppet p, in PoseSample s, float dt)
    {
        if (_animFailures >= 5) return;
        try
        {
            float target = Mathf.Sqrt(s.Vx * s.Vx + s.Vz * s.Vz);
            p.Speed = Mathf.Lerp(p.Speed, target, 1f - Mathf.Exp(-10f * dt));
            var e = p.E;
            e.currentMoveSpeed = p.Speed;
            e.UpdateAnimationBlend(dt);
        }
        catch (Exception ex)
        {
            _animFailures++;
            Log.Error($"Enemies: puppet animation update failed ({_animFailures}/5)", ex);
        }
    }

    // ---- local grab (cans module) -----------------------------------------------------------------------------

    /// <summary>
    /// The local player picked a dead body up. The game moves a held body by writing Rigidbody.linearVelocity, which a kinematic
    /// body ignores (the hold would then drop by itself as soon as the player walks away), so the puppet gets real physics back
    /// for as long as it is held. Apply() leaves it alone meanwhile (GrabSync.IsHeldByLocal).
    /// </summary>
    public static void OnLocalGrab(uint id)
    {
        if (!ById.TryGetValue(id, out var p) || p.E == null) return;
        p.SimUntil = 0f;
        var rb = p.Rb;
        if (rb != null) rb.isKinematic = false;
    }

    /// <summary>
    /// The local player dropped / threw the body (or the host refused the grab). The host takes the physics over from the Release
    /// message; until its stream catches up the body keeps simulating here, otherwise it would snap back to the pose it had
    /// before the grab.
    /// </summary>
    public static void OnLocalRelease(uint id, Vector3 velocity, bool simulate)
    {
        if (!ById.TryGetValue(id, out var p) || p.E == null) return;
        var rb = p.Rb;
        if (simulate && rb != null)
        {
            rb.isKinematic = false;
            rb.linearVelocity = velocity;
            p.SimUntil = Time.unscaledTime + ThrowSimSeconds;
            return;
        }
        EndSim(p, Time.unscaledTime);
    }

    // Back to the host stream from wherever the local simulation left the body (no jump: the buffer restarts at this pose).
    static void EndSim(Puppet p, float now)
    {
        p.SimUntil = 0f;
        p.HasApplied = false;
        var tr = p.Tr;
        if (tr == null) tr = p.Tr = p.E.transform;
        var pos = tr.position;
        var q = tr.rotation;
        p.Buf.Clear();
        p.Buf.Add(now, pos.x, pos.y, pos.z, new Quat4(q.x, q.y, q.z, q.w));
        var rb = p.Rb;
        if (rb != null)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.isKinematic = true;
        }
    }

    // ---- death ------------------------------------------------------------------------------------------------

    public static void HandleDeath(NetReader r)
    {
        var d = PoseCodec.ReadDeath(r);
        Ordered(() => ApplyDeath(d));
    }

    static void ApplyDeath(DeathEntry d)
    {
        if (!ById.TryGetValue(d.Id, out var p) || p.E == null || p.Dead) return;
        try
        {
            MarkDead(p, new Vector3(d.Vx, d.Vy, d.Vz), new Vector3(d.Wx, d.Wy, d.Wz));
            var pos = new Vector3(d.X, d.Y, d.Z);
            p.E.transform.SetPositionAndRotation(pos, new Quaternion(d.Rot.X, d.Rot.Y, d.Rot.Z, d.Rot.W));
            p.Buf.Clear();
            p.Buf.Add(Time.unscaledTime, d.X, d.Y, d.Z, d.Rot);
            p.Applied = true;
            p.HasApplied = false;
            DeathsApplied++;
        }
        catch (Exception e) { Failures++; if (Failures <= 5) Log.Error("Enemies: puppet death failed", e); }
    }

    static void MarkDead(Puppet p, Vector3 velocity, Vector3 angular)
    {
        var e = p.E;
        p.Dead = true;
        e.health = 0f;
        e.IsDead = true;
        var rb = e.rb;
        if (rb != null)
        {
            rb.isKinematic = false;           // EnableRagdoll writes velocities / constraints
            rb.linearVelocity = velocity;
            rb.angularVelocity = angular;
        }
        e.EnableRagdoll();
        MakeKinematic(p);                      // from here on the host stream moves the body
    }

    // ---- return / clean ---------------------------------------------------------------------------------------

    public static void HandleReturn(NetReader r)
    {
        var ids = new List<uint>();
        PoseCodec.ReadIds(r, ids);
        Ordered(() => { foreach (var id in ids) Remove(id, returnToPool: true); });
    }

    public static void HandleCleanAll(NetReader r)
    {
        var keep = new List<uint>();
        PoseCodec.ReadIds(r, keep);
        Ordered(() =>
        {
            var keepSet = new HashSet<uint>(keep);
            Scratch.Clear();
            foreach (var id in ById.Keys) if (!keepSet.Contains(id)) Scratch.Add(id);
            var gone = new List<uint>(Scratch);
            foreach (var id in gone) Remove(id, returnToPool: true);
        });
    }

    /// <summary>Postfix of Truck.ReturnEnemy on a client: the game itself pooled a puppet (CleanAllScene, StopTruck).</summary>
    public static void OnLocalReturn(Enemy e)
    {
        if (TryGet(e, out var p)) Remove(p.Id, returnToPool: false);
    }

    static void Remove(uint id, bool returnToPool)
    {
        if (!ById.TryGetValue(id, out var p)) return;
        ById.Remove(id);
        ByPtr.Remove(p.Ptr);
        ExternallyControlled.Remove(id);
        NetIds.Release(id);

        var e = p.E;
        if (e == null) return;
        try
        {
            var rb = e.rb;
            if (rb != null) rb.interpolation = p.SavedInterp;
            if (returnToPool)
            {
                var truck = Truck.Instance;
                if (truck != null) truck.ReturnEnemy(e);
            }
        }
        catch (Exception ex) { Failures++; if (Failures <= 5) Log.Error("Enemies: puppet return failed", ex); }
    }

    /// <summary>Session start/end: hand every puppet back to the pool so the world is vanilla again.</summary>
    public static void ReturnAll()
    {
        Work.Clear();
        var ids = new List<uint>(ById.Keys);
        foreach (var id in ids) Remove(id, returnToPool: true);
        ById.Clear();
        ByPtr.Clear();
        ExternallyControlled.Clear();
        _animFailures = 0;
    }

    public static string Debug() =>
        $"puppets={ById.Count} queued={Work.Count} posesApplied={PosesApplied} spawnsApplied={SpawnsApplied} deathsApplied={DeathsApplied} failures={Failures}";
}
