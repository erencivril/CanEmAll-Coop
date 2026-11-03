using System;
using System.Reflection;
using CanEmAllCoop.Core;
using HarmonyLib;

namespace CanEmAllCoop.Scaling;
using Net = CanEmAllCoop.Net.Net;

/// <summary>
/// Harmony patches of the scaling module (plan rows S1, S2, T1, M1, T3, F6). Every callback is a pass-through offline
/// and never throws into the game.
/// </summary>
public static class ScalingPatches
{
    static readonly RatioCarry MoneyCarry = new();
    static readonly RatioCarry FirstBloodCarry = new();
    static readonly RatioCarry ShockwaveCarry = new();
    static long _moneyIn, _moneyOut;
    static int _errors;

    /// <summary>Patch bodies run up to hundreds of times per second: log the first failures and then one in a thousand.</summary>
    static void Err(string where, Exception e)
    {
        int n = ++_errors;
        if (n <= 5 || n % 1000 == 0) Log.Error($"[Scaling] {where} (x{n})", e);
    }

    public static long MoneyIn => _moneyIn;
    public static long MoneyOut => _moneyOut;

    internal static void ResetCarries()
    {
        MoneyCarry.Reset();
        FirstBloodCarry.Reset();
        ShockwaveCarry.Reset();
    }

    // ------------------------------------------------------------------------------------------ apply

    /// <summary>Applies every patch. Required ones (S1, S2, T1, M1) failing sets Pace.Disabled so pace can never half-apply.</summary>
    internal static void Apply(Harmony h)
    {
        bool ok = true;
        // S1 + P0: the three places that write Truck.enemyCount / maxEnemyCount.
        ok &= TryPatch(h, "MoreCreaturesSkill.ApplySkill", typeof(MoreCreaturesSkill), "ApplySkill", postfix: nameof(SkillAppliedPostfix));
        ok &= TryPatch(h, "Truck.ResetToDefaultValues", typeof(Truck), "ResetToDefaultValues", postfix: nameof(TruckResetPostfix));
        ok &= TryPatch(h, "Truck.Awake", typeof(Truck), "Awake", postfix: nameof(TruckAwakePostfix));
        // T1
        ok &= TryPatch(h, "CanProcessor.GetCanInterval", typeof(CanProcessor), "GetCanInterval", postfix: nameof(CanIntervalPostfix));
        // M1
        ok &= TryPatch(h, "MoneyManager.AddMoney", typeof(MoneyManager), "AddMoney", prefix: nameof(AddMoneyPrefix));

        if (!ok)
        {
            Pace.Disabled = true;
            Log.Error("[Scaling] a required patch failed; player-count scaling is DISABLED (the game runs vanilla pace)");
        }

        // Cosmetic: keep the skill-shop text of "More Enemies" in solo units.
        TryPatch(h, "MoreCreaturesSkill.GetFromValueText", typeof(MoreCreaturesSkill), "GetFromValueText",
            prefix: nameof(FromTextPrefix), finalizer: nameof(FromTextFinalizer));
        // T3 (config ScaleConveyor, default off)
        TryPatch(h, "GameManager.UpdateConveyorSpeed", typeof(GameManager), "UpdateConveyorSpeed",
            prefix: nameof(ConveyorPrefix), finalizer: nameof(ConveyorFinalizer));
        // Telemetry (pass-through counters, no behaviour change)
        TryPatch(h, "Truck.SpawnEnemiesInsideTruck (telemetry)", typeof(Truck), "SpawnEnemiesInsideTruck", postfix: nameof(TruckSpawnPostfix));
        TryPatch(h, "GameManager.OnEnemyDeath (telemetry)", typeof(GameManager), "OnEnemyDeath", postfix: nameof(KillPostfix));
        // F6 (config ScaleFirstBloodCounters, default off)
        TryPatch(h, "GameManager.OnHitEnemy", typeof(GameManager), "OnHitEnemy", prefix: nameof(HitPrefix), postfix: nameof(HitPostfix));
        TryPatch(h, "GameManager.OnEnemyDeath", typeof(GameManager), "OnEnemyDeath", prefix: nameof(DeathPrefix), postfix: nameof(DeathPostfix));
    }

