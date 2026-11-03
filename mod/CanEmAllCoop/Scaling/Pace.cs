using System;
using CanEmAllCoop.Core;

namespace CanEmAllCoop.Scaling;
using CanEmAllCoop.Net;
using Net = CanEmAllCoop.Net.Net;

/// <summary>
/// Pace state P0 (plan section 1.4). N is latched, E_base is remembered from the game's own writes, E_eff is what we
/// write into Truck.enemyCount, k = E_eff / E_base. Everything that touches the game is host-only; offline and N = 1 are
/// exact identities. Clients only mirror N / E_base / E_eff (message 600) for the UI.
/// </summary>
public static class Pace
{
    public const ushort MsgState = 600;

    /// <summary>Players latched at the last Latch() (1 offline / on a fresh client until the host state arrives).</summary>
    public static int N = 1;

    /// <summary>Realized supply factor E_eff / E_base (UI / telemetry). Income and plant use <see cref="PayNum"/>/<see cref="PayDen"/>.</summary>
    public static double K = 1.0;

    /// <summary>Solo value of Truck.enemyCount as last written by the game (0 = not seen yet).</summary>
    public static int EBase;

    /// <summary>Value we wrote into Truck.enemyCount (equals EBase when not scaling).</summary>
    public static int EEff;

    /// <summary>The asset's Truck.maxEnemyCount, remembered once per Truck (0 = not seen yet).</summary>
    public static int OrigMax;

    /// <summary>Set when a required patch could not be applied: scaling stays off (N behaves as 1) so supply, plant and money can never drift apart.</summary>
    public static bool Disabled;

    /// <summary>Kept for flow's wiring; no longer used: N is latched at day start only (see <see cref="Latch"/>).</summary>
    public static Func<bool> IsDayRunning;

    /// <summary>Raised on the host after the pace state changed (N, E_eff, mode).</summary>
    public static event Action Changed;

    internal static bool ScaledTruck;     // we wrote non-vanilla values into the current Truck
    internal static bool ConveyorApplied; // UpdateConveyorSpeed last ran with the T3 factor

    /// <summary>
    /// Supply ratio as integers, k = SupplyNum / SupplyDen with SupplyNum = max(E_eff, E_base): k is never below 1, even
    /// for a moment when the game has just raised E_base and the rescale has not run yet. 1/1 when unknown or solo.
    /// </summary>
    public static long SupplyNum { get { PaceMath.SupplyFraction(EBase, EEff, out var n, out _); return n; } }
    public static long SupplyDen { get { PaceMath.SupplyFraction(EBase, EEff, out _, out var d); return d; } }

    /// <summary>Income divisor k^IncomeDivisorExponent as a fraction (paid = amount * IncomeDen / IncomeNum).</summary>
    public static void IncomeDivisor(out long num, out long den) =>
        PaceMath.IncomeDivisor(SupplyNum, SupplyDen, ScalingConfig.IncomeDivisorExponent, out num, out den);

    /// <summary>Income / grinder factor: the supply ratio in Neutral mode on the host, 1/1 otherwise (Free mode, clients, offline).</summary>
    public static long PayNum => PaysScaled ? SupplyNum : 1;
    public static long PayDen => PaysScaled ? SupplyDen : 1;

    /// <summary>True when T1 / M1 (and F6) must act.</summary>
    public static bool PaysScaled => !Disabled && Net.IsHost && CoopConfig.PaceMode == PaceMode.Neutral && SupplyNum != SupplyDen;

    /// <summary>True when skill tree prices are the solo price times k (Neutral mode, online; on the host and on every client).</summary>
    public static bool PricesScaled => !Disabled && Net.IsOnline && !IsFree && SupplyNum != SupplyDen;

    /// <summary>
    /// Latch N from the current session. Flow calls this at the start of every day and nothing else does: a join or a leave
    /// in the middle of a day (or in the shop) only changes the next day. N counts the host plus the clients whose game is
    /// running (a connected peer still loading or sitting in the menu plays no part in the day). Clients ignore it: the
    /// host's state arrives by message.
    /// </summary>
    public static void Latch()
    {
        if (Net.IsClient) return;
        N = Net.IsHost ? PaceMath.PlayerCount(Flow.FlowCore.InGameCount) : 1;
        Rescale();
    }

    /// <summary>Players that will be counted by the next Latch (UI text only).</summary>
    public static int PendingN => Net.IsHost ? PaceMath.PlayerCount(Flow.FlowCore.InGameCount) : N;

    static readonly ChangeWatch<PaceMode> ModeWatch = new();

    /// <summary>Called every frame: raises <see cref="Changed"/> when PaceMode was toggled in the config, so clients refresh their "(free)" label.</summary>
    internal static void PollMode()
    {
        if (ModeWatch.Update(CoopConfig.PaceMode)) Changed?.Invoke();
    }

    /// <summary>true when the mode shown to the player is Free (the host's setting, or the one it replicated).</summary>
    public static bool IsFree => Net.IsHost ? CoopConfig.PaceMode == PaceMode.Free : _remoteFree;

