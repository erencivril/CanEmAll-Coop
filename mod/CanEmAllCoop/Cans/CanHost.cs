using System;
using System.Collections.Generic;
using CanEmAllCoop.Core;
using UnityEngine;

namespace CanEmAllCoop.Cans;
using CanEmAllCoop.Net;
using Net = CanEmAllCoop.Net.Net;

/// <summary>
/// Host side of the money pipe: keeps a registry of every live can and crate, announces spawns / merges /
/// payments, and streams the transforms of the ones that move. The host runs the real simulation.
/// </summary>
internal static class CanHost
{
    internal sealed class Tracked
    {
        public uint Id;
        public Component Obj;
        public Can Can;      // null for crates
        public Transform T;
        public int Qty;
        public int Proc;
        public int Tex;
        public bool IsCrate => Can == null;
    }

    static readonly Dictionary<uint, Tracked> Registry = new();
    static readonly TransformFilter Filter = new(0.02f, 1.5f, 2);
    static readonly Dictionary<IntPtr, int> ProcIndex = new();
    static readonly List<uint> Scratch = new();
    struct Sent { public uint Id; public Vector3 P; public uint Rot; }
    static readonly List<Sent> Batch = new();
    static readonly List<Tracked> Order = new();   // stable iteration order of the registry for the round-robin stream
    static bool _orderDirty = true;
    static int _cursor;

    // Filled by the GetCanFromPool / FindCompatibleCan postfixes while CanProcessor spawns or merges a can.
    static Can _lastTaken;
    static Can _lastCompat;
    static bool _spawnedInCreate;

    static float _nextStream;
    static float _nextSweep;
    static uint[] _crateIds = Array.Empty<uint>();
    static bool _crateIdsDirty;
    static bool _warnedSweepSpawn;

    public static int TrackedCount => Registry.Count;
    public static bool HasPeers => Net.PlayerCount > 1;

    public static bool TryGet(uint id, out Tracked t) => Registry.TryGetValue(id, out t);

    public static void Reset()
    {
        Registry.Clear();
        Order.Clear();
        _orderDirty = true;
        _cursor = 0;
        Filter.Clear();
        ProcIndex.Clear();
        _lastTaken = null;
        _lastCompat = null;
        _crateIds = Array.Empty<uint>();
        _crateIdsDirty = false;
        _nextStream = _nextSweep = 0f;
    }

    // ---------------------------------------------------------------- hooks (host only, called from patches)

    public static void OnCanTakenFromPool(Can can) => _lastTaken = can;

    public static void OnSpawnCan(CanProcessor proc, int textureIndex)
    {
        _spawnedInCreate = true;
        var can = _lastTaken;
        _lastTaken = null;
        if (can == null) return; // the 1 Hz sweep will find it
        Track(can, ProcessorIndex(proc), textureIndex, announce: true);
    }

    public static void OnCreateBegin()
    {
        _spawnedInCreate = false;
        _lastCompat = null;
    }

    public static void OnFindCompatible(Can result) => _lastCompat = result;

    public static void OnCreateEnd()
    {
        if (!_spawnedInCreate && _lastCompat != null) NoteQuantity(_lastCompat);
        _lastCompat = null;
    }

    /// <summary>RewardMachine started the return-to-pool delay of a can: it was paid.</summary>
    public static void OnPaid(Can can)
    {
        uint id = NetIds.IdOf(can);
        if (id == 0 || !Registry.ContainsKey(id)) return;
        if (HasPeers) Net.Broadcast(CanMsg.Paid, w => w.Write(id));
        Forget(id);
    }

    // ---------------------------------------------------------------- registry

    static int ProcessorIndex(CanProcessor proc)
    {
        if (proc == null) return -1;
        if (ProcIndex.TryGetValue(proc.Pointer, out var idx)) return idx;
        idx = -1;
        var gm = GameManager.Instance;
        var arr = gm?.canProcessors;
        if (arr != null)
            for (int i = 0; i < arr.Length; i++)
                if (arr[i] != null && arr[i].Pointer == proc.Pointer) { idx = i; break; }
        ProcIndex[proc.Pointer] = idx;
        return idx;
    }

    public static Tracked Track(Can can, int proc, int tex, bool announce)
    {
        uint old = NetIds.IdOf(can);
        if (old != 0)
        {
            // The pool handed out a recycled object we never saw leave: close the old life first.
            if (Registry.ContainsKey(old))
            {
                if (HasPeers) Net.Broadcast(CanMsg.Gone, w => w.Write(old));
                Forget(old);
            }
            else NetIds.Release(old);
        }

        uint id = NetIds.Assign(can);
        if (id == 0) return null;
        var t = new Tracked { Id = id, Obj = can, Can = can, T = can.transform, Qty = can.quantity, Proc = proc, Tex = tex };
        Registry[id] = t;
        _orderDirty = true;
        if (announce && HasPeers) SendSpawn(null, t);
        return t;
    }

