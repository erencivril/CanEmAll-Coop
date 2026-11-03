using System;
using System.Collections.Generic;

namespace CanEmAllCoop.Avatar;

// Pure (Unity-free) avatar math: pose snapshot, interpolation buffer, clock offset estimation, send gating.

/// <summary>Bit flags carried in <see cref="AvPose.Flags"/>.</summary>
public static class PoseFlags
{
    public const byte Grounded = 1;
    public const byte Running = 2;
    public const byte Vacuum = 4;   // vacuum is sucking
    public const byte Firing = 8;   // continuous weapon (flamethrower, chainsaw) is active
    public const byte Down = 16;

    public static bool Has(byte flags, byte f) => (flags & f) != 0;
}

/// <summary>One pose of a remote player. Time is the SENDER's clock in seconds.</summary>
public struct AvPose
{
    public double Time;
    public float X, Y, Z;
    public float Yaw, Pitch;        // degrees; yaw = Player.rotY, pitch = Player.rotX (positive = looking down)
    public float Vx, Vy, Vz;
    public byte Flags;
    public byte Weapon;             // WeaponCodes
    public uint HeldId;             // NetIds id of the held body/can/crate, 0 = nothing
    public byte FireSeq;            // increments once per shot/swing (wraps)

    public bool Has(byte flag) => (Flags & flag) != 0;
}

public static class AngleMath
{
    public static float Wrap360(float a)
    {
        a %= 360f;
        return a < 0f ? a + 360f : a;
    }

    /// <summary>Signed shortest rotation from a to b in (-180, 180].</summary>
    public static float DeltaAngle(float a, float b)
    {
        float d = Wrap360(b - a);
        return d > 180f ? d - 360f : d;
    }

    public static float LerpAngle(float a, float b, float t) => Wrap360(a + DeltaAngle(a, b) * t);
}

/// <summary>
/// Time-ordered ring of received poses. <see cref="Sample"/> interpolates between the two samples around a render
/// time (normally "now - 100 ms") and extrapolates with the velocity for a short while when the buffer runs dry.
/// </summary>
public sealed class PoseBuffer
{
    public const int Capacity = 32;

    readonly List<AvPose> _s = new(Capacity + 1);

    /// <summary>Seconds of velocity extrapolation allowed past the newest sample.</summary>
    public double MaxExtrapolation = 0.25;

    /// <summary>Position jumps larger than this are teleports (respawn, elevator): no sliding across the map.</summary>
    public float TeleportDistance = 15f;

    public int Count => _s.Count;
    public bool IsEmpty => _s.Count == 0;
    public AvPose Newest => _s[_s.Count - 1];

    public void Clear() => _s.Clear();

    /// <summary>Appends a sample. Duplicates / out-of-order samples are dropped (returns false).</summary>
    public bool Add(in AvPose p)
    {
        if (_s.Count > 0 && p.Time <= _s[_s.Count - 1].Time) return false;
        _s.Add(p);
        if (_s.Count > Capacity) _s.RemoveAt(0);
        return true;
    }

    public bool Sample(double t, out AvPose result)
    {
        result = default;
        int n = _s.Count;
        if (n == 0) return false;

        if (t <= _s[0].Time)
        {
            result = _s[0];
            return true;
        }

        for (int i = n - 2; i >= 0; i--)
        {
            var a = _s[i];
            if (a.Time > t) continue;
            var b = _s[i + 1];
            result = Between(a, b, t);
            return true;
        }

        // t is at or past the newest sample
        var last = _s[n - 1];
        double dt = Math.Min(t - last.Time, MaxExtrapolation);
        result = last;
        result.Time = t;
        result.X += (float)(last.Vx * dt);
        result.Y += (float)(last.Vy * dt);
        result.Z += (float)(last.Vz * dt);
        return true;
    }

    AvPose Between(in AvPose a, in AvPose b, double t)
    {
        double span = b.Time - a.Time;
        float u = span <= 1e-6 ? 1f : (float)Math.Clamp((t - a.Time) / span, 0.0, 1.0);

        // discrete state (flags, weapon, held, fire seq) comes from the older sample until the newer one is reached
        var r = a;
        r.Time = t;

        float dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
        if (dx * dx + dy * dy + dz * dz > TeleportDistance * TeleportDistance)
        {
            if (u >= 1f) r = b;
            r.Time = t;
            return r;
        }

        r.X = a.X + dx * u;
        r.Y = a.Y + dy * u;
        r.Z = a.Z + dz * u;
        r.Yaw = AngleMath.LerpAngle(a.Yaw, b.Yaw, u);
        r.Pitch = a.Pitch + (b.Pitch - a.Pitch) * u;
        r.Vx = a.Vx + (b.Vx - a.Vx) * u;
        r.Vy = a.Vy + (b.Vy - a.Vy) * u;
        r.Vz = a.Vz + (b.Vz - a.Vz) * u;
        if (u >= 1f) { r.Flags = b.Flags; r.Weapon = b.Weapon; r.HeldId = b.HeldId; r.FireSeq = b.FireSeq; }
        return r;
    }
}

