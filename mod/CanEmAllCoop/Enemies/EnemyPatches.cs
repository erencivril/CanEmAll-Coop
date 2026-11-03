using System;
using CanEmAllCoop.Core;
using HarmonyLib;
using UnityEngine;

namespace CanEmAllCoop.Enemies;
using Net = CanEmAllCoop.Net.Net;

/// <summary>
/// Harmony prefixes / postfixes. Every one starts with an Offline / inactive guard so a solo game runs the original
/// code untouched (pass-through).
/// </summary>
internal static class EnemyPatches
{
    static bool Off => !EnemyModule.Active || !Net.IsOnline;

    /// <summary>
    /// Patches that matter on the host or are rare enough to keep installed always. The high-frequency client-only
    /// patches (Enemy.Update, FixedUpdate, ...) are installed lazily by <see cref="ApplyClientSide"/> so a solo or host
    /// game never pays a managed transition for 750 enemies x 5 methods per frame.
    /// </summary>
    public static void Apply(Harmony h)
    {
        var P = typeof(EnemyPatches);

        // Truck: runs on the host only (the client prefixes are cheap: a few calls per truck cycle).
        PatchUtil.Patch(h, typeof(Truck), "StartTruck", P, prefix: nameof(TruckStartPrefix), critical: true);
        PatchUtil.Patch(h, typeof(Truck), "SpawnEnemiesInsideTruck", P,
            prefix: nameof(TruckSpawnPrefix), postfix: nameof(TruckSpawnPostfix), critical: true);
        PatchUtil.Patch(h, typeof(Truck), "ReturnEnemy", P, postfix: nameof(ReturnEnemyPostfix), critical: true);
        PatchUtil.Patch(h, typeof(Truck), "ReturnAllActiveEnemies", P, prefix: nameof(BulkPrefix), finalizer: nameof(BulkFinalizer));
        PatchUtil.Patch(h, typeof(Truck), "StopTruck", P, prefix: nameof(BulkPrefix), finalizer: nameof(BulkFinalizer));
        PatchUtil.Patch(h, typeof(GameManager), "CleanAllScene", P, prefix: nameof(BulkPrefix), finalizer: nameof(BulkFinalizer));

        // Enemy lifecycle events (host -> clients).
        PatchUtil.Patch(h, typeof(Enemy), "ReleaseFromTruck", P, postfix: nameof(ReleasePostfix));
        PatchUtil.Patch(h, typeof(Enemy), "ApplyDamagedDelivery", P, postfix: nameof(DamagedPostfix));

        // Host-side combat observers.
        PatchUtil.Patch(h, typeof(GameManager), "OnHitEnemy", P, postfix: nameof(HitEnemyPostfix));
        PatchUtil.Patch(h, typeof(GameManager), "OnEnemyDeath", P, postfix: nameof(DeathPostfix));
        PatchUtil.Patch(h, typeof(Enemy), "TryPickFleePath", P, prefix: nameof(FleePrefix), finalizer: nameof(FleeFinalizer));
        PatchUtil.Patch(h, typeof(Turret), "Shoot", P, postfix: nameof(TurretShootPostfix));
    }

    static bool _clientApplied;

    /// <summary>Installed once, the first time this process becomes a client. Pass-through again when the session ends.</summary>
    public static bool ApplyClientSide(Harmony h)
    {
        if (_clientApplied) return true;
        _clientApplied = true;
        var P = typeof(EnemyPatches);
        int before = PatchUtil.Failures;
        bool critBefore = EnemyModule.CriticalFailure;

        // Combat: hits go to the host, rolls and kills are host-only.
        PatchUtil.Patch(h, typeof(Enemy), "Damage", P, prefix: nameof(DamagePrefix), critical: true);
        PatchUtil.Patch(h, typeof(Enemy), "AddBurn", P, prefix: nameof(BurnPrefix));
        PatchUtil.Patch(h, typeof(GameManager), "OnHitEnemy", P, prefix: nameof(HitEnemyPrefix));
        PatchUtil.Patch(h, typeof(GameManager), "OnEnemyDeath", P, prefix: nameof(HostOnlyPrefix));
        PatchUtil.Patch(h, typeof(Enemy), "BleedDamage", P, prefix: nameof(HostOnlyPrefix));
        PatchUtil.Patch(h, typeof(Enemy), "UpdateBleeding", P, prefix: nameof(HostOnlyPrefix));

        // Puppets: no AI, no physics.
        foreach (var name in new[] { "Update", "FixedUpdate", "FollowPath", "UpdatePanic", "StartPanic" })
            PatchUtil.Patch(h, typeof(Enemy), name, P, prefix: nameof(PuppetPrefix), critical: name == "Update");

        // Turrets and robots: simulated on the host, replicated poses on clients.
        PatchUtil.Patch(h, typeof(Turret), "Update", P, prefix: nameof(TurretUpdatePrefix));
        PatchUtil.Patch(h, typeof(Turret), "Shoot", P, prefix: nameof(HostOnlyPrefix));
        PatchUtil.Patch(h, typeof(RobotCleaner), "Update", P, prefix: nameof(HostOnlyPrefix));
        PatchUtil.Patch(h, typeof(RobotCleaner), "FixedUpdate", P, prefix: nameof(HostOnlyPrefix));

        if (EnemyModule.CriticalFailure && !critBefore) return false;
        if (PatchUtil.Failures > before) Log.Warn("Enemies: some client-side patches failed, see errors above");
        return true;
    }

    // ---- shared prefixes --------------------------------------------------------------------------------------

    /// <summary>Original runs everywhere except on a client.</summary>
    static bool HostOnlyPrefix() => !(EnemyModule.Active && Net.IsClient);

