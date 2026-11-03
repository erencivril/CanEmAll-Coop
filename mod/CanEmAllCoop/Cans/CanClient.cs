using System;
using System.Collections.Generic;
using CanEmAllCoop.Core;
using UnityEngine;

namespace CanEmAllCoop.Cans;
using CanEmAllCoop.Net;
using Net = CanEmAllCoop.Net.Net;

/// <summary>
/// Client side of the money pipe: cans and crates are kinematic puppets of the host's objects. Puppets are made
/// through the game's own can pool, positions are smoothed towards the last received transform, and the local
/// player can still grab / vacuum them (the ownership protocol in GrabSync takes over while that happens).
/// </summary>
internal static class CanClient
{
    sealed class Puppet
    {
        public uint Id;
        public Component Obj;
        public Can Can;          // null for crates
        public Transform T;
        public Rigidbody Rb;
        public Vector3 Target, Vel;
        public Quaternion TargetRot = Quaternion.identity;
        public float LastRecv = -100f;
        public bool HasTarget;
        public bool Held;        // the local player (hand or vacuum) owns it right now
        public float SimUntil;   // local physics takes over until then (suck assist, release grace)
        public bool WasKinematic; // crates: restore on session end
        public bool IsCrate => Can == null;
    }

    static readonly Dictionary<uint, Puppet> Puppets = new();
    static readonly HashSet<uint> Active = new();           // puppets that still have to converge
    static readonly HashSet<uint> Simulating = new();       // puppets with SimUntil in the future
    static readonly SpawnQueue Pending = new();
    static readonly Dictionary<uint, int> Retries = new();
    static readonly List<KeyValuePair<uint, float>> DueReturns = new();
    static readonly List<uint> Scratch = new();

    static uint[] _crateIds;
    static float _nextSweep;
    static float _lastRewardSound;
    static bool _loggedCreateFailure;

    public static int PuppetCount => Puppets.Count;
    public static int PendingCount => Pending.Count;

    public static void Reset()
    {
        Puppets.Clear();
        Active.Clear();
        Simulating.Clear();
        Pending.Clear();
        Retries.Clear();
        DueReturns.Clear();
        _crateIds = null;
        _nextSweep = 0f;
    }

    // ---------------------------------------------------------------- message handlers

    public static void OnSpawn(Peer from, NetReader r)
    {
        var s = new SpawnInfo
        {
            Id = r.ReadUInt(),
            Processor = r.ReadSByte(),
            Price = r.ReadInt(),
        };
        byte flags = r.ReadByte();
        s.Shiny = (flags & 1) != 0;
        s.Smoked = (flags & 2) != 0;
        s.TextureIndex = r.ReadShort();
        s.Quantity = r.ReadUShort();
        s.Px = r.ReadFloat(); s.Py = r.ReadFloat(); s.Pz = r.ReadFloat();
        s.Rot = r.ReadUInt();

        if (Puppets.TryGetValue(s.Id, out var existing))
        {
            // Duplicate announcement (e.g. late-join snapshot overlapping live traffic): just refresh.
            if (existing.Can != null) existing.Can.quantity = s.Quantity;
            SetTarget(existing, new Vector3(s.Px, s.Py, s.Pz), Items.UnpackRot(s.Rot), Time.realtimeSinceStartup);
            return;
        }
        Pending.Add(s);
    }

    public static void OnMerge(Peer from, NetReader r)
    {
        uint id = r.ReadUInt();
        int qty = r.ReadUShort();
        if (Puppets.TryGetValue(id, out var p)) { if (p.Can != null) p.Can.quantity = qty; }
        else Pending.SetQuantity(id, qty);
    }

    public static void OnPaid(Peer from, NetReader r)
    {
        uint id = r.ReadUInt();
        if (Pending.Remove(id)) return;
        if (!Puppets.TryGetValue(id, out var p)) return;

        GrabSync.NotifyObjectConsumed(id);
        PlayRewardSound();
        if (p.Can != null) p.Can.IsProcessed = true;
        float delay = 0.5f;
        try { var rm = RewardMachine.Instance; if (rm != null) delay = Mathf.Clamp(rm.respawnDelay, 0f, 5f); }
        catch (Exception) { }
        DueReturns.Add(new KeyValuePair<uint, float>(id, Time.realtimeSinceStartup + delay));
    }

