using System;
using CanEmAllCoop.Core;
using HarmonyLib;

namespace CanEmAllCoop.Enemies;

/// <summary>Message ids 300-399 (enemies module).</summary>
public static class EnemyMsg
{
    public const ushort Spawn = 300;        // host -> clients, reliable: SpawnEntry batch
    public const ushort Transforms = 301;   // host -> clients, unreliable: PoseEntry batch
    public const ushort Death = 302;        // host -> clients, reliable: DeathEntry
    public const ushort Return = 303;       // host -> clients, reliable: id list (enemy went back to the pool)
    public const ushort CleanAll = 304;     // host -> clients, reliable: id list of the enemies that REMAIN
    public const ushort Hit = 305;          // client -> host, reliable: HitEntry batch
    public const ushort HitResult = 306;    // host -> shooter, reliable: HitResultEntry batch
    public const ushort Damaged = 307;      // host -> clients, reliable: id list (ApplyDamagedDelivery)
    public const ushort Release = 308;      // host -> clients, reliable: direction/speed/roam + id list
    public const ushort World = 309;        // host -> clients, unreliable: truck + turrets + robots poses
    public const ushort TurretShot = 310;   // host -> clients, unreliable: turret index
    public const ushort Burn = 311;         // client -> host, reliable: BurnEntry batch
}

/// <summary>Harmony helper: patch by type + method name, log and carry on when a target is missing.</summary>
internal static class PatchUtil
{
    public static int Failures;

    public static bool Patch(Harmony harmony, Type type, string method, Type owner,
        string prefix = null, string postfix = null, string finalizer = null, Type[] args = null, bool critical = false)
    {
        string what = $"{type?.Name}.{method}";
        try
        {
            var target = args == null ? AccessTools.Method(type, method) : AccessTools.Method(type, method, args);
            if (target == null)
            {
                Fail(what, "method not found", critical);
                return false;
            }

            harmony.Patch(target,
                prefix: Hm(owner, prefix), postfix: Hm(owner, postfix), finalizer: Hm(owner, finalizer));
            return true;
        }
        catch (Exception e)
        {
            Fail(what, e.Message, critical);
            return false;
        }
    }

    static HarmonyMethod Hm(Type owner, string name)
    {
        if (name == null) return null;
        var m = AccessTools.Method(owner, name);
        if (m == null) throw new MissingMethodException(owner.Name, name);
        return new HarmonyMethod(m);
    }

    static void Fail(string what, string why, bool critical)
    {
        Failures++;
        Log.Error($"Enemies: patch {what} failed ({why})" + (critical ? " - CRITICAL, enemy sync disabled" : ""));
        if (critical) EnemyModule.CriticalFailure = true;
    }
}
