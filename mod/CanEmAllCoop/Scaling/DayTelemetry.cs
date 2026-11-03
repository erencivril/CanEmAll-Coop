using System;
using CanEmAllCoop.Core;
using CanEmAllCoop.Flow;
using UnityEngine;

namespace CanEmAllCoop.Scaling;
using Net = CanEmAllCoop.Net.Net;

/// <summary>
/// Plan 1.6 calibration telemetry: one "[DAYSTATS]" line per day on the host (and offline, so a solo reference run exists
/// to compare with). Counters are fed by tiny pass-through hooks (Scaling patches, Cans' paid hook) and a 1 Hz sampler
/// for the enemies on the field and the grinders' pending-can queues.
/// </summary>
internal static class DayTelemetry
{
    internal static readonly DayStats Stats = new();
    static bool _open;
    static float _nextSample;
    static int _sampleFailures;

    public static void OnPhase(FlowPhase phase)
    {
        if (Net.IsClient) return;
        if (phase == FlowPhase.Day && !_open) Begin();
        else if (_open && (phase == FlowPhase.Shop || phase == FlowPhase.Dead || phase == FlowPhase.None || phase == FlowPhase.ElevatorIn))
            End("day over");
    }

    static void Begin()
    {
        _open = true;
        Stats.Begin(FlowCore.CurrentDay());
        _nextSample = 0f;
    }

    public static void End(string why)
    {
        if (!_open) return;
        _open = false;
        try
        {
            if (!Stats.HasActivity) return;
            Log.Info(Stats.Format(Pace.N, Pace.EBase, Pace.EEff, Pace.K, $"{Net.Role}, {why}"));
        }
        catch (Exception e) { Log.Error("[DAYSTATS] formatting failed", e); }
    }

    public static void OnTruck(Truck truck)
    {
        if (!_open || Net.IsClient || truck == null) return;
        int n = 0;
        try { n = truck.spawnedEnemies?.Count ?? 0; } catch (Exception) { }
        Stats.AddTruck(n);
    }

    public static void OnKill()
    {
        if (_open && !Net.IsClient) Stats.AddKill();
    }

    public static void OnCanPaid(int quantity)
    {
        if (_open && !Net.IsClient) Stats.AddCanUnits(quantity);
    }

    public static void OnMoney(long incomeIn, long paidOut)
    {
        if (_open) Stats.AddMoney(incomeIn, paidOut);
    }

    /// <summary>Called every frame; samples once per second while a day is open.</summary>
    public static void Update()
    {
        if (!_open || Net.IsClient) return;
        float now = Time.unscaledTime;
        if (now < _nextSample) return;
        _nextSample = now + 1f;
        try
        {
            var gm = GameManager.Instance;
            if (gm == null) return;
            int pending = 0;
            var procs = gm.canProcessors;
            if (procs != null)
                for (int i = 0; i < procs.Length; i++)
                {
                    var p = procs[i];
                    var q = p != null ? p.pendingCans : null;
                    if (q != null) pending += q.Count;
                }
            Stats.AddSample(gm.enemies != null ? gm.enemies.Count : 0, pending);
        }
        catch (Exception e)
        {
            if (++_sampleFailures <= 3) Log.Error("[DAYSTATS] sampling failed", e);
        }
    }

    /// <summary>Compact running values for the [STATE] line.</summary>
    public static string DebugState() =>
        $"day={Stats.Day} trucks={Stats.Trucks} spawned={Stats.Spawned} kills={Stats.Kills} canUnits={Stats.CanUnits} qMean={Stats.QueueMean:0.0} qMax={Stats.QueueMax}";
}
