using System;

namespace CanEmAllCoop.Enemies;

/// <summary>Unity-free quaternion (x,y,z,w) with just what pose sync needs.</summary>
public readonly struct Quat4
{
    public readonly float X, Y, Z, W;

    public Quat4(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }

    public static Quat4 Identity => new Quat4(0, 0, 0, 1);

    public static Quat4 FromYawDegrees(float yawDeg)
    {
        float h = yawDeg * 0.5f * (MathF.PI / 180f);
        return new Quat4(0, MathF.Sin(h), 0, MathF.Cos(h));
    }

    /// <summary>Rotation about the Y axis in degrees [0,360) (valid for upright rotations).</summary>
    public float YawDegrees()
    {
        float yaw = MathF.Atan2(2f * (W * Y + X * Z), 1f - 2f * (Y * Y + X * X)) * (180f / MathF.PI);
        return yaw < 0f ? yaw + 360f : yaw;
    }

    /// <summary>True when the rotation is (almost) a pure yaw: pitch/roll below ~2 degrees.</summary>
    public bool IsUpright => MathF.Abs(X) < 0.0175f && MathF.Abs(Z) < 0.0175f;

    public Quat4 Normalized()
    {
        float m = MathF.Sqrt(X * X + Y * Y + Z * Z + W * W);
        if (m < 1e-6f) return Identity;
        float i = 1f / m;
        return new Quat4(X * i, Y * i, Z * i, W * i);
    }

    public float Dot(Quat4 o) => X * o.X + Y * o.Y + Z * o.Z + W * o.W;

    /// <summary>Angle between two rotations in degrees.</summary>
    public float AngleTo(Quat4 o)
    {
        float d = MathF.Min(1f, MathF.Abs(Dot(o)));
        return 2f * MathF.Acos(d) * (180f / MathF.PI);
    }

    /// <summary>Normalised lerp along the short arc (good enough at 12 Hz snapshots).</summary>
    public static Quat4 Nlerp(Quat4 a, Quat4 b, float t)
    {
        if (a.Dot(b) < 0f) b = new Quat4(-b.X, -b.Y, -b.Z, -b.W);
        return new Quat4(
            a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t,
            a.Z + (b.Z - a.Z) * t, a.W + (b.W - a.W) * t).Normalized();
    }
}