    static bool PuppetPrefix(Enemy __instance) => !(EnemyModule.Active && Net.IsClient && ClientPuppets.IsPuppet(__instance));

    // ---- truck ------------------------------------------------------------------------------------------------

    static bool TruckStartPrefix() => !(EnemyModule.Active && Net.IsClient);

    static bool TruckSpawnPrefix() => !(EnemyModule.Active && Net.IsClient);

    static void TruckSpawnPostfix(Truck __instance)
    {
        if (!EnemyModule.Active || !Net.IsHost) return;
        try { HostEnemies.OnSpawned(__instance); }
        catch (Exception e) { Log.Error("Enemies: EnemySpawn broadcast failed", e); }
    }

    static void ReturnEnemyPostfix(Enemy enemy)
    {
        if (Off) return;
        try
        {
            if (Net.IsHost) HostEnemies.OnReturned(enemy);
            else ClientPuppets.OnLocalReturn(enemy);
        }
        catch (Exception e) { Log.Error("Enemies: ReturnEnemy hook failed", e); }
    }

    static void BulkPrefix(out bool __state)
    {
        __state = EnemyModule.Active && Net.IsHost;
        if (__state) HostEnemies.BeginBulk();
    }

    static Exception BulkFinalizer(Exception __exception, bool __state)
    {
        if (__state)
        {
            try { HostEnemies.EndBulk(); }
            catch (Exception e) { Log.Error("Enemies: CleanAll broadcast failed", e); }
        }
        return __exception;
    }

    // ---- enemy events -----------------------------------------------------------------------------------------

    static void ReleasePostfix(Enemy __instance, Vector3 direction, float speed, float roamRadius)
    {
        if (!EnemyModule.Active || !Net.IsHost) return;
        try { HostEnemies.OnReleased(__instance, direction, speed, roamRadius); }
        catch (Exception e) { Log.Error("Enemies: release hook failed", e); }
    }

    static void DamagedPostfix(Enemy __instance)
    {
        if (!EnemyModule.Active || !Net.IsHost) return;
        HostEnemies.OnDamagedDelivery(__instance);
    }

    // ---- combat -----------------------------------------------------------------------------------------------

    static bool DamagePrefix(Enemy __instance, float damage, Vector3 dir, Vector3 hitPosition, float damageRadiusMultiplier,
        bool showBlood, bool showDamageText, bool highChanceToScream, bool flashAndDeform, float probabilityMultiplier)
    {
        if (!EnemyModule.Active || !Net.IsClient) return true;
        try
        {
            return Combat.ClientDamage(__instance, damage, dir, hitPosition, damageRadiusMultiplier,
                showBlood, showDamageText, highChanceToScream, flashAndDeform, probabilityMultiplier);
        }
        catch (Exception e)
        {
            Log.Error("Enemies: client hit forwarding failed", e);
            return true;
        }
    }

    static bool BurnPrefix(Enemy __instance, float delta)
    {
        if (EnemyModule.Active && Net.IsClient)
        {
            try { Combat.ClientBurn(__instance, delta); }
            catch (Exception e) { Log.Error("Enemies: client burn forwarding failed", e); }
        }
        return true;   // the local burn visual keeps running
    }

    static bool HitEnemyPrefix(ref float __result)
    {
        if (!EnemyModule.Active || !Net.IsClient) return true;
        __result = 0f;
        return false;
    }

    static void HitEnemyPostfix(float baseDamage, float __result)
    {
        if (EnemyModule.Active && Net.IsHost) Combat.CaptureHit(baseDamage, __result);
    }

    static void DeathPostfix(Enemy enemy)
    {
        if (!EnemyModule.Active || !Net.IsHost) return;
        try { HostEnemies.OnDeath(enemy); }
        catch (Exception e) { Log.Error("Enemies: EnemyDeath broadcast failed", e); }
    }

    // ---- flee path --------------------------------------------------------------------------------------------

    struct FleeState
    {
        public bool Moved;
        public Vector3 Original;
    }

    // TryPickFleePath flees from Player.Instance only. Rather than re-implementing the NavMesh logic we show it the
    // nearest player (local or avatar) for the duration of the call by moving the Player transform and putting it back.
    static void FleePrefix(Enemy __instance, out FleeState __state)
    {
        __state = default;
        if (!EnemyModule.Active || !Net.IsHost) return;
        try
        {
            var player = Player.Instance;
            if (player == null) return;
            var all = PlayerPositions.All();
            if (all.Count < 2) return;

            var from = __instance.transform.position;
            int best = 0;
            float bestD = (all[0] - from).sqrMagnitude;
            for (int i = 1; i < all.Count; i++)
            {
                float d = (all[i] - from).sqrMagnitude;
                if (d < bestD) { bestD = d; best = i; }
            }
            if (best == 0) return;

            var tr = player.transform;
            __state.Original = tr.position;
            __state.Moved = true;
            tr.position = all[best];
        }
        catch (Exception e) { Log.Error("Enemies: flee-path player swap failed", e); }
    }

    static Exception FleeFinalizer(Exception __exception, FleeState __state)
    {
        if (__state.Moved)
        {
            try { Player.Instance.transform.position = __state.Original; }
            catch (Exception e) { Log.Error("Enemies: could not restore the player position after a flee path", e); }
        }
        return __exception;
    }

    // ---- turrets ----------------------------------------------------------------------------------------------

    static bool TurretUpdatePrefix(Turret __instance)
    {
        if (!EnemyModule.Active || !Net.IsClient) return true;
        try { __instance.UpdatePowerState(); }   // the on / off panel is the only part of Update that is local
        catch (Exception) { }
        return false;
    }

    static void TurretShootPostfix(Turret __instance)
    {
        if (EnemyModule.Active && Net.IsHost) WorldSync.OnTurretShot(__instance);
    }
}