    /// <summary>Called after the game's own skill replay (SkillTreeView.ApplySave etc.). Re-runs the scale; harmless when nothing changed.</summary>
    public static void OnSkillApplied() => Rescale();

    /// <summary>Remember the solo batch size the game just wrote (call right after the game's write).</summary>
    internal static void RememberBase(Truck truck)
    {
        if (truck == null) return;
        EBase = truck.enemyCount;
        ScaledTruck = false; // the game just overwrote whatever we had written
    }

    internal static void RememberAsset(Truck truck)
    {
        if (truck == null) return;
        OrigMax = truck.maxEnemyCount;
        EBase = truck.enemyCount;
        ScaledTruck = false; // fresh Truck object (or re-Awake): nothing of ours in it
    }

    /// <summary>Recompute E_eff / field cap from N and write them (host, online). Offline / N=1 restores the vanilla values.</summary>
    public static void Rescale(Truck truck = null)
    {
        // A client never scales anything: N / E_eff / k come from the host's PaceState message (a local Rescale would reset them to solo values)
        if (Net.IsClient) return;
        try
        {
            truck ??= Truck.Instance;
            bool scale = Net.IsHost && !Disabled;
            int n = scale ? N : 1;

            if (truck != null)
            {
                if (EBase < 1) EBase = truck.enemyCount;               // never saw a writer: assume the current value is the solo one
                if (OrigMax < 1) OrigMax = truck.maxEnemyCount;
                if (!scale && !ScaledTruck)
                {
                    EEff = EBase; // offline: touch nothing
                }
                else
                {
                    double a = CoopConfig.WorkloadExponent;
                    int cap = CoopConfig.PerfCap;
                    // Batch ceiling: PerfCap, and additionally the one-frame spawn valve (Truck.SpawnEnemiesInsideTruck spawns the whole batch at once)
                    EEff = PaceMath.EffectiveEnemies(EBase, n, a, PaceMath.BatchCap(cap, ScalingConfig.MaxSpawnBatch));
                    int max = PaceMath.FieldCap(OrigMax, n, a, cap);
                    if (truck.enemyCount != EEff) truck.enemyCount = EEff;
                    if (truck.maxEnemyCount != max) truck.maxEnemyCount = max;
                    ScaledTruck = n > 1 && (EEff != EBase || max != OrigMax);
                }
            }
            else
            {
                EEff = EBase;
            }

            K = PaceMath.Ratio(Math.Max(EEff, EBase), EBase);
            // The money / F6 carries are deliberately NOT reset here: Rescale runs at every day start, MoreEnemies purchase,
            // Sell and Truck.Awake and each reset would throw away up to one unit of income. They rebase to the new ratio
            // inside RatioCarry and are only cleared when the session ends (Reset).
            ScalingPatches.RefreshConveyor();
            if (scale) Log.Info($"[Pace] N={N} a={CoopConfig.WorkloadExponent:0.##} mode={CoopConfig.PaceMode} E_base={EBase} E_eff={EEff} k={K:0.000} maxEnemyCount={OrigMax}->{(truck != null ? truck.maxEnemyCount : 0)}");
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            Log.Error("[Pace] Rescale failed", e);
        }
    }

    /// <summary>Back to vanilla (session end): restore Truck values and reset the state.</summary>
    public static void Reset()
    {
        N = 1;
        try
        {
            var truck = Truck.Instance;
            if (truck != null && ScaledTruck && EBase >= 1)
            {
                truck.enemyCount = EBase;
                if (OrigMax >= 1) truck.maxEnemyCount = OrigMax;
            }
        }
        catch (Exception e) { Log.Error("[Pace] restoring the truck failed", e); }
        ScaledTruck = false;
        EEff = EBase;
        K = 1.0;
        ScalingPatches.ResetCarries();   // session end: the only place the fractional income is dropped
        ScalingPatches.RefreshConveyor();
        Changed?.Invoke();
    }

    // ---- replication (UI only) ----

    internal static void Write(NetWriter w)
    {
        w.WriteByte((byte)N);
        w.WriteInt(EBase);
        w.WriteInt(EEff);
        w.WriteByte((byte)(CoopConfig.PaceMode == PaceMode.Free ? 1 : 0));
    }

    internal static void ReadClient(NetReader r)
    {
        N = Math.Clamp((int)r.ReadByte(), 1, 4);
        EBase = r.ReadInt();
        EEff = r.ReadInt();
        bool free = r.ReadByte() == 1;
        bool modeChanged = free != _remoteFree;
        _remoteFree = free;
        K = PaceMath.Ratio(Math.Max(EEff, EBase), EBase);
        if (modeChanged) Changed?.Invoke();
    }

    static bool _remoteFree;

    /// <summary>"N=2 k=1.74" for the menu / HUD (plus the pending N for the next day when it differs). Empty offline.</summary>
    public static string Text()
    {
        if (!Net.IsOnline) return "";
        string s = PaceMath.Format(N, Math.Max(EEff, EBase) >= 1 ? Math.Max(EEff, EBase) : 1, EBase >= 1 ? EBase : 1, IsFree);
        int now = PendingN;
        if (now != N) s += $" (next day N={now})";
        return s;
    }
}
