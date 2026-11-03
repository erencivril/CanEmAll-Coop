using System;
using System.Collections.Generic;
using CanEmAllCoop.Core;
using UnityEngine;

namespace CanEmAllCoop.Cans;
using CanEmAllCoop.Net;
using Net = CanEmAllCoop.Net.Net;

/// <summary>
/// Generic ownership protocol for IGrabbable objects (cans, crates, dead enemies), keyed by NetIds.
///
///  * The holder (a client, or the host itself) keeps simulating the object it holds. Everybody else sees a
///    kinematic puppet that follows the holder's HeldXform stream.
///  * A client asks the host with GrabReq; the host grants or denies. The client grabs optimistically (the game's own
///    grab code runs at once) and is rolled back on a denial.
///  * Drop / throw / vacuum-release hand physics back to the host (Release); the host calls OnDropped / OnThrown
///    so the HoleInOne flag and every other side effect of a throw happens on the authoritative machine.
///  * The vacuum is handled by diffing Vacuum.grabbables: objects that enter the list vanish for everybody else
///    (they are inactive on the host), objects that leave it re-appear at the thrower's pose.
///
/// Other modules: GrabSync.IsHeldByLocal / IsHeldByRemote(netId) tell a module not to apply or drive the
/// transform of an object that is currently owned by a player (see Cans/NOTES.md).
/// </summary>
public static class GrabSync
{
    internal static readonly GrabTable Table = new();

    struct HeldPose { public Vector3 P; public Quaternion Q; }

    static readonly Dictionary<uint, HeldPose> LastPose = new();
    static readonly HashSet<uint> VacSet = new();
    static readonly HashSet<uint> VacScratch = new();

    static uint _handId;
    static int _vacCount;
    static uint _throwId;
    static int _throwFrame = -100;
    static Vector3 _throwVel;
    static float _nextHeldSend;

    /// <summary>Raised on clients for HeldXform of objects that are not can/crate puppets (enemies): module smooths itself.</summary>
    public static event Action<uint, Vector3, Quaternion> RemoteHeldPose;

    public static int HoldCount => Table.Count;
    public static uint LocalHandId => _handId;

    public static bool IsHeldByLocal(uint id) => id != 0 && (id == _handId || VacSet.Contains(id));

    public static bool IsHeldByRemote(uint id)
    {
        int h = Table.HolderOf(id);
        return h != GrabTable.None && h != Items.LocalIndex;
    }

    /// <summary>true when the session that is ending (or running) was hosted by this process; set at session start.</summary>
    internal static bool WasHost;

    public static void Reset()
    {
        Table.Clear();
        LastPose.Clear();
        VacSet.Clear();
        _handId = 0;
        _vacCount = 0;
        _throwId = 0;
        _nextHeldSend = 0f;
    }

    // ---------------------------------------------------------------- patch entry points

    /// <summary>Player.GrabDeadBody prefix: false = somebody else holds it, the grab is refused locally.</summary>
    internal static bool AllowGrab(IGrabbable target)
    {
        if (!Net.IsOnline || Net.Applying || target == null) return true;
        uint id = NetIds.IdOf(target);
        if (id == 0) return true;
        int holder = Table.HolderOf(id);
        return holder == GrabTable.None || holder == Items.LocalIndex;
    }

    internal static void BeforeThrow(Player player)
    {
        if (!Net.IsOnline) return;
        var held = player?.HeldEnemy;
        _throwId = held == null ? 0 : NetIds.IdOf(held);
        _throwFrame = Time.frameCount;
        _throwVel = Vector3.zero;
    }

    internal static void AfterThrow(Player player)
    {
        if (!Net.IsOnline || _throwId == 0) return;
        if (!Items.TryResolve(_throwId, out var comp)) return;
        var rb = Items.RigidbodyOf(comp);
        if (rb != null) _throwVel = rb.linearVelocity;
    }

    // ---------------------------------------------------------------- per frame

