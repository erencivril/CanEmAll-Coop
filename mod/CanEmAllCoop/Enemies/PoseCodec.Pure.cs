using System;
using System.Collections.Generic;
using CanEmAllCoop.Net;

namespace CanEmAllCoop.Enemies;

public struct PoseEntry
{
    public uint Id;
    public float X, Y, Z;
    public Quat4 Rot;
    public bool Dead;
}

public struct SpawnEntry
{
    public uint Id;
    public ushort Prefab;     // index into Truck.allEnemyPrefabs
    public bool Shiny;
    public bool Dead;
    public byte State;        // Enemy.State: 0 waiting, 1 exiting, 2 roaming
    public byte HealthPct;    // 0..100
    public float X, Y, Z;
    public Quat4 Rot;
}

public struct HitEntry
{
    public uint Id;
    public float Damage;
    public float DirX, DirY, DirZ;
    public float PosX, PosY, PosZ;
    public float RadiusMult;
    public float ProbMult;
    public byte Flags;        // HitFlags
    public sbyte WeaponIdx;   // Inventory.currentWeaponIndex, -1 unknown
}

public static class HitFlags
{
    public const byte Blood = 1, Text = 2, Scream = 4, Flash = 8;
}

public struct HitResultEntry
{
    public uint Id;
    public float Damage;
    public byte Tag;          // 0 normal, 1 crit, 2 instant kill
    public bool Died;
    public bool FreeBullet;
    public sbyte WeaponIdx;
}

public struct BurnEntry
{
    public uint Id;
    public float Delta;
}

public struct DeathEntry
{
    public uint Id;
    public float X, Y, Z;
    public Quat4 Rot;
    public float Vx, Vy, Vz;
    public float Wx, Wy, Wz;
}

/// <summary>Wire formats of the enemy module. Everything here is pure so it is unit-tested.</summary>
public static class PoseCodec
{
    /// <summary>Half extent of the quantized world box (positions are clamped into [-Range, Range]).</summary>
    public const float Range = 512f;

    // ---- primitives -------------------------------------------------------------------------------------------

    public static void WriteVarUInt(NetWriter w, uint v)
    {
        while (v >= 0x80) { w.Write((byte)(v | 0x80)); v >>= 7; }
        w.Write((byte)v);
    }

