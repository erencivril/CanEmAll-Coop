using System;
using System.Collections.Generic;

namespace CanEmAllCoop.Flow;

public enum QuorumPolicy { All, Majority, AllWithTimeout }

/// <summary>The four things the team waits for together.</summary>
public enum BarrierKind : byte
{
    /// <summary>Day start: every player stepped out of the elevator (GameManager.OnTriggerOutsideElevator).</summary>
    Outside = 0,
    /// <summary>Day end: every player is in the elevator (GameManager.OnTriggerInsideElevator).</summary>
    Inside = 1,
    /// <summary>Shop: every player pressed Next Day.</summary>
    Ready = 2,
    /// <summary>Ending: every player is in the exit elevator (ExitDoor.OnTriggerInsideElevator).</summary>
    Ending = 3,
}

public static class Quorum
{
    public static int Required(QuorumPolicy policy, int expected)
    {
        if (expected <= 0) return 1;
        return policy == QuorumPolicy.Majority ? expected / 2 + 1 : expected;
    }

    /// <param name="reported">number of EXPECTED peers that reported</param>
    /// <param name="sinceFirstReport">seconds since the first report of this barrier</param>
    public static bool IsMet(QuorumPolicy policy, int expected, int reported, double sinceFirstReport, double timeoutSeconds)
    {
        if (expected <= 0 || reported <= 0) return false;
        if (reported >= Required(policy, expected)) return true;
        return policy == QuorumPolicy.AllWithTimeout && sinceFirstReport >= timeoutSeconds;
    }
}

/// <summary>Host-side bookkeeping of who reported which barrier.</summary>
public sealed class BarrierBoard
{
    const int Kinds = 4;
    readonly HashSet<ulong>[] _reported = new HashSet<ulong>[Kinds];
    readonly double[] _firstAt = new double[Kinds];

    public BarrierBoard()
    {
        for (int i = 0; i < Kinds; i++) _reported[i] = new HashSet<ulong>();
    }

    public void Report(BarrierKind k, ulong id, double now)
    {
        var set = _reported[(int)k];
        if (set.Count == 0) _firstAt[(int)k] = now;
        set.Add(id);
    }

    public bool Has(BarrierKind k, ulong id) => _reported[(int)k].Contains(id);
    public bool Any(BarrierKind k) => _reported[(int)k].Count > 0;

    public int CountIn(BarrierKind k, IEnumerable<ulong> expected)
    {
        int n = 0;
        var set = _reported[(int)k];
        foreach (var id in expected) if (set.Contains(id)) n++;
        return n;
    }

    public bool IsMet(BarrierKind k, QuorumPolicy policy, IReadOnlyCollection<ulong> expected, double now, double timeoutSeconds)
    {
        int have = CountIn(k, expected);
        return Quorum.IsMet(policy, expected.Count, have, now - _firstAt[(int)k], timeoutSeconds);
    }

    public void Reset(BarrierKind k) => _reported[(int)k].Clear();

    public void ResetAll()
    {
        for (int i = 0; i < Kinds; i++) _reported[i].Clear();
    }

    /// <summary>A peer left: its reports no longer count.</summary>
    public void Remove(ulong id)
    {
        for (int i = 0; i < Kinds; i++) _reported[i].Remove(id);
    }
}
