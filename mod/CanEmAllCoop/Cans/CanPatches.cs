using System;
using System.Reflection;
using CanEmAllCoop.Core;
using HarmonyLib;
using UnityEngine;

namespace CanEmAllCoop.Cans;
using Net = CanEmAllCoop.Net.Net;

/// <summary>
/// Harmony patches of the cans module. Every patch is a pass-through when Net.Role is Offline. A target that
/// cannot be resolved or patched is logged and skipped; the rest keeps working.
/// </summary>
internal static class CanPatches
{
    public static int Applied;
    public static int Failed;

    const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    public static void Apply(Harmony h)
    {
        // Host-only pipeline: the grinder coroutine must not run on clients (it kills and grinds enemies, rolls RNG).
        // The MoveNext prefix does not END the coroutine on a client (that would leave every grinder dead for the rest of
        // the process once the session ends): it only yields, so the vanilla loop resumes by itself when the role is Offline again.
        Patch(h, "CanProcessor.<ProcessEnemies>.MoveNext", Method(typeof(CanProcessor._ProcessEnemies_d__39), "MoveNext"),
            prefix: nameof(HoldProcessEnemies));

        var canProcessor = typeof(CanProcessor);
        Patch(h, "CanProcessor.SpawnCan", Method(canProcessor, "SpawnCan"), prefix: nameof(BlockOnClient), postfix: nameof(SpawnCanPostfix));
        Patch(h, "CanProcessor.CreateOrAccumulateCan", Method(canProcessor, "CreateOrAccumulateCan"), prefix: nameof(CreatePrefix), postfix: nameof(CreatePostfix));
        Patch(h, "CanProcessor.FindCompatibleCan", Method(canProcessor, "FindCompatibleCan"), postfix: nameof(FindCompatiblePostfix));
        Patch(h, "GameManager.GetCanFromPool", Method(typeof(GameManager), "GetCanFromPool"), postfix: nameof(GetCanFromPoolPostfix));

        // Pay exactly once: on the host.
        Patch(h, "RewardMachine.ProcessEnemies", Method(typeof(RewardMachine), "ProcessEnemies"), prefix: nameof(BlockOnClient));
        Patch(h, "RewardMachine.ReturnCanAfterDelay", Method(typeof(RewardMachine), "ReturnCanAfterDelay"), prefix: nameof(PaidPrefix));

        // Ownership protocol.
        Patch(h, "Player.GrabDeadBody", Method(typeof(Player), "GrabDeadBody"), prefix: nameof(GrabDeadBodyPrefix));
        Patch(h, "Player.ThrowHeldBody", Method(typeof(Player), "ThrowHeldBody"), prefix: nameof(ThrowPrefix), postfix: nameof(ThrowPostfix));
        Patch(h, "Vacuum.Suck", Method(typeof(Vacuum), "Suck"), prefix: nameof(SuckPrefix));
    }

    static MethodBase Method(Type type, string name)
    {
        try { return AccessTools.Method(type, name); }
        catch (Exception e) { Log.Error($"Cans: lookup of {type.Name}.{name} failed", e); return null; }
    }

    static void Patch(Harmony h, string what, MethodBase target, string prefix = null, string postfix = null)
    {
        if (target == null)
        {
            Failed++;
            Log.Error($"Cans: patch target {what} not found, feature disabled");
            return;
        }
        try
        {
            h.Patch(target,
                prefix == null ? null : new HarmonyMethod(typeof(CanPatches), prefix),
                postfix == null ? null : new HarmonyMethod(typeof(CanPatches), postfix));
            Applied++;
        }
        catch (Exception e)
        {
            Failed++;
            Log.Error($"Cans: patching {what} failed, feature disabled", e);
        }
    }

    static bool ClientRuns => Net.IsClient && !Net.Applying;

    // ---------------------------------------------------------------- client: host-only logic is skipped

    /// <summary>
    /// Iterator MoveNext on a client: skip the step and ask the scheduler to call again next frame (yield null) without
    /// advancing the state machine. Pass-through everywhere else.
    /// </summary>
    static bool HoldProcessEnemies(CanProcessor._ProcessEnemies_d__39 __instance, ref bool __result)
    {
        if (!ClientRuns) return true;
        try { __instance.__2__current = null; }
        catch (Exception) { /* current is only read by the scheduler; null is the default anyway */ }
        __result = true;
        return false;
    }

    static bool BlockOnClient() => !ClientRuns;

    // ---------------------------------------------------------------- host: announce what the game does

    static void GetCanFromPoolPostfix(Can __result)
    {
        if (Net.IsHost) CanHost.OnCanTakenFromPool(__result);
    }

    static void SpawnCanPostfix(CanProcessor __instance, int textureIndex)
    {
        if (!Net.IsHost) return;
        Guard.Run("SpawnCan hook", () => CanHost.OnSpawnCan(__instance, textureIndex));
    }

    static bool CreatePrefix()
    {
        if (ClientRuns) return false;
        if (Net.IsHost) CanHost.OnCreateBegin();
        return true;
    }

    static void CreatePostfix()
    {
        if (Net.IsHost) Guard.Run("CreateOrAccumulateCan hook", CanHost.OnCreateEnd);
    }

    static void FindCompatiblePostfix(Can __result)
    {
        if (Net.IsHost) CanHost.OnFindCompatible(__result);
    }

    static void PaidPrefix(Can can)
    {
        if (can == null || Net.IsClient) return;
        Guard.Run("paid hook", () =>
        {
            Scaling.DayTelemetry.OnCanPaid(can.quantity);   // per-day calibration counter (host and offline)
            if (Net.IsHost) CanHost.OnPaid(can);
        });
    }

    // ---------------------------------------------------------------- grab / throw / vacuum

    static bool GrabDeadBodyPrefix(IGrabbable enemy) => GrabSync.AllowGrab(enemy);

    static void ThrowPrefix(Player __instance)
    {
        if (Net.IsOnline) Guard.Run("throw prefix", () => GrabSync.BeforeThrow(__instance));
    }

    static void ThrowPostfix(Player __instance)
    {
        if (Net.IsOnline) Guard.Run("throw postfix", () => GrabSync.AfterThrow(__instance));
    }

    static void SuckPrefix(Vacuum __instance)
    {
        if (!ClientRuns) return;
        Guard.Run("suck assist", () =>
        {
            var point = __instance.point;
            if (point != null) CanClient.NoteVacuumActive(point.position, __instance.suckRange);
        });
    }
}
