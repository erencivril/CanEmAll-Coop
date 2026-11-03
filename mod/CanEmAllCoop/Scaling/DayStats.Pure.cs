using System;
using System.Globalization;

namespace CanEmAllCoop.Scaling;

/// <summary>
/// Per-day calibration counters (plan section 1.6): trucks and enemies spawned, kills, can units paid, money in/out of the
/// income division and the grinder pending-queue length (mean / max of 1 Hz samples). Pure so the formatting and the
/// accumulation are unit-tested; the game side only feeds it.
/// </summary>
public sealed class DayStats
{
    public int Day = int.MinValue;
    public long Trucks, Spawned, Kills, CanUnits, MoneyIn, MoneyOut;
    public int MaxEnemies;
    public int QueueMax;
    public long QueueSum;
    public int QueueSamples;

    public bool HasActivity => Trucks > 0 || Spawned > 0 || Kills > 0 || CanUnits > 0 || MoneyIn > 0 || QueueSamples > 0;

    public double QueueMean => QueueSamples == 0 ? 0.0 : (double)QueueSum / QueueSamples;

    public void Begin(int day)
    {
        Day = day;
        Trucks = Spawned = Kills = CanUnits = MoneyIn = MoneyOut = 0;
        MaxEnemies = QueueMax = QueueSamples = 0;
        QueueSum = 0;
    }

    public void AddTruck(int enemies)
    {
        Trucks++;
        Spawned += Math.Max(0, enemies);
    }

    public void AddKill() => Kills++;

    public void AddCanUnits(int quantity) => CanUnits += Math.Max(0, quantity);

    public void AddMoney(long incomeIn, long paidOut)
    {
        MoneyIn += Math.Max(0, incomeIn);
        MoneyOut += Math.Max(0, paidOut);
    }

    /// <summary>One 1 Hz sample: registered enemies on the field and the total length of every grinder's pending-can queue.</summary>
    public void AddSample(int enemiesOnField, int pendingCans)
    {
        if (enemiesOnField > MaxEnemies) MaxEnemies = enemiesOnField;
        if (pendingCans > QueueMax) QueueMax = pendingCans;
        QueueSum += Math.Max(0, pendingCans);
        QueueSamples++;
    }

    /// <summary>One log line, e.g. "[DAYSTATS] day=7 N=2 eBase=3 eEff=5 k=1.667 trucks=12 ...".</summary>
    public string Format(int n, int eBase, int eEff, double k, string tag = null)
    {
        var ci = CultureInfo.InvariantCulture;
        return string.Format(ci,
            "[DAYSTATS] day={0} N={1} eBase={2} eEff={3} k={4:0.000} trucks={5} spawned={6} maxEnemies={7} kills={8} canUnits={9} moneyIn={10} moneyOut={11} grinderQueue mean={12:0.0} max={13} samples={14}{15}",
            Day, n, eBase, eEff, k, Trucks, Spawned, MaxEnemies, Kills, CanUnits, MoneyIn, MoneyOut, QueueMean, QueueMax, QueueSamples,
            string.IsNullOrEmpty(tag) ? "" : " (" + tag + ")");
    }
}
