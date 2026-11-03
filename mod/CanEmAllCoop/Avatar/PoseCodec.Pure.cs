using System;

namespace CanEmAllCoop.Avatar;
using CanEmAllCoop.Net;

/// <summary>
/// Pose wire format (message 500, unreliable): time u32 ms, pos 3 x f32, yaw u16 (360 deg), pitch i16 (+-90 deg),
/// velocity 3 x i16 (1/256 m/s), flags u8, weapon u8, fireSeq u8, heldId u32 = 33 bytes.
/// </summary>
public static class PoseCodec
{
    public const ushort MsgPose = 500;
    public const float VelScale = 256f;

    public static void Write(NetWriter w, in AvPose p)
    {
        w.Write((uint)(long)Math.Round(p.Time * 1000.0));
        w.Write(p.X); w.Write(p.Y); w.Write(p.Z);
        w.Write((ushort)Math.Round(AngleMath.Wrap360(p.Yaw) / 360f * 65536f) );
        w.Write((short)Math.Round(Math.Clamp(p.Pitch, -90f, 90f) / 90f * 32767f));
        w.Write(Vel(p.Vx)); w.Write(Vel(p.Vy)); w.Write(Vel(p.Vz));
        w.Write(p.Flags);
        w.Write(p.Weapon);
        w.Write(p.FireSeq);
        w.Write(p.HeldId);
    }

    public static AvPose Read(NetReader r)
    {
        var p = new AvPose();
        p.Time = r.ReadUInt() / 1000.0;
        p.X = r.ReadFloat(); p.Y = r.ReadFloat(); p.Z = r.ReadFloat();
        p.Yaw = r.ReadUShort() / 65536f * 360f;
        p.Pitch = r.ReadShort() / 32767f * 90f;
        p.Vx = r.ReadShort() / VelScale; p.Vy = r.ReadShort() / VelScale; p.Vz = r.ReadShort() / VelScale;
        p.Flags = r.ReadByte();
        p.Weapon = r.ReadByte();
        p.FireSeq = r.ReadByte();
        p.HeldId = r.ReadUInt();
        return p;
    }

    static short Vel(float v) => (short)Math.Clamp((int)Math.Round(v * VelScale), short.MinValue, short.MaxValue);
}
