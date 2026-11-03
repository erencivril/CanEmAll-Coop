using System;
using System.Collections.Generic;

namespace CanEmAllCoop.Cans;

/// <summary>
/// Decides which objects go into the next unreliable transform batch: only those that moved since the last
/// time they were sent, plus a few repeats once they come to rest (so one lost datagram cannot leave a
/// puppet floating in the air).
/// </summary>
public sealed class TransformFilter
{
    sealed class Entry
    {
        public float Px, Py, Pz, Qx, Qy, Qz, Qw;
        public int RestRepeats;
    }

    readonly Dictionary<uint, Entry> _last = new();
    readonly float _posThresholdSq;
    readonly float _cosHalfAngle;
    readonly int _restRepeats;

    public TransformFilter(float positionThreshold = 0.02f, float angleThresholdDegrees = 1.5f, int restRepeats = 2)
    {
        _posThresholdSq = positionThreshold * positionThreshold;
        _cosHalfAngle = MathF.Cos(angleThresholdDegrees * MathF.PI / 360f);
        _restRepeats = restRepeats;
    }

    public int Count => _last.Count;

    /// <summary>True when the state must be sent now. Records it as sent in that case.</summary>
    public bool ShouldSend(uint id, float px, float py, float pz, float qx, float qy, float qz, float qw)
    {
        if (!_last.TryGetValue(id, out var e))
        {
            _last[id] = new Entry { Px = px, Py = py, Pz = pz, Qx = qx, Qy = qy, Qz = qz, Qw = qw, RestRepeats = _restRepeats };
            return true;
        }

        float dx = px - e.Px, dy = py - e.Py, dz = pz - e.Pz;
        float dot = MathF.Abs(qx * e.Qx + qy * e.Qy + qz * e.Qz + qw * e.Qw);
        bool moved = dx * dx + dy * dy + dz * dz > _posThresholdSq || dot < _cosHalfAngle;

        if (moved) e.RestRepeats = _restRepeats;
        else if (e.RestRepeats > 0) e.RestRepeats--;
        else return false;

        e.Px = px; e.Py = py; e.Pz = pz; e.Qx = qx; e.Qy = qy; e.Qz = qz; e.Qw = qw;
        return true;
    }

    /// <summary>Make the object eligible again on the next batch (e.g. after a teleport / release).</summary>
    public void Invalidate(uint id) => _last.Remove(id);

    public void Clear() => _last.Clear();
}