    /// <summary>Grab protocol hook: make sure a can the host is about to hold has an id.</summary>
    public static void EnsureTracked(Component c)
    {
        var can = c as Can ?? c.TryCast<Can>();
        if (can == null || can.IsInPool) return;
        uint id = NetIds.IdOf(can);
        if (id != 0 && Registry.ContainsKey(id)) return;
        Track(can, -1, 0, announce: true);
    }

    public static void Forget(uint id)
    {
        if (Registry.Remove(id)) _orderDirty = true;
        Filter.Invalidate(id);
        GrabSync.Table.Remove(id);
        NetIds.Release(id);
    }

    static void NoteQuantity(Can can)
    {
        uint id = NetIds.IdOf(can);
        if (id == 0 || !Registry.TryGetValue(id, out var t)) return;
        int q = can.quantity;
        if (q == t.Qty) return;
        t.Qty = q;
        if (HasPeers) Net.Broadcast(CanMsg.Merge, w => { w.Write(id); w.Write((ushort)Math.Clamp(q, 0, ushort.MaxValue)); });
    }

    static void SendSpawn(Peer to, Tracked t)
    {
        var can = t.Can;
        if (can == null || t.T == null) return;
        Vector3 p = t.T.position;
        Quaternion q = t.T.rotation;
        uint id = t.Id;
        Action<NetWriter> write = w =>
        {
            w.Write(id);
            w.Write((sbyte)t.Proc);
            w.Write(can.price);
            w.Write((byte)((can.IsShiny ? 1 : 0) | (can.IsSmoked ? 2 : 0)));
            w.Write((short)t.Tex);
            w.Write((ushort)Math.Clamp(can.quantity, 0, ushort.MaxValue));
            Items.WritePose(w, p, q);
        };
        if (to == null) Net.Broadcast(CanMsg.Spawn, write);
        else Net.SendTo(to, CanMsg.Spawn, write);
    }

    // ---------------------------------------------------------------- per frame

    public static void Update(float now)
    {
        if (!HasPeers) return;

        if (now >= _nextSweep)
        {
            _nextSweep = now + SweepSeconds;
            Guard.Run("host sweep", Sweep);
        }

        float interval = 1f / Mathf.Max(1, CoopConfig.CanRateHz);
        if (now >= _nextStream)
        {
            _nextStream = now + interval;
            Guard.Run("host stream", StreamTransforms);
        }
    }

    // The registry walk is cheap, the FindObjectsByType net below is not (it wraps every live can in a managed proxy):
    // the SpawnCan / pool hooks announce cans, the net only catches a missed one, so a few seconds of delay cost nothing.
    const float SweepSeconds = 4f;

    /// <summary>Reconciles the registry with the world: new cans, quantity changes, cans that left.</summary>
    public static void Sweep()
    {
        var gm = GameManager.Instance;
        if (gm == null) return;

        TrackCrates(gm);

        Scratch.Clear();
        foreach (var kv in Registry) Scratch.Add(kv.Key);
        foreach (uint id in Scratch)
        {
            var t = Registry[id];
            if (t.Obj == null) { SendGone(id); continue; }
            if (t.Can == null) continue;
            if (t.Can.IsInPool) { SendGone(id); continue; }
            if (t.Can.IsProcessed)
            {
                // Paid but the ReturnCanAfterDelay hook was missed.
                if (HasPeers) Net.Broadcast(CanMsg.Paid, w => w.Write(id));
                Forget(id);
                continue;
            }
            if (t.Can.quantity != t.Qty) NoteQuantity(t.Can);
        }

        var all = UnityEngine.Object.FindObjectsByType<Can>(FindObjectsSortMode.None);
        if (all == null) return;
        for (int i = 0; i < all.Length; i++)
        {
            var c = all[i];
            if (c == null || c.IsInPool || c.IsProcessed) continue;
            uint id = NetIds.IdOf(c);
            if (id != 0 && Registry.ContainsKey(id)) continue;
            if (!_warnedSweepSpawn)
            {
                _warnedSweepSpawn = true;
                Log.Warn("Cans: sweep found a can the SpawnCan hook missed (texture index unknown, shown with texture 0 on clients)");
            }
            Track(c, c.sourceProcessor != null ? ProcessorIndex(c.sourceProcessor) : -1, 0, announce: true);
        }
    }

