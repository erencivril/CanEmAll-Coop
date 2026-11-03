using System;
using System.Collections.Generic;

namespace CanEmAllCoop.Enemies;

public struct PoseSample
{
    public float X, Y, Z;
    public Quat4 Rot;
    public float Vx, Vy, Vz;   // estimated velocity (units/s) from the two samples around the render time

    /// <summary>
    /// True when the sample is exactly the pose that was applied last and nothing moves: the caller can skip every engine call
    /// for this puppet this frame (PoseBuffer keeps its samples after the enemy stopped, so "has samples" does not mean "moving").
    /// </summary>
    public bool IsRestAt(float ax, float ay, float az, Quat4 arot) =>
        X == ax && Y == ay && Z == az && Vx == 0f && Vy == 0f && Vz == 0f &&
        Rot.X == arot.X && Rot.Y == arot.Y && Rot.Z == arot.Z && Rot.W == arot.W;
}

/// <summary>
/// Client-side snapshot interpolation for one puppet. Samples carry the local receive time; the caller renders at
/// (now - delay). Beyond the newest sample the position is extrapolated with the last velocity for at most
/// <see cref="MaxExtrapolation"/> seconds, then held.
/// </summary>
public sealed class PoseBuffer
{
    public const int Capacity = 8;
    public const float MaxExtrapolation = 0.25f;

    struct S { public float T, X, Y, Z; public Quat4 Q; }

    readonly S[] _s = new S[Capacity];
    int _n;   // samples stored, oldest first (shifted down when full)

    public int Count => _n;

    public void Clear() => _n = 0;

    public void Add(float t, float x, float y, float z, Quat4 q)
    {
        if (_n > 0 && t <= _s[_n - 1].T) t = _s[_n - 1].T + 0.0005f;   // keep strictly increasing
        if (_n == Capacity)
        {
            Array.Copy(_s, 1, _s, 0, Capacity - 1);
            _n--;
        }
        _s[_n++] = new S { T = t, X = x, Y = y, Z = z, Q = q };
    }

    public bool TrySample(float renderTime, out PoseSample result)
    {
        result = default;
        if (_n == 0) return false;

        if (_n == 1 || renderTime <= _s[0].T)
        {
            var f = _s[0];
            result = new PoseSample { X = f.X, Y = f.Y, Z = f.Z, Rot = f.Q };
            return true;
        }

        var last = _s[_n - 1];
        if (renderTime >= last.T)
        {
            var prev = _s[_n - 2];
            float dt = last.T - prev.T;
            float vx = (last.X - prev.X) / dt, vy = (last.Y - prev.Y) / dt, vz = (last.Z - prev.Z) / dt;
            float ahead = MathF.Min(renderTime - last.T, MaxExtrapolation);
            // A velocity estimate from a tiny dt (burst of packets) would fling the body; clamp it.
            if (dt < 0.02f) { vx = vy = vz = 0f; }
            result = new PoseSample
            {
                X = last.X + vx * ahead, Y = last.Y + vy * ahead, Z = last.Z + vz * ahead,
                Rot = last.Q, Vx = vx, Vy = vy, Vz = vz,
            };
            // No packet for MaxExtrapolation: the host stopped sending because the entity stopped (delta filter). The pose is
            // held from here on, so report no velocity - otherwise a standing enemy keeps its walk cycle and can never be "at rest".
            if (renderTime - last.T >= MaxExtrapolation) { result.Vx = result.Vy = result.Vz = 0f; }
            return true;
        }

        int i = 1;
        while (i < _n - 1 && _s[i].T < renderTime) i++;
        var a = _s[i - 1];
        var b = _s[i];
        float span = b.T - a.T;
        float u = span > 1e-6f ? (renderTime - a.T) / span : 1f;
        u = Math.Clamp(u, 0f, 1f);
        result = new PoseSample
        {
            X = a.X + (b.X - a.X) * u, Y = a.Y + (b.Y - a.Y) * u, Z = a.Z + (b.Z - a.Z) * u,
            Rot = Quat4.Nlerp(a.Q, b.Q, u),
            Vx = (b.X - a.X) / span, Vy = (b.Y - a.Y) / span, Vz = (b.Z - a.Z) / span,
        };
        if (span < 0.02f) { result.Vx = result.Vy = result.Vz = 0f; }
        return true;
    }
}

/// <summary>What the host last sent for one entity; decides whether a new pose is worth a slot in the next packet.</summary>
public struct SentState
{
    public float X, Y, Z;
    public Quat4 Rot;
    public float LastSentTime;
    public bool HasSent;
    public bool Dead;

    public static bool NeedsSend(in SentState s, float x, float y, float z, Quat4 q, bool dead, float now,
        float posEps, float rotEpsDeg, float keyframeInterval)
    {
        if (!s.HasSent || s.Dead != dead) return true;
        if (now - s.LastSentTime >= keyframeInterval) return true;
        float dx = x - s.X, dy = y - s.Y, dz = z - s.Z;
        if (dx * dx + dy * dy + dz * dz > posEps * posEps) return true;
        return s.Rot.AngleTo(q) > rotEpsDeg;
    }

    public void MarkSent(float x, float y, float z, Quat4 q, bool dead, float now)
    {
        X = x; Y = y; Z = z; Rot = q; Dead = dead; LastSentTime = now; HasSent = true;
    }
}

/// <summary>
/// Fair selection under a per-tick budget: walks a ring of n candidates from where the previous tick stopped, so
/// entities that did not fit this tick come first next tick.
/// </summary>
public sealed class RoundRobin
{
    int _cursor;

    public void Reset() => _cursor = 0;

    /// <summary>Appends up to maxPick indices (into 0..n-1) for which wants(i) is true.</summary>
    public void Pick(int n, int maxPick, Func<int, bool> wants, List<int> picked)
    {
        if (n <= 0 || maxPick <= 0) return;
        if (_cursor >= n) _cursor = 0;
        int i = _cursor;
        int visited = 0;
        while (visited < n && picked.Count < maxPick)
        {
            if (wants(i)) picked.Add(i);
            i++;
            if (i >= n) i = 0;
            visited++;
        }
        // Resume right after the last examined index; when the budget was not exhausted we walked the whole ring.
        _cursor = i;
    }
}
