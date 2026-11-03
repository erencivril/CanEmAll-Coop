using CanEmAllCoop.Avatar;
using CanEmAllCoop.Net;
using Xunit;

namespace CanEmAllCoop.Tests;

public class AvatarTests
{
    static AvPose P(double t, float x, float z = 0f, float yaw = 0f, float vx = 0f) =>
        new AvPose { Time = t, X = x, Z = z, Yaw = yaw, Vx = vx };

    [Fact]
    public void InterpolatesBetweenSamples()
    {
        var b = new PoseBuffer();
        b.Add(P(1.0, 0f));
        b.Add(P(1.1, 10f));
        Assert.True(b.Sample(1.05, out var r));
        Assert.Equal(5f, r.X, 3);
        Assert.True(b.Sample(1.0, out r));
        Assert.Equal(0f, r.X, 3);
    }

    [Fact]
    public void BeforeFirstSampleHoldsFirst()
    {
        var b = new PoseBuffer();
        b.Add(P(1.0, 3f));
        b.Add(P(1.1, 4f));
        Assert.True(b.Sample(0.2, out var r));
        Assert.Equal(3f, r.X);
    }

    [Fact]
    public void EmptyBufferHasNoSample()
    {
        Assert.False(new PoseBuffer().Sample(1.0, out _));
    }

    [Fact]
    public void ExtrapolatesWithVelocityThenClamps()
    {
        var b = new PoseBuffer();
        b.Add(P(1.0, 0f, vx: 4f));
        Assert.True(b.Sample(1.1, out var r));
        Assert.Equal(0.4f, r.X, 3);
        Assert.True(b.Sample(5.0, out r)); // far past: clamped to MaxExtrapolation (0.25 s)
        Assert.Equal(1.0f, r.X, 3);
    }

    [Fact]
    public void YawTakesTheShortWayAround()
    {
        var b = new PoseBuffer();
        b.Add(P(0.0, 0f, yaw: 350f));
        b.Add(P(1.0, 0f, yaw: 10f));
        Assert.True(b.Sample(0.5, out var r));
        Assert.Equal(0f, AngleMath.DeltaAngle(r.Yaw, 0f), 2);
    }

    [Fact]
    public void TeleportIsNotInterpolated()
    {
        var b = new PoseBuffer();
        b.Add(P(0.0, 0f));
        b.Add(P(0.1, 100f));
        Assert.True(b.Sample(0.05, out var r));
        Assert.Equal(0f, r.X);
        Assert.True(b.Sample(0.1, out r));
        Assert.Equal(100f, r.X);
    }

    [Fact]
    public void DiscreteStateSwitchesWhenNewerSampleIsReached()
    {
        var b = new PoseBuffer();
        b.Add(new AvPose { Time = 0.0, Weapon = 1, FireSeq = 5 });
        b.Add(new AvPose { Time = 0.1, Weapon = 2, FireSeq = 6 });
        b.Sample(0.05, out var r);
        Assert.Equal(1, r.Weapon);
        Assert.Equal(5, r.FireSeq);
        b.Sample(0.2, out r);
        Assert.Equal(2, r.Weapon);
        Assert.Equal(6, r.FireSeq);
    }

    [Fact]
    public void OutOfOrderAndDuplicateSamplesAreDropped()
    {
        var b = new PoseBuffer();
        Assert.True(b.Add(P(1.0, 0f)));
        Assert.False(b.Add(P(1.0, 1f)));
        Assert.False(b.Add(P(0.9, 1f)));
        Assert.Equal(1, b.Count);
    }

    [Fact]
    public void BufferIsBounded()
    {
        var b = new PoseBuffer();
        for (int i = 0; i < 100; i++) b.Add(P(i * 0.05, i));
        Assert.Equal(PoseBuffer.Capacity, b.Count);
        Assert.Equal(99f, b.Newest.X);
    }

    [Fact]
    public void ClockSyncTracksMinimumLatency()
    {
        var c = new ClockSync();
        c.Observe(10.080, 5.000); // 5.080
        c.Observe(10.130, 5.100); // 5.030 (lower => taken)
        c.Observe(10.280, 5.200); // 5.080 (higher => leaks slowly)
        Assert.True(c.Ready);
        Assert.InRange(c.ToSender(10.030), 4.99, 5.01 + 0.0005); // offset ~5.03
    }