    public static uint ReadVarUInt(NetReader r)
    {
        uint v = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            byte b = r.ReadByte();
            v |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return v;
        }
        throw new System.IO.InvalidDataException("varint too long");
    }

    public static void WritePos(NetWriter w, float x, float y, float z)
    {
        w.WriteQuantized(x, -Range, Range);
        w.WriteQuantized(y, -Range, Range);
        w.WriteQuantized(z, -Range, Range);
    }

    public static void ReadPos(NetReader r, out float x, out float y, out float z)
    {
        x = r.ReadQuantized(-Range, Range);
        y = r.ReadQuantized(-Range, Range);
        z = r.ReadQuantized(-Range, Range);
    }

    public static void WriteVec(NetWriter w, float x, float y, float z) { w.Write(x); w.Write(y); w.Write(z); }

    public static void ReadVec(NetReader r, out float x, out float y, out float z)
    {
        x = r.ReadFloat(); y = r.ReadFloat(); z = r.ReadFloat();
    }

    const float Q3Scale = 32767f / 0.70710678f;

    /// <summary>Smallest-three quaternion: 1 byte (index of the dropped component) + 3 x int16 = 7 bytes.</summary>
    public static void WriteQuat(NetWriter w, Quat4 q)
    {
        q = q.Normalized();
        float[] c = { q.X, q.Y, q.Z, q.W };
        int big = 0;
        for (int i = 1; i < 4; i++) if (MathF.Abs(c[i]) > MathF.Abs(c[big])) big = i;
        float sign = c[big] < 0f ? -1f : 1f;
        w.Write((byte)big);
        for (int i = 0; i < 4; i++)
        {
            if (i == big) continue;
            float v = Math.Clamp(c[i] * sign * Q3Scale, -32767f, 32767f);
            w.Write((short)MathF.Round(v));
        }
    }

    public static Quat4 ReadQuat(NetReader r)
    {
        int big = r.ReadByte() & 3;
        float[] c = new float[4];
        float sum = 0f;
        for (int i = 0; i < 4; i++)
        {
            if (i == big) continue;
            c[i] = r.ReadShort() / Q3Scale;
            sum += c[i] * c[i];
        }
        c[big] = MathF.Sqrt(MathF.Max(0f, 1f - sum));
        return new Quat4(c[0], c[1], c[2], c[3]).Normalized();
    }

    // Local copies of NetUnity.WriteAngle16 (that class needs UnityEngine).
    static void WriteYaw16(NetWriter w, float degrees)
    {
        float d = degrees % 360f;
        if (d < 0f) d += 360f;
        w.Write((ushort)(d / 360f * 65536f));
    }

    static float ReadYaw16(NetReader r) => r.ReadUShort() / 65536f * 360f;

    // ---- pose batches (EnemyTransforms) -----------------------------------------------------------------------

    const byte PoseDead = 1, PoseFullRot = 2;

    public static void WritePose(NetWriter w, in PoseEntry e)
    {
        WriteVarUInt(w, e.Id);
        bool full = !e.Rot.IsUpright;
        w.Write((byte)((e.Dead ? PoseDead : 0) | (full ? PoseFullRot : 0)));
        WritePos(w, e.X, e.Y, e.Z);
        if (full) WriteQuat(w, e.Rot);
        else WriteYaw16(w, e.Rot.YawDegrees());
    }

    public static PoseEntry ReadPose(NetReader r)
    {
        var e = new PoseEntry { Id = ReadVarUInt(r) };
        byte f = r.ReadByte();
        e.Dead = (f & PoseDead) != 0;
        ReadPos(r, out e.X, out e.Y, out e.Z);
        e.Rot = (f & PoseFullRot) != 0 ? ReadQuat(r) : Quat4.FromYawDegrees(ReadYaw16(r));
        return e;
    }

    public static void WritePoseBatch(NetWriter w, IList<PoseEntry> list, int start, int count)
    {
        w.Write((ushort)count);
        for (int i = 0; i < count; i++)
        {
            var e = list[start + i];
            WritePose(w, in e);
        }
    }

    public static void ReadPoseBatch(NetReader r, List<PoseEntry> into)
    {
        int n = r.ReadUShort();
        for (int i = 0; i < n; i++) into.Add(ReadPose(r));
    }

    // ---- spawn ------------------------------------------------------------------------------------------------

    const byte SpawnShiny = 1, SpawnDead = 2;

    public static void WriteSpawn(NetWriter w, in SpawnEntry e)
    {
        WriteVarUInt(w, e.Id);
        w.Write(e.Prefab);
        w.Write((byte)((e.Shiny ? SpawnShiny : 0) | (e.Dead ? SpawnDead : 0)));
        w.Write(e.State);
        w.Write(e.HealthPct);
        WritePos(w, e.X, e.Y, e.Z);
        WriteQuat(w, e.Rot);
    }

    public static SpawnEntry ReadSpawn(NetReader r)
    {
        var e = new SpawnEntry { Id = ReadVarUInt(r), Prefab = r.ReadUShort() };
        byte f = r.ReadByte();
        e.Shiny = (f & SpawnShiny) != 0;
        e.Dead = (f & SpawnDead) != 0;
        e.State = r.ReadByte();
        e.HealthPct = r.ReadByte();
        ReadPos(r, out e.X, out e.Y, out e.Z);
        e.Rot = ReadQuat(r);
        return e;
    }

    public static void WriteSpawnBatch(NetWriter w, IList<SpawnEntry> list, int start, int count)
    {
        w.Write((ushort)count);
        for (int i = 0; i < count; i++) { var e = list[start + i]; WriteSpawn(w, in e); }
    }

    public static void ReadSpawnBatch(NetReader r, List<SpawnEntry> into)
    {
        int n = r.ReadUShort();
        for (int i = 0; i < n; i++) into.Add(ReadSpawn(r));
    }

    // ---- id lists (EnemyReturn, EnemyDamaged, CleanAll) -------------------------------------------------------

    public static void WriteIds(NetWriter w, IList<uint> ids, int start, int count)
    {
        w.Write((ushort)count);
        for (int i = 0; i < count; i++) WriteVarUInt(w, ids[start + i]);
    }

    public static void ReadIds(NetReader r, List<uint> into)
    {
        int n = r.ReadUShort();
        for (int i = 0; i < n; i++) into.Add(ReadVarUInt(r));
    }

    // ---- release ----------------------------------------------------------------------------------------------

    public static void WriteRelease(NetWriter w, float dx, float dy, float dz, float speed, float roam, IList<uint> ids)
    {
        WriteVec(w, dx, dy, dz);
        w.Write(speed);
        w.Write(roam);
        WriteIds(w, ids, 0, ids.Count);
    }

    public static void ReadRelease(NetReader r, out float dx, out float dy, out float dz, out float speed, out float roam, List<uint> ids)
    {
        ReadVec(r, out dx, out dy, out dz);
        speed = r.ReadFloat();
        roam = r.ReadFloat();
        ReadIds(r, ids);
    }

    // ---- death ------------------------------------------------------------------------------------------------

    public static void WriteDeath(NetWriter w, in DeathEntry e)
    {
        WriteVarUInt(w, e.Id);
        WritePos(w, e.X, e.Y, e.Z);
        WriteQuat(w, e.Rot);
        WriteVec(w, e.Vx, e.Vy, e.Vz);
        WriteVec(w, e.Wx, e.Wy, e.Wz);
    }

    public static DeathEntry ReadDeath(NetReader r)
    {
        var e = new DeathEntry { Id = ReadVarUInt(r) };
        ReadPos(r, out e.X, out e.Y, out e.Z);
        e.Rot = ReadQuat(r);
        ReadVec(r, out e.Vx, out e.Vy, out e.Vz);
        ReadVec(r, out e.Wx, out e.Wy, out e.Wz);
        return e;
    }

    // ---- hits -------------------------------------------------------------------------------------------------

    public static void WriteHit(NetWriter w, in HitEntry e)
    {
        WriteVarUInt(w, e.Id);
        w.Write(e.Damage);
        WriteVec(w, e.DirX, e.DirY, e.DirZ);
        WriteVec(w, e.PosX, e.PosY, e.PosZ);
        w.Write(e.RadiusMult);
        w.Write(e.ProbMult);
        w.Write(e.Flags);
        w.Write(e.WeaponIdx);
    }

    public static HitEntry ReadHit(NetReader r)
    {
        var e = new HitEntry { Id = ReadVarUInt(r), Damage = r.ReadFloat() };
        ReadVec(r, out e.DirX, out e.DirY, out e.DirZ);
        ReadVec(r, out e.PosX, out e.PosY, out e.PosZ);
        e.RadiusMult = r.ReadFloat();
        e.ProbMult = r.ReadFloat();
        e.Flags = r.ReadByte();
        e.WeaponIdx = r.ReadSByte();
        return e;
    }

    public static void WriteHitBatch(NetWriter w, IList<HitEntry> list, int start, int count)
    {
        w.Write((ushort)count);
        for (int i = 0; i < count; i++) { var e = list[start + i]; WriteHit(w, in e); }
    }

    public static void ReadHitBatch(NetReader r, List<HitEntry> into)
    {
        int n = r.ReadUShort();
        for (int i = 0; i < n; i++) into.Add(ReadHit(r));
    }

    public static void WriteHitResult(NetWriter w, in HitResultEntry e)
    {
        WriteVarUInt(w, e.Id);
        w.Write(e.Damage);
        w.Write((byte)(e.Tag | (e.Died ? 0x10 : 0) | (e.FreeBullet ? 0x20 : 0)));
        w.Write(e.WeaponIdx);
    }

    public static HitResultEntry ReadHitResult(NetReader r)
    {
        var e = new HitResultEntry { Id = ReadVarUInt(r), Damage = r.ReadFloat() };
        byte f = r.ReadByte();
        e.Tag = (byte)(f & 3);
        e.Died = (f & 0x10) != 0;
        e.FreeBullet = (f & 0x20) != 0;
        e.WeaponIdx = r.ReadSByte();
        return e;
    }

    public static void WriteHitResultBatch(NetWriter w, IList<HitResultEntry> list)
    {
        w.Write((ushort)list.Count);
        for (int i = 0; i < list.Count; i++) { var e = list[i]; WriteHitResult(w, in e); }
    }

    public static void ReadHitResultBatch(NetReader r, List<HitResultEntry> into)
    {
        int n = r.ReadUShort();
        for (int i = 0; i < n; i++) into.Add(ReadHitResult(r));
    }

    public static void WriteBurnBatch(NetWriter w, IList<BurnEntry> list)
    {
        w.Write((ushort)list.Count);
        for (int i = 0; i < list.Count; i++) { WriteVarUInt(w, list[i].Id); w.Write(list[i].Delta); }
    }

    public static void ReadBurnBatch(NetReader r, List<BurnEntry> into)
    {
        int n = r.ReadUShort();
        for (int i = 0; i < n; i++) into.Add(new BurnEntry { Id = ReadVarUInt(r), Delta = r.ReadFloat() });
    }
}
