using System;
using System.Collections.Generic;
using CanEmAllCoop.Cans;
using Xunit;

namespace CanEmAllCoop.Tests;

public class QuatPackTests
{
    static float AngleDeg(float ax, float ay, float az, float aw, float bx, float by, float bz, float bw)
    {
        float dot = MathF.Abs(ax * bx + ay * by + az * bz + aw * bw);
        return 2f * MathF.Acos(MathF.Min(1f, dot)) * 180f / MathF.PI;
    }

    [Fact]
    public void Identity_roundtrips()
    {
        QuatPack.Unpack(QuatPack.Pack(0, 0, 0, 1), out var x, out var y, out var z, out var w);
        Assert.True(AngleDeg(0, 0, 0, 1, x, y, z, w) < 0.1f);
    }

    [Fact]
    public void Random_rotations_stay_within_half_a_degree()
    {
        var rng = new Random(1234);
        for (int i = 0; i < 5000; i++)
        {
            float qx = (float)(rng.NextDouble() * 2 - 1), qy = (float)(rng.NextDouble() * 2 - 1),
                  qz = (float)(rng.NextDouble() * 2 - 1), qw = (float)(rng.NextDouble() * 2 - 1);
            float len = MathF.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
            if (len < 0.01f) continue;
            qx /= len; qy /= len; qz /= len; qw /= len;
            QuatPack.Unpack(QuatPack.Pack(qx, qy, qz, qw), out var x, out var y, out var z, out var w);
            Assert.True(AngleDeg(qx, qy, qz, qw, x, y, z, w) < 0.5f, $"iteration {i}");
        }
    }

    [Fact]
    public void Negated_quaternion_packs_to_the_same_rotation()
    {
        QuatPack.Unpack(QuatPack.Pack(0.5f, 0.5f, 0.5f, 0.5f), out var a, out var b, out var c, out var d);
        QuatPack.Unpack(QuatPack.Pack(-0.5f, -0.5f, -0.5f, -0.5f), out var e, out var f, out var g, out var h);
        Assert.True(AngleDeg(a, b, c, d, e, f, g, h) < 0.1f);
    }

    [Fact]
    public void Degenerate_input_becomes_identity()
    {
        QuatPack.Unpack(QuatPack.Pack(0, 0, 0, 0), out var x, out var y, out var z, out var w);
        Assert.True(AngleDeg(0, 0, 0, 1, x, y, z, w) < 0.1f);
    }
}

public class TransformFilterTests
{
    [Fact]
    public void First_sight_is_sent()
    {
        var f = new TransformFilter();
        Assert.True(f.ShouldSend(1, 0, 0, 0, 0, 0, 0, 1));
    }

    [Fact]
    public void Resting_object_is_sent_a_few_extra_times_then_goes_quiet()
    {
        var f = new TransformFilter(restRepeats: 2);
        Assert.True(f.ShouldSend(1, 0, 0, 0, 0, 0, 0, 1));
        Assert.True(f.ShouldSend(1, 0, 0, 0, 0, 0, 0, 1));
        Assert.True(f.ShouldSend(1, 0, 0, 0, 0, 0, 0, 1));
        Assert.False(f.ShouldSend(1, 0, 0, 0, 0, 0, 0, 1));
        Assert.False(f.ShouldSend(1, 0, 0, 0, 0, 0, 0, 1));
    }

    [Fact]
    public void Movement_and_rotation_wake_the_object_up_again()
    {
        var f = new TransformFilter(positionThreshold: 0.02f, angleThresholdDegrees: 2f, restRepeats: 0);
        f.ShouldSend(1, 0, 0, 0, 0, 0, 0, 1);
        Assert.False(f.ShouldSend(1, 0.005f, 0, 0, 0, 0, 0, 1));
        Assert.True(f.ShouldSend(1, 0.5f, 0, 0, 0, 0, 0, 1));
        // 10 degrees about Y
        float s = MathF.Sin(5f * MathF.PI / 180f), c = MathF.Cos(5f * MathF.PI / 180f);
        Assert.True(f.ShouldSend(1, 0.5f, 0, 0, 0, s, 0, c));
        Assert.False(f.ShouldSend(1, 0.5f, 0, 0, 0, s, 0, c));
    }

    [Fact]
    public void Sub_threshold_drift_does_not_accumulate_silently()
    {
        // The reference is the last SENT state, so slow creep eventually crosses the threshold.
        var f = new TransformFilter(positionThreshold: 0.1f, restRepeats: 0);
        f.ShouldSend(1, 0, 0, 0, 0, 0, 0, 1);
        bool sent = false;
        for (int i = 1; i <= 30; i++) sent |= f.ShouldSend(1, i * 0.01f, 0, 0, 0, 0, 0, 1);
        Assert.True(sent);
    }