    public static void Update(float now)
    {
        Guard.Run("hand poll", PollHand);
        Guard.Run("vacuum poll", PollVacuum);
        if (_handId != 0 && Net.IsClient && now >= _nextHeldSend)
        {
            _nextHeldSend = now + 0.05f;
            Guard.Run("held stream", StreamHeld);
        }
    }

    static void PollHand()
    {
        var pl = Player.Instance;
        IGrabbable held = pl == null ? null : pl.HeldEnemy;
        uint id = 0;
        if (held != null)
        {
            id = NetIds.IdOf(held);
            if (id == 0 && Net.IsHost)
            {
                var comp = Items.AsComponent(held);
                if (comp != null) { CanHost.EnsureTracked(comp); id = NetIds.IdOf(held); }
            }
        }
        if (id == _handId) return;

        uint prev = _handId;
        _handId = id;
        if (prev != 0) LocalRelease(prev, GrabKind.Hand);
        if (id != 0) LocalGrab(id, GrabKind.Hand);
    }

    static void PollVacuum()
    {
        var vac = Vacuum.Instance;
        if (vac == null) return;
        var list = vac.grabbables;
        if (list == null) return;
        int n = list.Count;
        if (n == _vacCount) return;
        _vacCount = n;

        VacScratch.Clear();
        for (int i = 0; i < n; i++)
        {
            var g = list[i];
            if (g == null) continue;
            uint id = NetIds.IdOf(g);
            if (id != 0) VacScratch.Add(id);
        }

        var added = new List<uint>();
        foreach (var id in VacScratch) if (!VacSet.Contains(id)) added.Add(id);
        var removed = new List<uint>();
        foreach (var id in VacSet) if (!VacScratch.Contains(id)) removed.Add(id);

        VacSet.Clear();
        foreach (var id in VacScratch) VacSet.Add(id);

        foreach (var id in removed) LocalRelease(id, GrabKind.Vacuum);
        foreach (var id in added) LocalGrab(id, GrabKind.Vacuum);
    }

    static void StreamHeld()
    {
        uint id = _handId;
        if (!Items.TryResolve(id, out var comp)) return;
        var t = comp.transform;
        Vector3 p = t.position;
        Quaternion q = t.rotation;
        Net.Broadcast(CanMsg.HeldXform, w => { w.Write(id); Items.WritePose(w, p, q); }, reliable: false);
    }

    // ---------------------------------------------------------------- local player actions

    static void LocalGrab(uint id, GrabKind kind)
    {
        if (Net.IsHost)
        {
            if (Table.TryGrab(id, 0, kind)) BroadcastOwner(id, 0, kind, includePose: true);
        }
        else if (Net.IsClient)
        {
            Table.Force(id, Items.LocalIndex, kind);
            CanClient.BeginLocalHold(id);
            Net.SendToHost(CanMsg.GrabReq, w => { w.Write(id); w.Write((byte)kind); });
        }
    }

    static void LocalRelease(uint id, GrabKind kind)
    {
        if (!Items.TryResolve(id, out var comp))
        {
            Table.Remove(id);
            return;
        }

        var go = comp.gameObject;
        bool active = go != null && go.activeInHierarchy;
        var t = comp.transform;
        Vector3 pos = t.position;
        Quaternion rot = t.rotation;
        var rb = Items.RigidbodyOf(comp);
        Vector3 vel = rb != null ? rb.linearVelocity : Vector3.zero;

        bool thrown;
        if (kind == GrabKind.Hand)
        {
            thrown = _throwId == id && Time.frameCount - _throwFrame <= 3;
            if (thrown) vel = _throwVel;
        }
        else thrown = active;
        bool gone = kind == GrabKind.Vacuum && !active;

        if (Net.IsHost)
        {
            Table.Remove(id);
            CanHost.InvalidateTransform(id);
            BroadcastOwner(id, GrabTable.None, kind, includePose: !gone);
        }
        else if (Net.IsClient)
        {
            Table.Force(id, GrabTable.None, kind);
            CanClient.EndLocalHold(id, vel, simulate: !gone);
            Net.SendToHost(CanMsg.Release, w =>
            {
                w.Write(id);
                w.Write((byte)kind);
                w.Write(thrown);
                w.Write(gone);
                Items.WritePose(w, pos, rot);
                Items.WriteVec(w, vel);
            });
        }
    }