    public static void OnGone(Peer from, NetReader r)
    {
        uint id = r.ReadUInt();
        if (Pending.Remove(id)) return;
        if (Puppets.ContainsKey(id))
        {
            GrabSync.NotifyObjectConsumed(id);
            ReturnPuppet(id);
        }
    }

    public static void OnTransforms(Peer from, NetReader r)
    {
        int count = r.ReadUShort();
        float now = Time.realtimeSinceStartup;
        for (int i = 0; i < count; i++)
        {
            uint id = r.ReadUInt();
            Vector3 p = r.ReadVec3Q();
            Quaternion q = Items.UnpackRot(r.ReadUInt());
            ApplyTransform(id, p, q, now);
        }
    }

    public static void OnCrateIds(Peer from, NetReader r)
    {
        int n = r.ReadByte();
        var ids = new uint[n];
        for (int i = 0; i < n; i++) ids[i] = r.ReadUInt();
        _crateIds = ids;
        TryBindCrates();
    }

    /// <summary>Position update from the host stream or from a remote holder (HeldXform).</summary>
    public static void ApplyTransform(uint id, Vector3 pos, Quaternion rot, float now)
    {
        if (Puppets.TryGetValue(id, out var p))
        {
            if (p.Held) return;
            SetTarget(p, pos, rot, now);
        }
        else if (Pending.Contains(id))
        {
            Pending.SetPose(id, pos.x, pos.y, pos.z, Items.PackRot(rot));
        }
    }

    static void SetTarget(Puppet p, Vector3 pos, Quaternion rot, float now)
    {
        if (p.HasTarget)
        {
            float dt = now - p.LastRecv;
            if (dt > 0.02f && dt < 0.5f)
            {
                Vector3 v = (pos - p.Target) / dt;
                if (v.sqrMagnitude > 25f * 25f) v = Vector3.zero;
                p.Vel = Vector3.Lerp(p.Vel, v, 0.5f);
            }
            else p.Vel = Vector3.zero;
        }
        p.Target = pos;
        p.TargetRot = rot;
        p.LastRecv = now;
        p.HasTarget = true;
        if (p.SimUntil <= now) Active.Add(p.Id);
    }

    /// <summary>Puts the puppet at the pose immediately (an object that was inactive / just released).</summary>
    public static void Snap(uint id, Vector3 pos, Quaternion rot)
    {
        if (!Puppets.TryGetValue(id, out var p)) return;
        p.Target = pos; p.TargetRot = rot; p.Vel = Vector3.zero; p.HasTarget = true;
        p.LastRecv = Time.realtimeSinceStartup;
        if (p.T != null) p.T.SetPositionAndRotation(pos, rot);
    }

    // ---------------------------------------------------------------- per frame

    public static void Update(float now, float dt)
    {
        var gm = GameManager.Instance;
        if (gm != null)
        {
            Guard.Run("client spawn", () => ProcessPending(gm, now));
            if (_crateIds != null) Guard.Run("client crates", TryBindCrates);
        }
        Guard.Run("client smoothing", () => Smooth(now, dt));
        if (DueReturns.Count > 0) Guard.Run("client returns", () => ProcessReturns(now));
        if (now >= _nextSweep)
        {
            _nextSweep = now + 1f;
            Guard.Run("client sweep", Sweep);
        }
    }

    const int SpawnsPerFrame = 40;

    static void ProcessPending(GameManager gm, float now)
    {
        if (Pending.Count == 0) return;
        if (gm.canProcessors == null) return;
        for (int n = 0; n < SpawnsPerFrame && Pending.TryDequeue(out var s); n++)
        {
            bool ok = false;
            try { ok = CreatePuppet(gm, s, now); }
            catch (Exception e)
            {
                if (!_loggedCreateFailure) { _loggedCreateFailure = true; Log.Error("Cans: creating a can puppet failed", e); }
            }
            if (ok) { Retries.Remove(s.Id); continue; }
            Retries.TryGetValue(s.Id, out var tries);
            if (tries < 5) { Retries[s.Id] = tries + 1; Pending.Add(s); }
            else Retries.Remove(s.Id);
        }
    }