/// <summary>
/// Maps the sender's clock onto the local clock. offset = localArrival - senderTime tracks the MINIMUM seen (the
/// lowest-latency packet) and leaks slowly upwards so clock drift and route changes are followed.
/// </summary>
public sealed class ClockSync
{
    public double LeakPerSample = 0.01;

    double _offset;
    bool _init;

    public bool Ready => _init;

    public void Observe(double localArrival, double senderTime)
    {
        double d = localArrival - senderTime;
        if (!_init) { _offset = d; _init = true; }
        else if (d < _offset) _offset = d;
        else _offset += (d - _offset) * LeakPerSample;
    }

    /// <summary>The sender's clock value that corresponds to the local time <paramref name="localNow"/>.</summary>
    public double ToSender(double localNow) => localNow - _offset;

    public void Reset() { _init = false; _offset = 0; }
}

/// <summary>Rate limiter for the pose stream; an urgent change (fire, weapon swap, down) may go out early.</summary>
public sealed class PoseSendGate
{
    public const double UrgentMinInterval = 0.03;

    double _last = double.NegativeInfinity;

    public bool Due(double now, int hz, bool urgent)
    {
        if (hz <= 0) return false;
        double interval = 1.0 / hz;
        double need = urgent ? Math.Min(interval, UrgentMinInterval) : interval;
        if (now - _last < need) return false;
        _last = now;
        return true;
    }

    public void Reset() => _last = double.NegativeInfinity;
}

/// <summary>Fire sequence counter helpers (byte, wraps).</summary>
public static class FireSeq
{
    /// <summary>Shots between two counter values (0..3; capped so a long gap does not machine-gun).</summary>
    public static int Delta(byte previous, byte current)
    {
        int d = (byte)(current - previous);
        if (d > 100) return 0; // went backwards (reordered / restarted): ignore
        return Math.Min(d, 3);
    }
}

public static class WeaponCodes
{
    public const byte Fists = 0, Pistol = 1, Smg = 2, Ak47 = 3, Shotgun = 4, Rocket = 5, Flame = 6, Chainsaw = 7,
        Bat = 8, Knife = 9, Vacuum = 10, Unknown = 255;

    public static string Label(byte code) => code switch
    {
        Fists => "Fists",
        Pistol => "Pistol",
        Smg => "SMG",
        Ak47 => "AK-47",
        Shotgun => "Shotgun",
        Rocket => "Rocket launcher",
        Flame => "Flamethrower",
        Chainsaw => "Chainsaw",
        Bat => "Bat",
        Knife => "Knife",
        Vacuum => "Vacuum",
        _ => "?",
    };

    public static bool IsMelee(byte code) => code == Fists || code == Bat || code == Knife;
    public static bool IsContinuous(byte code) => code == Flame || code == Chainsaw;
    public static bool IsFirearm(byte code) => code >= Pistol && code <= Rocket;
}

public struct Rgb
{
    public float R, G, B;
    public Rgb(float r, float g, float b) { R = r; G = g; B = b; }
}

public static class AvatarStyle
{
    static readonly Rgb[] Tints =
    {
        new(0.95f, 0.50f, 0.12f), // host: orange
        new(0.20f, 0.60f, 0.95f), // blue
        new(0.30f, 0.82f, 0.38f), // green
        new(0.85f, 0.32f, 0.85f), // magenta
    };

    public static Rgb Tint(int peerIndex) => Tints[((peerIndex % Tints.Length) + Tints.Length) % Tints.Length];

    public static string TagText(string name, byte weapon, bool down)
    {
        string n = string.IsNullOrEmpty(name) ? "Player" : name;
        return down ? n + " (DOWN)" : n + "\n" + WeaponCodes.Label(weapon);
    }

    /// <summary>Moves a 0..1 blend toward its target at 1/seconds per second.</summary>
    public static float Blend(float current, bool target, float dt, float seconds)
    {
        float step = seconds <= 0f ? 1f : dt / seconds;
        float goal = target ? 1f : 0f;
        if (current < goal) return Math.Min(goal, current + step);
        return Math.Max(goal, current - step);
    }
}
