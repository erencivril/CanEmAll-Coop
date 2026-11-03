using System;
using System.Collections.Generic;
using CanEmAllCoop.Core;
using UnityEngine;

namespace CanEmAllCoop.Enemies;
using CanEmAllCoop.Net;
using Net = CanEmAllCoop.Net.Net;

/// <summary>
/// The delivery truck, turrets and cleaning robots. The host simulates them; clients get their Update / FixedUpdate /
/// coroutines patched off (EnemyPatches) and follow a 10 Hz pose message instead.
/// </summary>
internal static class WorldSync
{
    const float Rate = 10f;
    const float Follow = 14f;   // exponential smoothing rate toward the newest pose

    sealed class TurretTarget
    {
        public bool On;
        public int Bullets;
        public Quat4 Base, Head, Barrel;
        public bool Valid;
    }

    sealed class RobotTarget
    {
        public bool Active;
        public Vector3 Pos;
        public Quat4 Rot;
        public bool Valid;
    }

    static float _next;
    static byte[] _lastPayload;
    static float _lastSent;
    static readonly NetWriter Writer = new(512);

    // client targets
    static bool _truckValid;
    static Vector3 _truckPos;
    static float _doorY;
    static int _facilityHash;
    static float _engineVolume;
    static int _appliedFacilityHash;
    static TurretTarget[] _turrets = Array.Empty<TurretTarget>();
    static RobotTarget[] _robots = Array.Empty<RobotTarget>();
    static int _failures;

    public static long Sent, Received;

    public static void Reset()
    {
        _lastPayload = null;
        _truckValid = false;
        _turrets = Array.Empty<TurretTarget>();
        _robots = Array.Empty<RobotTarget>();
        _appliedFacilityHash = 0;
        _next = 0f;
        _failures = 0;
    }

    // ---- host -------------------------------------------------------------------------------------------------

    public static void HostTick(float now)
    {
        if (now < _next || Net.Peers.Count < 2) return;
        _next = now + 1f / Rate;
        try
        {
            Writer.Reset();
            WritePayload(Writer);
            var bytes = Writer.ToArray();
            bool same = _lastPayload != null && _lastPayload.AsSpan().SequenceEqual(bytes);
            if (same && now - _lastSent < 1f) return;
            _lastPayload = bytes;
            _lastSent = now;
            Net.Broadcast(EnemyMsg.World, w => w.WriteBytes(bytes, 0, bytes.Length), reliable: false);
            Sent++;
        }
        catch (Exception e)
        {
            if (++_failures <= 3) Log.Error("Enemies: world pose build failed", e);
        }
    }

    static void WritePayload(NetWriter w)
    {
        var truck = Truck.Instance;
        bool hasTruck = truck != null && truck.truck != null && truck.door != null;
        w.Write(hasTruck);
        if (hasTruck)
        {
            var p = truck.truck.transform.position;
            PoseCodec.WriteVec(w, p.x, p.y, p.z);
            w.Write(truck.door.localPosition.y);
            int hash = 0;
            try { if (truck.facilityDoor != null) hash = truck.facilityDoor.GetCurrentAnimatorStateInfo(0).shortNameHash; }
            catch (Exception) { }
            w.Write(hash);
            float vol = 0f;
            try { var a = truck.engineAudioSource; if (a != null && a.isPlaying) vol = a.volume; }
            catch (Exception) { }
            w.Write((byte)Mathf.Clamp(Mathf.RoundToInt(vol * 255f), 0, 255));
        }

        var gm = GameManager.Instance;
        var turrets = gm?.turrets;
        int tn = turrets?.Length ?? 0;
        w.Write((byte)Math.Min(tn, 255));
        for (int i = 0; i < tn && i < 255; i++)
        {
            var t = turrets[i];
            bool ok = t != null && t.turretBase != null && t.head != null && t.barrel != null;
            w.Write(ok);
            if (!ok) continue;
            w.Write(t.isOn);
            w.Write((short)Mathf.Clamp(t.bulletCount, short.MinValue, short.MaxValue));
            PoseCodec.WriteQuat(w, Q(t.turretBase.localRotation));
            PoseCodec.WriteQuat(w, Q(t.head.localRotation));
            PoseCodec.WriteQuat(w, Q(t.barrel.localRotation));
        }

        var robots = gm?.robotCleaners;
        int rn = robots?.Length ?? 0;
        w.Write((byte)Math.Min(rn, 255));
        for (int i = 0; i < rn && i < 255; i++)
        {
            var rbt = robots[i];
            bool ok = rbt != null;
            w.Write(ok);
            if (!ok) continue;
            w.Write(rbt.gameObject.activeSelf);
            var tr = rbt.transform;
            var p = tr.position;
            PoseCodec.WriteVec(w, p.x, p.y, p.z);
            PoseCodec.WriteQuat(w, Q(tr.rotation));
        }
    }

    static Quat4 Q(Quaternion q) => new Quat4(q.x, q.y, q.z, q.w);
    static Quaternion U(Quat4 q) => new Quaternion(q.X, q.Y, q.Z, q.W);

    /// <summary>Postfix of Turret.Shoot (host): muzzle flash + sound on the clients.</summary>
    public static void OnTurretShot(Turret t)
    {
        if (!Net.IsHost || Net.Peers.Count < 2) return;
        var turrets = GameManager.Instance?.turrets;
        if (turrets == null) return;
        for (int i = 0; i < turrets.Length; i++)
        {
            var c = turrets[i];
            if (c != null && c.Pointer == t.Pointer)
            {
                byte idx = (byte)i;
                Net.Broadcast(EnemyMsg.TurretShot, w => w.Write(idx), reliable: false);
                return;
            }
        }
    }

    // ---- client -----------------------------------------------------------------------------------------------