    // ---------------------------------------------------------------- messages

    public static void Register()
    {
        Net.Register(CanMsg.GrabReq, OnGrabReq);
        Net.Register(CanMsg.GrabResult, OnGrabResult);
        Net.Register(CanMsg.OwnerSet, OnOwnerSet);
        Net.Register(CanMsg.HeldXform, OnHeldXform);
        Net.Register(CanMsg.Release, OnRelease);
    }

    static void BroadcastOwner(uint id, int holder, GrabKind kind, bool includePose, Peer except = null)
    {
        if (!CanHost.HasPeers) return;
        Net.Broadcast(CanMsg.OwnerSet, w => WriteOwner(w, id, holder, kind, includePose), except: except);
    }

    static void WriteOwner(NetWriter w, uint id, int holder, GrabKind kind, bool includePose)
    {
        w.Write(id);
        w.Write((sbyte)holder);
        w.Write((byte)kind);
        bool havePose = false;
        Vector3 p = default; Quaternion q = default;
        if (includePose && Items.TryResolve(id, out var comp))
        {
            var t = comp.transform;
            p = t.position; q = t.rotation; havePose = true;
        }
        w.Write(havePose);
        if (havePose) Items.WritePose(w, p, q);
    }

    /// <summary>Late join: tell the new peer who holds what.</summary>
    internal static void SendHoldsTo(Peer peer)
    {
        foreach (var kv in Table.All)
        {
            uint id = kv.Key;
            var hold = kv.Value;
            Net.SendTo(peer, CanMsg.OwnerSet, w => WriteOwner(w, id, hold.Holder, hold.Kind, includePose: false));
        }
    }

    static void OnGrabReq(Peer from, NetReader r)
    {
        if (!Net.IsHost) return;
        uint id = r.ReadUInt();
        var kind = (GrabKind)r.ReadByte();

        bool ok = Items.TryResolve(id, out var comp) && Table.TryGrab(id, from.Index, kind);
        Net.SendTo(from, CanMsg.GrabResult, w => { w.Write(id); w.Write((byte)kind); w.Write(ok); });
        if (!ok) return;

        Guard.Run("host apply grab", () => HostApplyGrab(comp, kind));
        BroadcastOwner(id, from.Index, kind, includePose: false, except: from);
    }

    static void HostApplyGrab(Component comp, GrabKind kind)
    {
        if (kind == GrabKind.Vacuum)
        {
            comp.gameObject.SetActive(false);
            return;
        }
        Items.OnGrabbed(comp);
        var rb = Items.RigidbodyOf(comp);
        if (rb != null) { rb.linearVelocity = Vector3.zero; rb.isKinematic = true; }
    }

    static void OnGrabResult(Peer from, NetReader r)
    {
        uint id = r.ReadUInt();
        var kind = (GrabKind)r.ReadByte();
        bool ok = r.ReadBool();
        if (ok) return;
        Guard.Run("grab rollback", () => RollBack(id, kind));
    }