    [Fact]
    public void Invalidate_forces_a_resend()
    {
        var f = new TransformFilter(restRepeats: 0);
        f.ShouldSend(1, 0, 0, 0, 0, 0, 0, 1);
        Assert.False(f.ShouldSend(1, 0, 0, 0, 0, 0, 0, 1));
        f.Invalidate(1);
        Assert.True(f.ShouldSend(1, 0, 0, 0, 0, 0, 0, 1));
    }
}

public class GrabTableTests
{
    [Fact]
    public void Second_peer_is_denied_while_held()
    {
        var t = new GrabTable();
        Assert.True(t.TryGrab(10, 1, GrabKind.Hand));
        Assert.False(t.TryGrab(10, 2, GrabKind.Hand));
        Assert.False(t.TryGrab(10, 0, GrabKind.Vacuum));
        Assert.Equal(1, t.HolderOf(10));
    }

    [Fact]
    public void Same_peer_may_regrab()
    {
        var t = new GrabTable();
        Assert.True(t.TryGrab(10, 1, GrabKind.Hand));
        Assert.True(t.TryGrab(10, 1, GrabKind.Hand));
    }

    [Fact]
    public void Only_holder_releases()
    {
        var t = new GrabTable();
        t.TryGrab(10, 1, GrabKind.Vacuum);
        Assert.False(t.TryRelease(10, 2, out _));
        Assert.True(t.TryRelease(10, 1, out var kind));
        Assert.Equal(GrabKind.Vacuum, kind);
        Assert.False(t.IsHeld(10));
        Assert.True(t.TryGrab(10, 2, GrabKind.Hand));
    }

    [Fact]
    public void Release_all_returns_only_that_peers_objects()
    {
        var t = new GrabTable();
        t.TryGrab(1, 1, GrabKind.Hand);
        t.TryGrab(2, 1, GrabKind.Vacuum);
        t.TryGrab(3, 2, GrabKind.Hand);
        var released = new List<KeyValuePair<uint, GrabKind>>();
        t.ReleaseAll(1, released);
        Assert.Equal(2, released.Count);
        Assert.False(t.IsHeld(1));
        Assert.False(t.IsHeld(2));
        Assert.True(t.IsHeld(3));
    }

    [Fact]
    public void Invalid_ids_and_peers_are_rejected()
    {
        var t = new GrabTable();
        Assert.False(t.TryGrab(0, 1, GrabKind.Hand));
        Assert.False(t.TryGrab(5, -1, GrabKind.Hand));
    }

    [Fact]
    public void Mirror_force_sets_and_clears()
    {
        var t = new GrabTable();
        t.Force(7, 2, GrabKind.Hand);
        Assert.Equal(2, t.HolderOf(7));
        t.Force(7, GrabTable.None, GrabKind.Hand);
        Assert.Equal(GrabTable.None, t.HolderOf(7));
    }
}

public class SpawnQueueTests
{
    static SpawnInfo Make(uint id, int qty = 1) => new SpawnInfo { Id = id, Quantity = qty, Price = 5 };

    [Fact]
    public void Dequeues_in_arrival_order()
    {
        var q = new SpawnQueue();
        q.Add(Make(3)); q.Add(Make(1)); q.Add(Make(2));
        Assert.True(q.TryDequeue(out var a)); Assert.Equal(3u, a.Id);
        Assert.True(q.TryDequeue(out var b)); Assert.Equal(1u, b.Id);
        Assert.True(q.TryDequeue(out var c)); Assert.Equal(2u, c.Id);
        Assert.False(q.TryDequeue(out _));
    }

    [Fact]
    public void Merge_updates_pending_entry_and_removal_skips_it()
    {
        var q = new SpawnQueue();
        q.Add(Make(1)); q.Add(Make(2));
        Assert.True(q.SetQuantity(1, 4));
        Assert.True(q.Remove(2));
        Assert.True(q.TryDequeue(out var a));
        Assert.Equal(4, a.Quantity);
        Assert.False(q.TryDequeue(out _));
        Assert.False(q.SetQuantity(1, 5));
    }

    [Fact]
    public void Re_adding_an_id_replaces_it_without_duplicating()
    {
        var q = new SpawnQueue();
        q.Add(Make(1, 1)); q.Add(Make(1, 3));
        Assert.Equal(1, q.Count);
        Assert.True(q.TryDequeue(out var a));
        Assert.Equal(3, a.Quantity);
        Assert.False(q.TryDequeue(out _));
    }

    [Fact]
    public void Pose_update_applies_to_pending_entry()
    {
        var q = new SpawnQueue();
        q.Add(Make(1));
        Assert.True(q.SetPose(1, 1f, 2f, 3f, 42u));
        q.TryDequeue(out var a);
        Assert.Equal(2f, a.Py);
        Assert.Equal(42u, a.Rot);
    }
}