    static bool TryPatch(Harmony h, string what, Type type, string method, string prefix = null, string postfix = null, string finalizer = null)
    {
        try
        {
            var target = AccessTools.Method(type, method);
            if (target == null)
            {
                Log.Error($"[Scaling] patch target not found: {what}");
                return false;
            }
            h.Patch(target,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(ScalingPatches), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(ScalingPatches), postfix),
                finalizer: finalizer == null ? null : new HarmonyMethod(typeof(ScalingPatches), finalizer));
            Log.Info($"[Scaling] patched {what}");
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"[Scaling] patching {what} failed", e);
            return false;
        }
    }

    // ------------------------------------------------------------------------------------------ S1 / S2 / P0

    // The game has just written Truck.enemyCount (absolute skill value): remember it as E_base, then scale.
    static void SkillAppliedPostfix()
    {
        try
        {
            var truck = Truck.Instance;
            if (truck == null) return;
            Pace.RememberBase(truck);
            Pace.Rescale(truck);
        }
        catch (Exception e) { Err("SkillApplied postfix", e); }
    }

    static void TruckResetPostfix(Truck __instance)
    {
        try
        {
            Pace.RememberBase(__instance);
            Pace.Rescale(__instance);
        }
        catch (Exception e) { Err("Truck.ResetToDefaultValues postfix", e); }
    }

    static void TruckAwakePostfix(Truck __instance)
    {
        try
        {
            Pace.RememberAsset(__instance);
            Pace.Rescale(__instance);
        }
        catch (Exception e) { Err("Truck.Awake postfix", e); }
    }

    // ------------------------------------------------------------------------------------------ T1

    // result = processInterval / speedMult / (...): dividing by k makes every grinder drain k times faster.
    static void CanIntervalPostfix(ref float __result)
    {
        try
        {
            if (!Pace.PaysScaled) return;
            long num = Pace.PayNum, den = Pace.PayDen;
            float r = (float)(__result * ((double)den / num));
            if (r > 0f && !float.IsNaN(r) && !float.IsInfinity(r)) __result = r;
        }
        catch (Exception e) { Err("GetCanInterval postfix", e); }
    }

    // ------------------------------------------------------------------------------------------ M1

    static bool AddMoneyPrefix(ref int amount)
    {
        try
        {
            if (Net.IsClient) return Net.Applying;           // clients never pay; economy applies host balances itself
            if (amount <= 0) return true;
            if (!Pace.PaysScaled)                             // offline / Free / N=1: vanilla, only counted for the day telemetry
            {
                DayTelemetry.OnMoney(amount, amount);
                return true;
            }
            _moneyIn += amount;
            Pace.IncomeDivisor(out long divNum, out long divDen);   // k^exponent = divNum / divDen
            long paid = MoneyCarry.Take(amount, divDen, divNum);    // amount / k
            _moneyOut += paid;
            DayTelemetry.OnMoney(amount, paid);
            if (paid <= 0) return false;
            amount = (int)paid;
            return true;
        }
        catch (Exception e)
        {
            Err("AddMoney prefix", e);
            return true;
        }
    }

    // ------------------------------------------------------------------------------------------ telemetry hooks

    static void TruckSpawnPostfix(Truck __instance)
    {
        try { DayTelemetry.OnTruck(__instance); }
        catch (Exception e) { Err("SpawnEnemiesInsideTruck telemetry", e); }
    }

    static void KillPostfix()
    {
        try { DayTelemetry.OnKill(); }
        catch (Exception e) { Err("OnEnemyDeath telemetry", e); }
    }

    // ------------------------------------------------------------------------------------------ cosmetic: shop text

    // MoreCreaturesSkill.GetFromValueText may print Truck.enemyCount (the scaled value on the host). Show the solo value.
    static void FromTextPrefix(out int __state)
    {
        __state = -1;
        try
        {
            if (!Pace.ScaledTruck || !Net.IsHost || Pace.EBase < 1) return;
            var truck = Truck.Instance;
            if (truck == null) return;
            __state = truck.enemyCount;
            truck.enemyCount = Pace.EBase;
        }
        catch (Exception e) { Err("GetFromValueText prefix", e); }
    }

    static Exception FromTextFinalizer(int __state, Exception __exception)
    {
        try
        {
            if (__state >= 0)
            {
                var truck = Truck.Instance;
                if (truck != null) truck.enemyCount = __state;
            }
        }
        catch (Exception e) { Err("GetFromValueText finalizer", e); }
        return __exception;
    }

    // ------------------------------------------------------------------------------------------ T3

    internal static bool ConveyorActive =>
        CoopConfig.ScaleConveyor && !Pace.Disabled && Net.IsHost && Pace.SupplyNum != Pace.SupplyDen;

    // UpdateConveyorSpeed copies GameManager.conveyorSpeed into every belt; scale the input for the duration of the call only.
    static void ConveyorPrefix(GameManager __instance, out float __state)
    {
        __state = -1f;
        try
        {
            if (!ConveyorActive || __instance == null) return;
            float speed = __instance.conveyorSpeed;
            __state = speed;
            __instance.conveyorSpeed = (float)(speed * Math.Sqrt((double)Pace.SupplyNum / Pace.SupplyDen));
            Pace.ConveyorApplied = true;
        }
        catch (Exception e) { Err("UpdateConveyorSpeed prefix", e); }
    }

    static Exception ConveyorFinalizer(GameManager __instance, float __state, Exception __exception)
    {
        try
        {
            if (__state >= 0f && __instance != null) __instance.conveyorSpeed = __state;
        }
        catch (Exception e) { Err("UpdateConveyorSpeed finalizer", e); }
        return __exception;
    }

    /// <summary>Re-run UpdateConveyorSpeed after N / k changed (or the option was switched) so belts pick up the new factor.</summary>
    internal static void RefreshConveyor()
    {
        try
        {
            if (!ConveyorActive && !Pace.ConveyorApplied) return;
            Pace.ConveyorApplied = ConveyorActive;
            var gm = GameManager.Instance;
            if (gm != null) gm.UpdateConveyorSpeed();
        }
        catch (Exception e) { Err("RefreshConveyor", e); }
    }

    // ------------------------------------------------------------------------------------------ F6

    static bool FirstBloodActive => CoopConfig.ScaleFirstBloodCounters && Pace.PaysScaled;

    // Each counter increment is given back with probability (1 - 1/k) (exact carry), so a counter advances 1/k per event
    // and the perks cover the same share of a k times bigger day.
    static void HitPrefix(out int __state)
    {
        __state = -1;
        try
        {
            if (!FirstBloodActive) return;
            var gm = GameManager.Instance;
            if (gm != null) __state = gm.firstBloodCounter;
        }
        catch (Exception e) { Err("OnHitEnemy prefix", e); }
    }

    static void HitPostfix(int __state)
    {
        try
        {
            if (__state < 0) return;
            var gm = GameManager.Instance;
            if (gm == null || gm.firstBloodCounter != __state + 1) return;
            long refund = FirstBloodCarry.Take(1, Pace.PayNum - Pace.PayDen, Pace.PayNum);
            if (refund > 0) gm.firstBloodCounter = __state;
        }
        catch (Exception e) { Err("OnHitEnemy postfix", e); }
    }

    static void DeathPrefix(out int __state)
    {
        __state = -1;
        try
        {
            if (!FirstBloodActive) return;
            var gm = GameManager.Instance;
            if (gm != null) __state = gm.startingShockwaveCounter;
        }
        catch (Exception e) { Err("OnEnemyDeath prefix", e); }
    }

    static void DeathPostfix(int __state)
    {
        try
        {
            if (__state < 0) return;
            var gm = GameManager.Instance;
            if (gm == null || gm.startingShockwaveCounter != __state + 1) return;
            long refund = ShockwaveCarry.Take(1, Pace.PayNum - Pace.PayDen, Pace.PayNum);
            if (refund > 0) gm.startingShockwaveCounter = __state;
        }
        catch (Exception e) { Err("OnEnemyDeath postfix", e); }
    }
}