    static void RollBack(uint id, GrabKind kind)
    {
        if (Table.HolderOf(id) == Items.LocalIndex) Table.Remove(id);

        if (kind == GrabKind.Hand)
        {
            if (_handId != id) return;
            _handId = 0; // before ClearGrabbable so the poll does not report a release
            var pl = Player.Instance;
            var held = pl?.HeldEnemy;
            if (pl != null && held != null) pl.ClearGrabbable(held);
            CanClient.EndLocalHold(id, Vector3.zero, simulate: false);
            return;
        }

        if (!VacSet.Remove(id)) return;
        var vac = Vacuum.Instance;
        var list = vac?.grabbables;
        if (Items.TryResolve(id, out var comp))
        {
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                    if (list[i] != null && list[i].Pointer == comp.Pointer) { list.RemoveAt(i); break; }
            comp.gameObject.SetActive(true);
            Items.RestoreLayer(comp);
        }
        if (list != null) _vacCount = list.Count;
        CanClient.EndLocalHold(id, Vector3.zero, simulate: false);
    }

    static void OnOwnerSet(Peer from, NetReader r)
    {
        if (Net.IsHost) return;
        uint id = r.ReadUInt();
        int holder = r.ReadSByte();
        var kind = (GrabKind)r.ReadByte();
        bool havePose = r.ReadBool();
        Vector3 pos = default; Quaternion rot = Quaternion.identity;
        if (havePose) Items.ReadPose(r, out pos, out rot);

        if (holder == Items.LocalIndex && holder != GrabTable.None) return; // our own grab echoed
        Table.Force(id, holder, kind);
        if (!Items.TryResolve(id, out var comp)) return;

        Guard.Run("owner set", () =>
        {
            var go = comp.gameObject;
            if (holder >= 0)
            {
                if (kind == GrabKind.Vacuum && !VacSet.Contains(id)) go.SetActive(false);
                return;
            }

            if (VacSet.Contains(id)) return; // we hold it in our own vacuum
            bool wasInactive = !go.activeSelf;
            if (wasInactive) go.SetActive(true);
            if (!havePose) return;
            if (CanClient.IsPuppet(id))
            {
                if (wasInactive) CanClient.Snap(id, pos, rot);
                else CanClient.ApplyTransform(id, pos, rot, Time.realtimeSinceStartup);
            }
            else
            {
                comp.transform.SetPositionAndRotation(pos, rot);
                RemoteHeldPose?.Invoke(id, pos, rot);
            }
        });
    }

    static void OnHeldXform(Peer from, NetReader r)
    {
        uint id = r.ReadUInt();
        Items.ReadPose(r, out var pos, out var rot);
        if (from.Index == Items.LocalIndex) return;

        if (Net.IsHost)
        {
            if (Table.HolderOf(id) != from.Index) return;
            LastPose[id] = new HeldPose { P = pos, Q = rot };
            if (Items.TryResolve(id, out var comp)) HostMove(comp, pos, rot);
        }
        else
        {
            if (CanClient.IsPuppet(id)) CanClient.ApplyTransform(id, pos, rot, Time.realtimeSinceStartup);
            else RemoteHeldPose?.Invoke(id, pos, rot);
        }
    }

    static void HostMove(Component comp, Vector3 pos, Quaternion rot)
    {
        comp.transform.SetPositionAndRotation(pos, rot);
        var rb = Items.RigidbodyOf(comp);
        if (rb != null) { rb.position = pos; rb.rotation = rot; }
    }

    static void OnRelease(Peer from, NetReader r)
    {
        if (!Net.IsHost) return;
        uint id = r.ReadUInt();
        var kind = (GrabKind)r.ReadByte();
        bool thrown = r.ReadBool();
        bool gone = r.ReadBool();
        Items.ReadPose(r, out var pos, out var rot);
        Vector3 vel = Items.ReadVec(r);

        if (!Table.TryRelease(id, from.Index, out var heldKind)) return;
        LastPose.Remove(id);

        if (Items.TryResolve(id, out var comp))
            Guard.Run("host apply release", () => HostPlace(id, comp, heldKind, thrown, gone, pos, rot, vel));
        BroadcastOwner(id, GrabTable.None, heldKind, includePose: !gone, except: from);
    }

