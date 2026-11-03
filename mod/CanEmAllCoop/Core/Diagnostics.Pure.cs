using System.Collections.Generic;

namespace CanEmAllCoop.Core;

/// <summary>
/// Keeps a diagnostic line from flooding the log: the first <c>burst</c> events of a key always pass, after that one event per
/// <c>interval</c> seconds. <c>suppressed</c> is the number of events that were swallowed since the last line that passed.
/// </summary>
public sealed class LogGate
{
    struct Slot { public int Seen; public float NextAllowed; public int Suppressed; }

    readonly Dictionary<string, Slot> _slots = new();
    readonly int _burst;
    readonly float _interval;

    public LogGate(int burst = 3, float interval = 5f)
    {
        _burst = burst;
        _interval = interval;
    }

    public bool Allow(string key, float now, out int suppressed)
    {
        _slots.TryGetValue(key, out var s);
        suppressed = 0;
        if (s.Seen < _burst || now >= s.NextAllowed)
        {
            suppressed = s.Suppressed;
            s.Seen++;
            s.Suppressed = 0;
            if (s.Seen >= _burst) s.NextAllowed = now + _interval;
            _slots[key] = s;
            return true;
        }
        s.Suppressed++;
        _slots[key] = s;
        return false;
    }

    public void Clear() => _slots.Clear();
}

/// <summary>
/// Reports an id once when it has been continuously "inside" for at least <c>threshold</c> seconds. Leaving (or Forget) resets it,
/// so a second stay is reported again. Used to spot cans that sit in the reward pipe without being paid.
/// </summary>
public sealed class DwellTracker
{
    readonly Dictionary<uint, float> _since = new();
    readonly HashSet<uint> _reported = new();
    readonly List<uint> _scratch = new();

    public int Count => _since.Count;

    /// <summary>Call for every id that is inside right now. True exactly once per stay, when the threshold is first reached.</summary>
    public bool Inside(uint id, float now, float threshold)
    {
        if (!_since.TryGetValue(id, out var t)) { _since[id] = now; return false; }
        if (now - t < threshold || _reported.Contains(id)) return false;
        _reported.Add(id);
        return true;
    }

    /// <summary>Drops every tracked id that is not in <paramref name="insideNow"/> (call after a round of <see cref="Inside"/> calls).</summary>
    public void Prune(HashSet<uint> insideNow)
    {
        _scratch.Clear();
        foreach (var id in _since.Keys)
            if (!insideNow.Contains(id)) _scratch.Add(id);
        foreach (var id in _scratch) Forget(id);
    }

    public void Forget(uint id)
    {
        _since.Remove(id);
        _reported.Remove(id);
    }

    public void Clear()
    {
        _since.Clear();
        _reported.Clear();
    }
}
