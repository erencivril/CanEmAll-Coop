using System;
using CanEmAllCoop.Core;

using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace CanEmAllCoop.Cans;
using CanEmAllCoop.Net;

/// <summary>Message ids 400..499 (DESIGN.md section 2).</summary>
internal static class CanMsg
{
    public const ushort Spawn = 400;      // host -> all: new can {id, proc, price, flags, tex, qty, pose}
    public const ushort Merge = 401;      // host -> all: can quantity changed {id, qty}
    public const ushort Paid = 402;       // host -> all: can was paid by the reward pipe {id}
    public const ushort Gone = 403;       // host -> all: can left the world unpaid (pool return) {id}
    public const ushort Transforms = 404; // host -> all, unreliable: batch of {id, pos(q), rot(packed)}
    public const ushort CrateIds = 405;   // host -> all: ids of GameManager.crates in array order
    public const ushort GrabReq = 406;    // client -> host: {id, kind}
    public const ushort GrabResult = 407; // host -> requester: {id, granted}
    public const ushort OwnerSet = 408;   // host -> all: {id, holder(-1 none), kind, pose}
    public const ushort HeldXform = 409;  // holder -> all (relayed), unreliable: {id, pos, rot}
    public const ushort Release = 410;    // holder -> host: {id, kind, thrown, gone, pos, rot, vel}
}

/// <summary>Thin helpers over the three IGrabbable implementations (Can, Crate, Enemy), which share no base class.</summary>
internal static class Items
{
    public static Component AsComponent(Il2CppObjectBase o)
    {
        if (o == null) return null;
        if (o is Component c) return c;
        return o.TryCast<Component>();
    }

    public static bool TryResolve(uint id, out Component comp)
    {
        comp = null;
        if (id == 0 || !NetIds.TryGet<Il2CppObjectBase>(id, out var o)) return false;
        comp = AsComponent(o);
        return comp != null;
    }

    public static Rigidbody RigidbodyOf(Component c)
    {
        if (c == null) return null;
        var can = c as Can ?? c.TryCast<Can>();
        if (can != null) return can.rb;
        var crate = c as Crate ?? c.TryCast<Crate>();
        if (crate != null) return crate.rb;
        var enemy = c as Enemy ?? c.TryCast<Enemy>();
        return enemy?.GrabRigidbody;
    }

    public static void OnGrabbed(Component c)
    {
        var can = c as Can ?? c.TryCast<Can>();
        if (can != null) { can.OnGrabbed(); return; }
        var crate = c as Crate ?? c.TryCast<Crate>();
        if (crate != null) { crate.OnGrabbed(); return; }
        (c as Enemy ?? c.TryCast<Enemy>())?.OnGrabbed();
    }

    public static void OnDropped(Component c)
    {
        var can = c as Can ?? c.TryCast<Can>();
        if (can != null) { can.OnDropped(); return; }
        var crate = c as Crate ?? c.TryCast<Crate>();
        if (crate != null) { crate.OnDropped(); return; }
        (c as Enemy ?? c.TryCast<Enemy>())?.OnDropped();
    }

    public static void OnThrown(Component c, Vector3 velocity)
    {
        var can = c as Can ?? c.TryCast<Can>();
        if (can != null) { can.OnThrown(velocity); return; }
        var crate = c as Crate ?? c.TryCast<Crate>();
        if (crate != null) { crate.OnThrown(velocity); return; }
        (c as Enemy ?? c.TryCast<Enemy>())?.OnThrown(velocity);
    }

    public static void RestoreLayer(Component c)
    {
        var can = c as Can ?? c.TryCast<Can>();
        if (can != null) { can.RestoreCollidersLayer(); return; }
        var crate = c as Crate ?? c.TryCast<Crate>();
        if (crate != null) { crate.RestoreCollidersLayer(); return; }
        (c as Enemy ?? c.TryCast<Enemy>())?.RestoreCollidersLayer();
    }

    public static void WritePose(NetWriter w, Vector3 p, Quaternion q)
    {
        w.Write(p.x); w.Write(p.y); w.Write(p.z);
        w.Write(QuatPack.Pack(q.x, q.y, q.z, q.w));
    }

    public static void ReadPose(NetReader r, out Vector3 p, out Quaternion q)
    {
        p = new Vector3(r.ReadFloat(), r.ReadFloat(), r.ReadFloat());
        q = UnpackRot(r.ReadUInt());
    }

    public static Quaternion UnpackRot(uint packed)
    {
        QuatPack.Unpack(packed, out var x, out var y, out var z, out var w);
        return new Quaternion(x, y, z, w);
    }

    public static uint PackRot(Quaternion q) => QuatPack.Pack(q.x, q.y, q.z, q.w);

    public static void WriteVec(NetWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }

    public static Vector3 ReadVec(NetReader r) => new Vector3(r.ReadFloat(), r.ReadFloat(), r.ReadFloat());

    /// <summary>Peer.Index of a peer, 0 for the host.</summary>
    public static int LocalIndex => CanEmAllCoop.Net.Net.LocalPeer?.Index ?? 0;
}

/// <summary>Runs a piece of per-frame work, logging the first failures only.</summary>
internal static class Guard
{
    static readonly System.Collections.Generic.Dictionary<string, int> Failures = new();

    public static void Run(string what, Action a)
    {
        try { a(); }
        catch (Exception e)
        {
            Failures.TryGetValue(what, out var n);
            Failures[what] = ++n;
            if (n <= 3 || n % 500 == 0) Log.Error($"Cans: {what} failed (x{n})", e);
        }
    }

    public static int TotalFailures
    {
        get { int t = 0; foreach (var v in Failures.Values) t += v; return t; }
    }
}
