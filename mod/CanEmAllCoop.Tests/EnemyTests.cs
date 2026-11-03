using System;
using System.Collections.Generic;
using CanEmAllCoop.Enemies;
using CanEmAllCoop.Net;
using Xunit;

namespace CanEmAllCoop.Tests;

public class EnemyTests
{
    static NetReader Reader(NetWriter w) => new NetReader(w.ToArray());

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(127u)]
    [InlineData(128u)]
    [InlineData(300u)]
    [InlineData(16384u)]
    [InlineData(uint.MaxValue)]
    public void VarIntRoundTrips(uint v)
    {
        var w = new NetWriter();
        PoseCodec.WriteVarUInt(w, v);
        Assert.Equal(v, PoseCodec.ReadVarUInt(Reader(w)));
    }

    [Fact]
    public void SmallIdsCostOneOrTwoBytes()
    {
        var w = new NetWriter();
        PoseCodec.WriteVarUInt(w, 100);
        Assert.Equal(1, w.Length);
        w.Reset();
        PoseCodec.WriteVarUInt(w, 750);
        Assert.Equal(2, w.Length);
    }

    [Fact]
    public void QuaternionSmallestThreeRoundTripsWithinHalfADegree()
    {
        var rnd = new Random(1234);
        for (int i = 0; i < 500; i++)
        {
            var q = new Quat4((float)rnd.NextDouble() * 2 - 1, (float)rnd.NextDouble() * 2 - 1,
                (float)rnd.NextDouble() * 2 - 1, (float)rnd.NextDouble() * 2 - 1).Normalized();
            var w = new NetWriter();
            PoseCodec.WriteQuat(w, q);
            Assert.Equal(7, w.Length);
            var back = PoseCodec.ReadQuat(Reader(w));
            Assert.True(q.AngleTo(back) < 0.5f, $"angle {q.AngleTo(back)}");
        }
    }

    [Fact]
    public void YawOnlyPoseUsesTheShortFormAndKeepsTheHeading()
    {
        var e = new PoseEntry { Id = 5, X = 10.5f, Y = 0.25f, Z = -33f, Rot = Quat4.FromYawDegrees(123f) };
        var w = new NetWriter();
        PoseCodec.WritePose(w, in e);
        // id(1) + flags(1) + pos(6) + yaw(2)
        Assert.Equal(10, w.Length);
        var back = PoseCodec.ReadPose(Reader(w));
        Assert.Equal(5u, back.Id);
        Assert.InRange(back.X, 10.49f, 10.51f);
        Assert.InRange(back.Y, 0.24f, 0.26f);
        Assert.InRange(back.Z, -33.02f, -32.98f);
        Assert.InRange(back.Rot.YawDegrees(), 122.9f, 123.1f);
        Assert.False(back.Dead);
    }

    [Fact]
    public void TumblingBodyUsesTheFullRotation()
    {
        var q = new Quat4(0.5f, 0.5f, 0.5f, 0.5f);
        var e = new PoseEntry { Id = 70000, X = -1, Y = 2, Z = 3, Rot = q, Dead = true };
        var w = new NetWriter();
        PoseCodec.WritePose(w, in e);
        var back = PoseCodec.ReadPose(Reader(w));
        Assert.True(back.Dead);
        Assert.Equal(70000u, back.Id);
        Assert.True(q.AngleTo(back.Rot) < 0.5f);
    }

    [Fact]
    public void PoseBatchFitsAnMtuAtSeventyEntries()
    {
        var list = new List<PoseEntry>();
        for (uint i = 1; i <= 70; i++)
            list.Add(new PoseEntry { Id = 600 + i, X = i, Y = 1, Z = -i, Rot = Quat4.FromYawDegrees(i * 5) });
        var w = new NetWriter();
        PoseCodec.WritePoseBatch(w, list, 0, list.Count);
        Assert.True(w.Length < 1200, $"{w.Length} bytes");

        var back = new List<PoseEntry>();
        PoseCodec.ReadPoseBatch(Reader(w), back);
        Assert.Equal(70, back.Count);
        Assert.Equal(670u, back[69].Id);
    }

    [Fact]
    public void PositionsOutsideTheBoxAreClampedNotWrapped()
    {
        var e = new PoseEntry { Id = 1, X = 5000, Y = -5000, Z = 0, Rot = Quat4.Identity };
        var w = new NetWriter();
        PoseCodec.WritePose(w, in e);
        var back = PoseCodec.ReadPose(Reader(w));
        Assert.InRange(back.X, 511f, 512.1f);
        Assert.InRange(back.Y, -512.1f, -511f);
    }

    [Fact]
    public void SpawnBatchRoundTrips()
    {
        var list = new List<SpawnEntry>
        {
            new SpawnEntry { Id = 1, Prefab = 3, Shiny = true, State = 0, HealthPct = 100, X = 1, Y = 2, Z = 3, Rot = Quat4.FromYawDegrees(90) },
            new SpawnEntry { Id = 2, Prefab = 0, Dead = true, State = 2, HealthPct = 0, X = -4, Y = 0.5f, Z = 9, Rot = new Quat4(0.1f, 0.2f, 0.3f, 0.9f).Normalized() },
        };
        var w = new NetWriter();
        PoseCodec.WriteSpawnBatch(w, list, 0, 2);
        var back = new List<SpawnEntry>();
        PoseCodec.ReadSpawnBatch(Reader(w), back);
        Assert.Equal(2, back.Count);
        Assert.True(back[0].Shiny);
        Assert.False(back[0].Dead);
        Assert.Equal((ushort)3, back[0].Prefab);
        Assert.True(back[1].Dead);
        Assert.Equal((byte)2, back[1].State);
        Assert.InRange(back[1].Z, 8.98f, 9.02f);
    }

    [Fact]
    public void ReleaseAndIdListsRoundTrip()
    {
        var ids = new List<uint> { 3, 4, 500, 70000 };
        var w = new NetWriter();
        PoseCodec.WriteRelease(w, 0f, 0f, 1f, 2.5f, 12f, ids);
        var back = new List<uint>();
        PoseCodec.ReadRelease(Reader(w), out var dx, out var dy, out var dz, out var speed, out var roam, back);
        Assert.Equal(new[] { 0f, 0f, 1f }, new[] { dx, dy, dz });
        Assert.Equal(2.5f, speed);
        Assert.Equal(12f, roam);
        Assert.Equal(ids, back);
    }

    [Fact]
    public void DeathAndHitEntriesRoundTrip()
    {
        var d = new DeathEntry { Id = 9, X = 1, Y = 2, Z = 3, Rot = Quat4.FromYawDegrees(45), Vx = 4, Vy = 5, Vz = -6, Wx = 1, Wy = 2, Wz = 3 };
        var w = new NetWriter();
        PoseCodec.WriteDeath(w, in d);
        var db = PoseCodec.ReadDeath(Reader(w));
        Assert.Equal(9u, db.Id);
        Assert.Equal(-6f, db.Vz);

        var h = new HitEntry
        {
            Id = 12, Damage = 37.5f, DirX = 0, DirY = 0, DirZ = 1, PosX = 1, PosY = 2, PosZ = 3,
            RadiusMult = 1.5f, ProbMult = 8f, Flags = (byte)(HitFlags.Blood | HitFlags.Flash), WeaponIdx = 4,
        };
        var hw = new NetWriter();
        PoseCodec.WriteHitBatch(hw, new[] { h, h }, 0, 2);
        var hb = new List<HitEntry>();
        PoseCodec.ReadHitBatch(Reader(hw), hb);
        Assert.Equal(2, hb.Count);
        Assert.Equal(37.5f, hb[1].Damage);
        Assert.Equal(8f, hb[1].ProbMult);
        Assert.Equal((byte)(HitFlags.Blood | HitFlags.Flash), hb[0].Flags);
        Assert.Equal((sbyte)4, hb[0].WeaponIdx);
    }

    [Fact]
    public void HitResultAndBurnRoundTrip()
    {
        var results = new List<HitResultEntry>
        {
            new HitResultEntry { Id = 3, Damage = 99999f, Tag = 2, Died = true, FreeBullet = true, WeaponIdx = 2 },
            new HitResultEntry { Id = 4, Damage = 12f, Tag = 1, Died = false, FreeBullet = false, WeaponIdx = -1 },
        };
        var w = new NetWriter();
        PoseCodec.WriteHitResultBatch(w, results);
        var back = new List<HitResultEntry>();
        PoseCodec.ReadHitResultBatch(Reader(w), back);
        Assert.True(back[0].Died);
        Assert.True(back[0].FreeBullet);
        Assert.Equal((byte)2, back[0].Tag);
        Assert.Equal((byte)1, back[1].Tag);
        Assert.False(back[1].Died);
        Assert.Equal((sbyte)-1, back[1].WeaponIdx);

        var bw = new NetWriter();
        PoseCodec.WriteBurnBatch(bw, new[] { new BurnEntry { Id = 8, Delta = 0.25f } });
        var bb = new List<BurnEntry>();
        PoseCodec.ReadBurnBatch(Reader(bw), bb);
        Assert.Single(bb);
        Assert.Equal(0.25f, bb[0].Delta);
    }

    // ---- interpolation ----------------------------------------------------------------------------------------

    [Fact]
    public void BufferInterpolatesBetweenTwoSnapshots()
    {
        var b = new PoseBuffer();
        b.Add(1.0f, 0, 0, 0, Quat4.Identity);
        b.Add(1.1f, 10, 0, 0, Quat4.Identity);
        Assert.True(b.TrySample(1.05f, out var s));
        Assert.InRange(s.X, 4.9f, 5.1f);
        Assert.InRange(s.Vx, 99f, 101f);
    }

    [Fact]
    public void BufferExtrapolatesBrieflyThenHolds()
    {
        var b = new PoseBuffer();
        b.Add(1.0f, 0, 0, 0, Quat4.Identity);
        b.Add(1.1f, 1, 0, 0, Quat4.Identity);   // 10 units/s
        Assert.True(b.TrySample(1.2f, out var s));
        Assert.InRange(s.X, 1.99f, 2.01f);
        Assert.True(b.TrySample(5f, out var far));
        Assert.InRange(far.X, 3.49f, 3.51f);    // capped at 0.25 s of extrapolation
    }

    [Fact]
    public void BufferBeforeFirstSampleReturnsFirstAndEmptyReturnsFalse()
    {
        var b = new PoseBuffer();
        Assert.False(b.TrySample(1f, out _));
        b.Add(2f, 7, 8, 9, Quat4.Identity);
        b.Add(2.1f, 8, 8, 9, Quat4.Identity);
        Assert.True(b.TrySample(0f, out var s));
        Assert.Equal(7f, s.X);
    }

    [Fact]
    public void BufferKeepsTimeStrictlyIncreasingAndBounded()
    {
        var b = new PoseBuffer();
        for (int i = 0; i < 40; i++) b.Add(1f, i, 0, 0, Quat4.Identity);   // same timestamp (packet burst)
        Assert.Equal(PoseBuffer.Capacity, b.Count);
        Assert.True(b.TrySample(1f, out var s));
        Assert.True(float.IsFinite(s.X));
        Assert.True(b.TrySample(1.2f, out var late));
        Assert.True(float.IsFinite(late.Vx));
        Assert.Equal(0f, late.Vx);   // a burst has no usable velocity
    }

    [Fact]
    public void BufferRotationTakesTheShortArc()
    {
        var b = new PoseBuffer();
        b.Add(0f, 0, 0, 0, Quat4.FromYawDegrees(350f));
        b.Add(0.1f, 0, 0, 0, Quat4.FromYawDegrees(10f));
        Assert.True(b.TrySample(0.05f, out var s));
        float yaw = s.Rot.YawDegrees();
        Assert.True(yaw > 359f || yaw < 1f, $"yaw {yaw}");
    }

    // ---- send scheduling --------------------------------------------------------------------------------------

    [Fact]
    public void StationaryEntityIsOnlySentOnKeyframe()
    {
        var s = default(SentState);
        var q = Quat4.Identity;
        Assert.True(SentState.NeedsSend(in s, 1, 2, 3, q, false, 0f, 0.02f, 1.5f, 3f));   // never sent
        s.MarkSent(1, 2, 3, q, false, 0f);
        Assert.False(SentState.NeedsSend(in s, 1, 2, 3, q, false, 0.5f, 0.02f, 1.5f, 3f));
        Assert.False(SentState.NeedsSend(in s, 1.01f, 2, 3, q, false, 0.5f, 0.02f, 1.5f, 3f));
        Assert.True(SentState.NeedsSend(in s, 1.05f, 2, 3, q, false, 0.5f, 0.02f, 1.5f, 3f));
        Assert.True(SentState.NeedsSend(in s, 1, 2, 3, Quat4.FromYawDegrees(5), false, 0.5f, 0.02f, 1.5f, 3f));
        Assert.True(SentState.NeedsSend(in s, 1, 2, 3, q, false, 3.1f, 0.02f, 1.5f, 3f));    // keyframe
        Assert.True(SentState.NeedsSend(in s, 1, 2, 3, q, true, 0.5f, 0.02f, 1.5f, 3f));     // state change (died)
    }

    [Fact]
    public void RoundRobinResumesWhereTheBudgetRanOut()
    {
        var rr = new RoundRobin();
        var all = new HashSet<int>();
        for (int tick = 0; tick < 4; tick++)
        {
            var picked = new List<int>();
            rr.Pick(10, 3, i => true, picked);
            Assert.Equal(3, picked.Count);
            foreach (var i in picked) all.Add(i);
        }
        Assert.Equal(10, all.Count);   // 12 slots over 4 ticks cover all ten entities
    }

    [Fact]
    public void RoundRobinSkipsEntitiesThatDoNotWantToSend()
    {
        var rr = new RoundRobin();
        var picked = new List<int>();
        rr.Pick(10, 100, i => i % 2 == 0, picked);
        Assert.Equal(new[] { 0, 2, 4, 6, 8 }, picked);
        picked.Clear();
        rr.Pick(0, 5, i => true, picked);
        Assert.Empty(picked);
    }

    // ---- Part C: client-side burst spreading and resting puppets ----

    [Fact]
    public void WorkQueue_RunsJobsInOrderAndKeepsLaterWorkBehindAnUnfinishedJob()
    {
        var q = new WorkQueue();
        var log = new List<string>();
        int budget = 3;
        q.Enqueue(WorkQueue.ForEachItem(5, i => { log.Add("spawn" + i); budget--; return true; }));
        q.Enqueue(_ => { log.Add("release"); return true; });

        q.Pump(() => budget <= 0);                       // 3 spawns fit the first frame
        Assert.Equal(new[] { "spawn0", "spawn1", "spawn2" }, log);
        Assert.Equal(2, q.Count);

        budget = 3;
        q.Pump(() => budget <= 0);                       // spawn3, spawn4 finish the job, then the release runs after them
        Assert.Equal(new[] { "spawn0", "spawn1", "spawn2", "spawn3", "spawn4", "release" }, log);
        Assert.False(q.HasWork);
    }

    [Fact]
    public void WorkQueue_AlwaysMakesProgressEvenWithNoBudget()
    {
        var q = new WorkQueue();
        int done = 0;
        q.Enqueue(WorkQueue.ForEachItem(4, i => { done++; return true; }));
        for (int frame = 1; frame <= 4; frame++)
        {
            q.Pump(() => true);                          // budget already used up
            Assert.Equal(frame, done);                   // exactly one item per frame, never zero
        }
        Assert.False(q.HasWork);
    }

    [Fact]
    public void WorkQueue_JobThatCannotRunYetBlocksTheQueueWithoutLosingItems()
    {
        var q = new WorkQueue();
        bool ready = false;
        var seen = new List<int>();
        q.Enqueue(WorkQueue.ForEachItem(2, i => { if (!ready) return false; seen.Add(i); return true; }));
        q.Enqueue(_ => { seen.Add(99); return true; });
        q.Pump(() => false);
        Assert.Empty(seen);
        Assert.Equal(2, q.Count);
        ready = true;
        q.Pump(() => false);
        Assert.Equal(new[] { 0, 1, 99 }, seen);
    }

    [Fact]
    public void WorkQueue_ClearDropsEverything()
    {
        var q = new WorkQueue();
        q.Enqueue(_ => true);
        q.Clear();
        Assert.False(q.HasWork);
    }

    [Fact]
    public void PoseBuffer_HeldPoseReportsNoVelocity_SoAStoppedPuppetIsAtRest()
    {
        var b = new PoseBuffer();
        b.Add(1.0f, 0, 0, 0, Quat4.Identity);
        b.Add(1.1f, 1, 0, 0, Quat4.Identity);            // moving at 10 u/s, then the host stops sending
        Assert.True(b.TrySample(1.15f, out var live));   // within the extrapolation window: still moving
        Assert.True(live.Vx > 9f);
        Assert.False(live.IsRestAt(live.X, live.Y, live.Z, live.Rot));

        Assert.True(b.TrySample(5.0f, out var held));    // far past: pose held, no velocity
        Assert.Equal(0f, held.Vx);
        Assert.True(held.IsRestAt(held.X, held.Y, held.Z, held.Rot));
        Assert.False(held.IsRestAt(held.X + 0.001f, held.Y, held.Z, held.Rot));
    }
}