    [Fact]
    public void CodecRoundTrips()
    {
        var p = new AvPose
        {
            Time = 1234.567, X = 12.5f, Y = -3.25f, Z = 100.125f, Yaw = 271.5f, Pitch = -42.5f,
            Vx = 3.5f, Vy = -9.0f, Vz = 0.25f, Flags = (byte)(PoseFlags.Grounded | PoseFlags.Down),
            Weapon = WeaponCodes.Shotgun, HeldId = 77u, FireSeq = 250,
        };
        var w = new NetWriter();
        PoseCodec.Write(w, p);
        Assert.Equal(33, w.Length);
        var q = PoseCodec.Read(new NetReader(w.ToArray()));
        Assert.Equal(p.Time, q.Time, 3);
        Assert.Equal(p.X, q.X); Assert.Equal(p.Y, q.Y); Assert.Equal(p.Z, q.Z);
        Assert.Equal(p.Yaw, q.Yaw, 1);
        Assert.Equal(p.Pitch, q.Pitch, 1);
        Assert.Equal(p.Vx, q.Vx, 2); Assert.Equal(p.Vy, q.Vy, 2); Assert.Equal(p.Vz, q.Vz, 2);
        Assert.Equal(p.Flags, q.Flags);
        Assert.Equal(p.Weapon, q.Weapon);
        Assert.Equal(p.HeldId, q.HeldId);
        Assert.Equal(p.FireSeq, q.FireSeq);
    }

    [Fact]
    public void CodecClampsExtremes()
    {
        var p = new AvPose { Pitch = 500f, Vx = 1000f, Vy = -1000f, Yaw = -10f };
        var w = new NetWriter();
        PoseCodec.Write(w, p);
        var q = PoseCodec.Read(new NetReader(w.ToArray()));
        Assert.Equal(90f, q.Pitch, 1);
        Assert.Equal(127.99f, q.Vx, 1);
        Assert.Equal(-128f, q.Vy, 1);
        Assert.Equal(350f, q.Yaw, 1);
    }

    [Fact]
    public void SendGateHonoursRateAndUrgency()
    {
        var g = new PoseSendGate();
        Assert.True(g.Due(0.00, 20, false));
        Assert.False(g.Due(0.02, 20, false));
        Assert.False(g.Due(0.02, 20, true));   // urgent still needs 30 ms
        Assert.True(g.Due(0.04, 20, true));
        Assert.False(g.Due(0.06, 20, false));
        Assert.True(g.Due(0.10, 20, false));
        Assert.False(g.Due(5.0, 0, true));     // 0 Hz = off
    }

    [Fact]
    public void FireSeqDeltaWrapsAndCaps()
    {
        Assert.Equal(0, FireSeq.Delta(5, 5));
        Assert.Equal(1, FireSeq.Delta(5, 6));
        Assert.Equal(1, FireSeq.Delta(255, 0));
        Assert.Equal(3, FireSeq.Delta(0, 50));
        Assert.Equal(0, FireSeq.Delta(10, 5));
    }

    [Fact]
    public void AngleMathWorks()
    {
        Assert.Equal(350f, CanEmAllCoop.Avatar.AngleMath.Wrap360(-10f), 3);
        Assert.Equal(-20f, CanEmAllCoop.Avatar.AngleMath.DeltaAngle(10f, 350f), 3);
        Assert.Equal(20f, CanEmAllCoop.Avatar.AngleMath.DeltaAngle(350f, 10f), 3);
    }

    [Fact]
    public void TintsAreDistinctPerPeerIndex()
    {
        var seen = new System.Collections.Generic.HashSet<(float, float, float)>();
        for (int i = 0; i < 4; i++)
        {
            var t = AvatarStyle.Tint(i);
            Assert.True(seen.Add((t.R, t.G, t.B)));
        }
        Assert.Equal(AvatarStyle.Tint(0).R, AvatarStyle.Tint(4).R);
        Assert.Equal(AvatarStyle.Tint(0).R, AvatarStyle.Tint(-4).R);
    }

    [Fact]
    public void BlendMovesTowardsTarget()
    {
        Assert.Equal(0.5f, AvatarStyle.Blend(0f, true, 0.1f, 0.2f), 3);
        Assert.Equal(1f, AvatarStyle.Blend(0.9f, true, 0.1f, 0.2f), 3);
        Assert.Equal(0f, AvatarStyle.Blend(0.1f, false, 0.1f, 0.2f), 3);
        Assert.Equal(1f, AvatarStyle.Blend(0f, true, 0.1f, 0f), 3);
    }

    [Fact]
    public void TagText()
    {
        Assert.Equal("Bob\nPistol", AvatarStyle.TagText("Bob", WeaponCodes.Pistol, false));
        Assert.Equal("Bob (DOWN)", AvatarStyle.TagText("Bob", WeaponCodes.Pistol, true));
        Assert.StartsWith("Player", AvatarStyle.TagText(null, WeaponCodes.Fists, false));
        Assert.Equal("?", WeaponCodes.Label(WeaponCodes.Unknown));
    }
}
