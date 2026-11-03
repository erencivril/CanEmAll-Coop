using UnityEngine;

namespace CanEmAllCoop.Net;

/// <summary>Unity-type helpers for NetWriter / NetReader (extension methods: w.WriteVec3(v), r.ReadVec3()).</summary>
public static class NetUnity
{
    /// <summary>Default half-extent of the quantized world box (1.56 cm resolution at 16 bits per axis).</summary>
    public const float DefaultRange = 512f;

    public static void WriteVec3(this NetWriter w, Vector3 v)
    {
        w.Write(v.x); w.Write(v.y); w.Write(v.z);
    }

    public static Vector3 ReadVec3(this NetReader r) => new Vector3(r.ReadFloat(), r.ReadFloat(), r.ReadFloat());

    public static void WriteQuat(this NetWriter w, Quaternion q)
    {
        w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w);
    }

    public static Quaternion ReadQuat(this NetReader r) => new Quaternion(r.ReadFloat(), r.ReadFloat(), r.ReadFloat(), r.ReadFloat());

    /// <summary>3 x ushort, each axis mapped from [-range, range]. 6 bytes.</summary>
    public static void WriteVec3Q(this NetWriter w, Vector3 v, float range = DefaultRange)
    {
        w.WriteQuantized(v.x, -range, range);
        w.WriteQuantized(v.y, -range, range);
        w.WriteQuantized(v.z, -range, range);
    }

    public static Vector3 ReadVec3Q(this NetReader r, float range = DefaultRange) =>
        new Vector3(r.ReadQuantized(-range, range), r.ReadQuantized(-range, range), r.ReadQuantized(-range, range));

    /// <summary>Angle in degrees (any range) as one ushort covering [0,360). Resolution 0.0055 degrees.</summary>
    public static void WriteAngle16(this NetWriter w, float degrees)
    {
        float d = degrees % 360f;
        if (d < 0f) d += 360f;
        w.Write((ushort)(d / 360f * 65536f) );
    }

    /// <summary>Returns degrees in [0,360).</summary>
    public static float ReadAngle16(this NetReader r) => r.ReadUShort() / 65536f * 360f;

    /// <summary>Yaw [0,360) and pitch [-90,90] as two ushorts (4 bytes).</summary>
    public static void WriteYawPitch(this NetWriter w, float yawDegrees, float pitchDegrees)
    {
        w.WriteAngle16(yawDegrees);
        w.WriteQuantized(Mathf.Clamp(pitchDegrees, -90f, 90f), -90f, 90f);
    }

    public static void ReadYawPitch(this NetReader r, out float yawDegrees, out float pitchDegrees)
    {
        yawDegrees = r.ReadAngle16();
        pitchDegrees = r.ReadQuantized(-90f, 90f);
    }

    /// <summary>Writes only yaw (euler Y) and pitch (euler X) of a rotation, 4 bytes. Roll is dropped.</summary>
    public static void WriteQuatYawPitch(this NetWriter w, Quaternion q)
    {
        var e = q.eulerAngles;
        float pitch = e.x > 180f ? e.x - 360f : e.x;
        w.WriteYawPitch(e.y, pitch);
    }

    public static Quaternion ReadQuatYawPitch(this NetReader r)
    {
        r.ReadYawPitch(out var yaw, out var pitch);
        return Quaternion.Euler(pitch, yaw, 0f);
    }
}