    /// <summary>Gives physics of a formerly held object back to the host simulation.</summary>
    static void HostPlace(uint id, Component comp, GrabKind kind, bool thrown, bool gone, Vector3 pos, Quaternion rot, Vector3 vel)
    {
        if (gone)
        {
            // The holder's vacuum was cleared: the object does not come back.
            var can = comp as Can ?? comp.TryCast<Can>();
            if (can != null)
            {
                comp.gameObject.SetActive(true);
                GameManager.Instance?.ReturnCanToPool(can);
                CanHost.ForgetAndAnnounceGone(id);
            }
            else
            {
                var crate = comp as Crate ?? comp.TryCast<Crate>();
                if (crate != null) { comp.gameObject.SetActive(true); crate.Restore(); }
            }
            return;
        }

        if (kind == GrabKind.Vacuum)
        {
            comp.gameObject.SetActive(true);
            Items.RestoreLayer(comp);
        }
        HostMove(comp, pos, rot);

        var rb = Items.RigidbodyOf(comp);
        if (rb != null) rb.isKinematic = false;
        if (thrown) Items.OnThrown(comp, vel);     // sets Can.HoleInOne on the authoritative machine
        else if (kind == GrabKind.Hand) Items.OnDropped(comp);
        if (rb != null)
        {
            rb.isKinematic = false;
            rb.linearVelocity = vel;
            rb.angularVelocity = Vector3.zero;
        }
        CanHost.InvalidateTransform(id);
    }

    // ---------------------------------------------------------------- lifecycle

    public static void OnPeerLeft(Peer peer)
    {
        if (!Net.IsHost) return;
        var released = new List<KeyValuePair<uint, GrabKind>>();
        Table.ReleaseAll(peer.Index, released);
        foreach (var kv in released)
        {
            uint id = kv.Key;
            if (!Items.TryResolve(id, out var comp)) continue;
            Vector3 pos; Quaternion rot;
            if (LastPose.TryGetValue(id, out var hp)) { pos = hp.P; rot = hp.Q; }
            else FallbackPose(comp, out pos, out rot);
            LastPose.Remove(id);
            Guard.Run("release on leave", () => HostPlace(id, comp, kv.Value, thrown: false, gone: false, pos, rot, Vector3.zero));
            BroadcastOwner(id, GrabTable.None, kv.Value, includePose: true);
        }
    }

    static void FallbackPose(Component comp, out Vector3 pos, out Quaternion rot)
    {
        pos = comp.transform.position;
        rot = comp.transform.rotation;
        var procs = GameManager.Instance?.canProcessors;
        if (procs == null) return;
        for (int i = 0; i < procs.Length; i++)
        {
            var sp = procs[i]?.spawnPoint;
            if (sp != null) { pos = sp.position + Vector3.up * 0.5f; return; }
        }
    }

    /// <summary>Host session end: nobody holds anything any more, make sure nothing stays frozen or hidden.</summary>
    public static void ReleaseAllOnHost()
    {
        // On a client the table is only a mirror: the objects are puppets (CanClient.Shutdown / ClientPuppets.ReturnAll clean them up)
        if (!WasHost) return;
        var all = new List<KeyValuePair<uint, GrabTable.Hold>>(Table.All);
        foreach (var kv in all)
        {
            if (kv.Value.Holder == 0 || !Items.TryResolve(kv.Key, out var comp)) continue;
            FallbackPose(comp, out var pos, out var rot);
            Guard.Run("release at session end", () => HostPlace(kv.Key, comp, kv.Value.Kind, false, false, pos, rot, Vector3.zero));
        }
    }

    /// <summary>The object was consumed by the host (paid / returned): drop any local hold on it.</summary>
    internal static void NotifyObjectConsumed(uint id)
    {
        Table.Remove(id);
        if (_handId == id)
        {
            _handId = 0;
            var pl = Player.Instance;
            var held = pl?.HeldEnemy;
            if (pl != null && held != null && NetIds.IdOf(held) == id) pl.ClearGrabbable(held);
        }
        VacSet.Remove(id);
    }
}