    static void SendGone(uint id)
    {
        if (HasPeers) Net.Broadcast(CanMsg.Gone, w => w.Write(id));
        Forget(id);
    }

    public static void ForgetAndAnnounceGone(uint id) => SendGone(id);

    static void TrackCrates(GameManager gm)
    {
        var crates = gm.crates;
        if (crates == null) return;
        if (_crateIds.Length != crates.Length) { _crateIds = new uint[crates.Length]; _crateIdsDirty = true; }
        for (int i = 0; i < crates.Length; i++)
        {
            var crate = crates[i];
            if (crate == null) { _crateIds[i] = 0; continue; }
            uint id = NetIds.IdOf(crate);
            if (id == 0 || !Registry.ContainsKey(id))
            {
                id = NetIds.Assign(crate);
                if (id == 0) continue;
                Registry[id] = new Tracked { Id = id, Obj = crate, Can = null, T = crate.transform, Qty = 1 };
                _orderDirty = true;
                _crateIdsDirty = true;
            }
            if (_crateIds[i] != id) { _crateIds[i] = id; _crateIdsDirty = true; }
        }
        if (_crateIdsDirty && HasPeers)
        {
            _crateIdsDirty = false;
            SendCrateIds(null);
        }
    }

    static void SendCrateIds(Peer to)
    {
        var ids = _crateIds;
        Action<NetWriter> write = w =>
        {
            w.Write((byte)Math.Min(ids.Length, 255));
            for (int i = 0; i < ids.Length && i < 255; i++) w.Write(ids[i]);
        };
        if (to == null) Net.Broadcast(CanMsg.CrateIds, write);
        else Net.SendTo(to, CanMsg.CrateIds, write);
    }

    // ~14 bytes per entry: 70 per packet stays near 1 KB (below a typical MTU, so one lost datagram costs 70 poses, not 400).
    const int MaxPerPacket = 70;
    // Bandwidth valve: at most this many transforms per stream tick (400 x 14 B x CanRateHz 8 = ~45 KB/s per client), the rest
    // waits for the next tick (round robin, so nothing starves). 1000 cans that all move still update at ~3 Hz.
    const int MaxPerTick = 400;

    static void RebuildOrder()
    {
        _orderDirty = false;
        Order.Clear();
        foreach (var t in Registry.Values) Order.Add(t);
        if (_cursor >= Order.Count) _cursor = 0;
    }

    static void StreamTransforms()
    {
        if (_orderDirty) RebuildOrder();
        Batch.Clear();
        int n = Order.Count;
        if (n == 0) return;
        if (_cursor >= n) _cursor = 0;
        int i = _cursor;
        int visited = 0;
        while (visited < n && Batch.Count < MaxPerTick)
        {
            var t = Order[i];
            i++;
            if (i >= n) i = 0;
            visited++;
            if (t.T == null) continue;
            if (GrabSync.Table.TryGetHold(t.Id, out var h) && (h.Kind == GrabKind.Vacuum || h.Holder != 0))
                continue; // remote-held objects are streamed by their holder, vacuumed ones are inactive
            Vector3 p = t.T.position;
            Quaternion q = t.T.rotation;
            if (Filter.ShouldSend(t.Id, p.x, p.y, p.z, q.x, q.y, q.z, q.w)) Batch.Add(new Sent { Id = t.Id, P = p, Rot = Items.PackRot(q) });
        }
        _cursor = i;   // the entity after the last one examined goes first next tick

        for (int start = 0; start < Batch.Count; start += MaxPerPacket)
        {
            int from = start, count = Math.Min(MaxPerPacket, Batch.Count - start);
            Net.Broadcast(CanMsg.Transforms, w =>
            {
                w.Write((ushort)count);
                for (int k = from; k < from + count; k++)
                {
                    var e = Batch[k];
                    w.Write(e.Id);
                    w.WriteVec3Q(e.P);
                    w.Write(e.Rot);
                }
            }, reliable: false);
        }
    }

    // ---------------------------------------------------------------- late join / leave

    public static void OnPeerJoined(Peer peer)
    {
        Guard.Run("host sweep (join)", Sweep);
        Filter.Clear(); // everything is sent again with the next batch, resting crates included
        SendCrateIds(peer);
        foreach (var t in Registry.Values)
            if (!t.IsCrate) SendSpawn(peer, t);
        GrabSync.SendHoldsTo(peer);
    }

    public static void InvalidateTransform(uint id) => Filter.Invalidate(id);
}