    public static void HandleWorld(NetReader r)
    {
        Received++;
        _truckValid = r.ReadBool();
        if (_truckValid)
        {
            PoseCodec.ReadVec(r, out var x, out var y, out var z);
            _truckPos = new Vector3(x, y, z);
            _doorY = r.ReadFloat();
            _facilityHash = r.ReadInt();
            _engineVolume = r.ReadByte() / 255f;
        }

        int tn = r.ReadByte();
        if (_turrets.Length != tn) _turrets = new TurretTarget[tn];
        for (int i = 0; i < tn; i++)
        {
            _turrets[i] ??= new TurretTarget();
            var t = _turrets[i];
            t.Valid = r.ReadBool();
            if (!t.Valid) continue;
            t.On = r.ReadBool();
            t.Bullets = r.ReadShort();
            t.Base = PoseCodec.ReadQuat(r);
            t.Head = PoseCodec.ReadQuat(r);
            t.Barrel = PoseCodec.ReadQuat(r);
        }

        int rn = r.ReadByte();
        if (_robots.Length != rn) _robots = new RobotTarget[rn];
        for (int i = 0; i < rn; i++)
        {
            _robots[i] ??= new RobotTarget();
            var rb = _robots[i];
            rb.Valid = r.ReadBool();
            if (!rb.Valid) continue;
            rb.Active = r.ReadBool();
            PoseCodec.ReadVec(r, out var x, out var y, out var z);
            rb.Pos = new Vector3(x, y, z);
            rb.Rot = PoseCodec.ReadQuat(r);
        }
    }

    public static void HandleTurretShot(NetReader r)
    {
        int idx = r.ReadByte();
        var turrets = GameManager.Instance?.turrets;
        if (turrets == null || idx >= turrets.Length) return;
        var t = turrets[idx];
        if (t == null) return;
        try
        {
            if (t.muzzleFlash != null) t.muzzleFlash.Play();
            t.PlayShootSound();
        }
        catch (Exception e)
        {
            if (++_failures <= 3) Log.Error("Enemies: turret shot feedback failed", e);
        }
    }

    public static void ClientApply(float dt)
    {
        try
        {
            float k = 1f - Mathf.Exp(-Follow * dt);
            ApplyTruck(k);
            ApplyTurrets(k);
            ApplyRobots(k);
        }
        catch (Exception e)
        {
            if (++_failures <= 3) Log.Error("Enemies: world pose apply failed", e);
        }
    }

    static void ApplyTruck(float k)
    {
        if (!_truckValid) return;
        var truck = Truck.Instance;
        if (truck == null || truck.truck == null || truck.door == null) return;

        var tr = truck.truck.transform;
        var rb = truck.truck;
        if (!rb.isKinematic) rb.isKinematic = true;
        var cur = tr.position;
        // Snap on a teleport (day reset), otherwise glide.
        tr.position = (cur - _truckPos).sqrMagnitude > 25f ? _truckPos : Vector3.Lerp(cur, _truckPos, k);

        var dl = truck.door.localPosition;
        dl.y = Mathf.Lerp(dl.y, _doorY, k);
        truck.door.localPosition = dl;

        if (_facilityHash != 0 && _facilityHash != _appliedFacilityHash && truck.facilityDoor != null)
        {
            _appliedFacilityHash = _facilityHash;
            truck.facilityDoor.CrossFadeInFixedTime(_facilityHash, 0.1f);
        }

        var engine = truck.engineAudioSource;
        if (engine != null)
        {
            if (_engineVolume > 0.001f)
            {
                engine.volume = _engineVolume;
                if (!engine.isPlaying) engine.Play();
            }
            else if (engine.isPlaying) engine.Stop();
        }
    }

    static void ApplyTurrets(float k)
    {
        var turrets = GameManager.Instance?.turrets;
        if (turrets == null) return;
        int n = Math.Min(turrets.Length, _turrets.Length);
        for (int i = 0; i < n; i++)
        {
            var t = turrets[i];
            var tg = _turrets[i];
            if (t == null || tg == null || !tg.Valid || t.turretBase == null || t.head == null || t.barrel == null) continue;
            t.turretBase.localRotation = Quaternion.Slerp(t.turretBase.localRotation, U(tg.Base), k);
            t.head.localRotation = Quaternion.Slerp(t.head.localRotation, U(tg.Head), k);
            t.barrel.localRotation = Quaternion.Slerp(t.barrel.localRotation, U(tg.Barrel), k);
            if (t.bulletCount != tg.Bullets)
            {
                t.bulletCount = tg.Bullets;
                t.UpdateBulletCountText();
            }
        }
    }

    static void ApplyRobots(float k)
    {
        var robots = GameManager.Instance?.robotCleaners;
        if (robots == null) return;
        int n = Math.Min(robots.Length, _robots.Length);
        for (int i = 0; i < n; i++)
        {
            var rbt = robots[i];
            var tg = _robots[i];
            if (rbt == null || tg == null || !tg.Valid) continue;
            if (rbt.gameObject.activeSelf != tg.Active) rbt.gameObject.SetActive(tg.Active);
            if (!tg.Active) continue;
            if (rbt.rb != null && !rbt.rb.isKinematic) rbt.rb.isKinematic = true;
            var tr = rbt.transform;
            var cur = tr.position;
            if ((cur - tg.Pos).sqrMagnitude > 25f) tr.SetPositionAndRotation(tg.Pos, U(tg.Rot));
            else tr.SetPositionAndRotation(Vector3.Lerp(cur, tg.Pos, k), Quaternion.Slerp(tr.rotation, U(tg.Rot), k));
        }
    }

    public static string Debug() => $"worldSent={Sent} worldRecv={Received}";
}