    static bool CreatePuppet(GameManager gm, SpawnInfo s, float now)
    {
        var procs = gm.canProcessors;
        CanProcessor proc = null;
        Can prefab = null;
        if (s.Processor >= 0 && s.Processor < procs.Length) { proc = procs[s.Processor]; prefab = proc?.canPrefab; }
        if (prefab == null)
            for (int i = 0; i < procs.Length && prefab == null; i++) prefab = procs[i]?.canPrefab;
        if (prefab == null) prefab = gm.pooledCanPrefab;
        if (prefab == null) return false;

        var pos = new Vector3(s.Px, s.Py, s.Pz);
        var rot = Items.UnpackRot(s.Rot);
        var can = gm.GetCanFromPool(prefab, pos, rot);
        if (can == null) return false;

        can.Initialize(s.TextureIndex, s.Smoked, s.Shiny);
        can.price = s.Price;
        can.quantity = s.Quantity;
        if (proc != null) can.sourceProcessor = proc;

        var rb = can.rb;
        if (rb != null)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.isKinematic = true;
        }
        var p = new Puppet
        {
            Id = s.Id, Obj = can, Can = can, T = can.transform, Rb = rb,
            Target = pos, TargetRot = rot, HasTarget = true, LastRecv = now,
        };
        Puppets[s.Id] = p;
        NetIds.Bind(s.Id, can);
        return true;
    }

    static void TryBindCrates()
    {
        var gm = GameManager.Instance;
        var crates = gm?.crates;
        if (crates == null || _crateIds == null) return;
        int n = Math.Min(crates.Length, _crateIds.Length);
        for (int i = 0; i < n; i++)
        {
            var crate = crates[i];
            uint id = _crateIds[i];
            if (crate == null || id == 0 || Puppets.ContainsKey(id)) continue;
            var rb = crate.rb;
            var p = new Puppet
            {
                Id = id, Obj = crate, Can = null, T = crate.transform, Rb = rb,
                WasKinematic = rb != null && rb.isKinematic,
            };
            if (rb != null)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic = true;
            }
            Puppets[id] = p;
            NetIds.Bind(id, crate);
        }
    }

    static void Smooth(float now, float dt)
    {
        // Local physics windows that ended: hand the body back to the network.
        if (Simulating.Count > 0)
        {
            Scratch.Clear();
            foreach (var id in Simulating) Scratch.Add(id);
            foreach (var id in Scratch)
            {
                if (!Puppets.TryGetValue(id, out var p)) { Simulating.Remove(id); continue; }
                if (p.SimUntil > now) continue;
                Simulating.Remove(id);
                if (p.Held) continue;
                if (p.Rb != null && p.T != null) { p.Rb.linearVelocity = Vector3.zero; p.Rb.isKinematic = true; }
                Active.Add(id);
            }
        }

        if (Active.Count == 0) return;
        float a = 1f - Mathf.Exp(-18f * dt);
        Scratch.Clear();
        foreach (var id in Active) Scratch.Add(id);
        foreach (uint id in Scratch)
        {
            if (!Puppets.TryGetValue(id, out var p) || p.T == null) { Active.Remove(id); continue; }
            if (p.Held || p.SimUntil > now) { Active.Remove(id); continue; }

            Vector3 want = p.Target + p.Vel * Mathf.Min(now - p.LastRecv, 0.2f);
            Vector3 cur = p.T.position;
            Vector3 np = (want - cur).sqrMagnitude > 25f ? want : Vector3.Lerp(cur, want, a);
            Quaternion nr = Quaternion.Slerp(p.T.rotation, p.TargetRot, a);
            p.T.SetPositionAndRotation(np, nr);

            if (now - p.LastRecv > 0.6f && (want - np).sqrMagnitude < 1e-5f) Active.Remove(id);
        }
    }

    static void ProcessReturns(float now)
    {
        for (int i = DueReturns.Count - 1; i >= 0; i--)
        {
            if (DueReturns[i].Value > now) continue;
            uint id = DueReturns[i].Key;
            DueReturns.RemoveAt(i);
            ReturnPuppet(id);
        }
    }

    static void PlayRewardSound()
    {
        float t = Time.realtimeSinceStartup;
        if (t - _lastRewardSound < 0.05f) return;
        _lastRewardSound = t;
        try
        {
            var rm = RewardMachine.Instance;
            if (rm != null && rm.audioSource != null && rm.rewardClip != null) rm.audioSource.PlayOneShot(rm.rewardClip);
        }
        catch (Exception) { }
    }

    /// <summary>Back to the game's pool (cans) / start state (crates are left where they are).</summary>
    static void ReturnPuppet(uint id)
    {
        if (!Puppets.TryGetValue(id, out var p)) return;
        Puppets.Remove(id);
        Active.Remove(id);
        Simulating.Remove(id);
        GrabSync.Table.Remove(id);
        NetIds.Release(id);
        if (p.Can != null && p.Obj != null)
        {
            var gm = GameManager.Instance;
            if (gm != null && !p.Can.IsInPool)
            {
                var obj = p.Can.gameObject;
                if (obj != null && !obj.activeSelf) obj.SetActive(true); // vacuumed puppets are inactive
                gm.ReturnCanToPool(p.Can);
            }
        }
    }

    /// <summary>Puppets the game removed on its own (CleanAllScene, day change) must not keep their ids.</summary>
    static void Sweep()
    {
        Scratch.Clear();
        foreach (var kv in Puppets) Scratch.Add(kv.Key);
        foreach (uint id in Scratch)
        {
            var p = Puppets[id];
            bool dead = p.Obj == null || (p.Can != null && p.Can.IsInPool);
            if (!dead) continue;
            Puppets.Remove(id);
            Active.Remove(id);
            Simulating.Remove(id);
            GrabSync.Table.Remove(id);
            NetIds.Release(id);
        }
    }

    // ---------------------------------------------------------------- hooks used by GrabSync

    public static void BeginLocalHold(uint id)
    {
        if (!Puppets.TryGetValue(id, out var p)) return;
        p.Held = true;
        p.SimUntil = 0f;
        Simulating.Remove(id);
        Active.Remove(id);
    }

    /// <summary>The local player let go: simulate locally for a moment so a throw does not freeze, then re-sync.</summary>
    public static void EndLocalHold(uint id, Vector3 velocity, bool simulate)
    {
        if (!Puppets.TryGetValue(id, out var p)) return;
        p.Held = false;
        if (p.Rb == null || p.T == null) return;
        if (simulate)
        {
            p.SimUntil = Time.realtimeSinceStartup + 0.35f;
            Simulating.Add(id);
            p.Rb.isKinematic = false;
            p.Rb.useGravity = true;
            p.Rb.linearVelocity = velocity;
        }
        else
        {
            p.Rb.isKinematic = true;
            Active.Add(id);
        }
    }

    public static bool IsPuppet(uint id) => Puppets.ContainsKey(id);

    /// <summary>The vacuum is pulling: puppets close to it get real physics for a moment so suck forces move them.</summary>
    public static void NoteVacuumActive(Vector3 origin, float range)
    {
        float now = Time.realtimeSinceStartup;
        float r2 = range * range * 1.7f;
        foreach (var p in Puppets.Values)
        {
            if (p.Held || p.Rb == null || p.T == null || p.Obj == null) continue;
            if ((p.Target - origin).sqrMagnitude > r2 && p.SimUntil <= now) continue;
            if (p.SimUntil <= now)
            {
                p.Rb.isKinematic = false;
                p.Rb.useGravity = true;
                Active.Remove(p.Id);
                Simulating.Add(p.Id);
            }
            p.SimUntil = now + 0.3f;
        }
    }

    // ---------------------------------------------------------------- session end

    public static void Shutdown()
    {
        var gm = GameManager.Instance;
        foreach (var p in Puppets.Values)
        {
            try
            {
                if (p.Obj == null) continue;
                if (p.Can != null)
                {
                    if (gm != null && !p.Can.IsInPool)
                    {
                        var obj = p.Can.gameObject;
                        if (obj != null && !obj.activeSelf) obj.SetActive(true);
                        gm.ReturnCanToPool(p.Can);
                    }
                }
                else if (p.Rb != null) p.Rb.isKinematic = p.WasKinematic;
            }
            catch (Exception e) { Log.Error("Cans: puppet cleanup failed", e); }
        }
        Reset();
    }
}
