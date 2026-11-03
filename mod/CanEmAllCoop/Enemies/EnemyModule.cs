using System;
using CanEmAllCoop.Core;
using HarmonyLib;
using UnityEngine;

namespace CanEmAllCoop.Enemies;
using CanEmAllCoop.Net;
using Net = CanEmAllCoop.Net.Net;

/// <summary>
/// Enemy / combat replication (plan 2.3 "Spawn / enemies / combat"). The host runs the truck, the AI and all damage
/// rolls; clients hold puppets. See Enemies/NOTES.md.
/// </summary>
public sealed class EnemyModule : ICoopModule
{
    /// <summary>False when a critical patch could not be applied: every hook then becomes a pass-through.</summary>
    internal static bool Active { get; private set; }
    internal static bool CriticalFailure;

    static Harmony _harmony;

    public string Name => "Enemies";

    public void Init(Harmony harmony)
    {
        try
        {
            _harmony = harmony;
            EnemyPatches.Apply(harmony);
            Active = !CriticalFailure;
            if (!Active) { Log.Error("Enemies: disabled (critical patch missing); the game keeps running vanilla for enemies"); return; }
            if (PatchUtil.Failures > 0) Log.Warn($"Enemies: {PatchUtil.Failures} non-critical patch(es) failed, see errors above");

            Net.Register(EnemyMsg.Spawn, (sender, r) => { if (Net.IsClient) ClientPuppets.HandleSpawn(r); });
            Net.Register(EnemyMsg.Transforms, (sender, r) => { if (Net.IsClient) ClientPuppets.HandleTransforms(r); });
            Net.Register(EnemyMsg.Death, (sender, r) => { if (Net.IsClient) ClientPuppets.HandleDeath(r); });
            Net.Register(EnemyMsg.Return, (sender, r) => { if (Net.IsClient) ClientPuppets.HandleReturn(r); });
            Net.Register(EnemyMsg.CleanAll, (sender, r) => { if (Net.IsClient) ClientPuppets.HandleCleanAll(r); });
            Net.Register(EnemyMsg.Damaged, (sender, r) => { if (Net.IsClient) ClientPuppets.HandleDamaged(r); });
            Net.Register(EnemyMsg.Release, (sender, r) => { if (Net.IsClient) ClientPuppets.HandleRelease(r); });
            Net.Register(EnemyMsg.World, (sender, r) => { if (Net.IsClient) WorldSync.HandleWorld(r); });
            Net.Register(EnemyMsg.TurretShot, (sender, r) => { if (Net.IsClient) WorldSync.HandleTurretShot(r); });
            Net.Register(EnemyMsg.HitResult, (sender, r) => { if (Net.IsClient) Combat.HandleHitResult(sender, r); });
            Net.Register(EnemyMsg.Hit, (sender, r) => { if (Net.IsHost) Combat.HandleHit(sender, r); });
            Net.Register(EnemyMsg.Burn, (sender, r) => { if (Net.IsHost) Combat.HandleBurn(sender, r); });
            CanEmAllCoop.Cans.GrabSync.RemoteHeldPose += ClientPuppets.OnRemoteHeldPose;
        }
        catch (Exception e)
        {
            Active = false;
            Log.Error("Enemies: Init failed, module disabled", e);
        }
    }

    public void OnUpdate()
    {
        if (!Active || !Net.IsOnline) return;
        float now = Time.unscaledTime;
        if (Net.IsHost)
        {
            HostEnemies.Tick(now);
            WorldSync.HostTick(now);
        }
        else
        {
            float dt = Time.unscaledDeltaTime;
            ClientPuppets.Apply(now, dt);
            WorldSync.ClientApply(dt);
            Combat.FlushClient();
        }
    }

    public void OnSessionStart()
    {
        if (!Active) return;
        HostEnemies.Reset();
        WorldSync.Reset();
        Combat.ClearClient();
        if (!Net.IsClient) return;
        if (!EnemyPatches.ApplyClientSide(_harmony))
        {
            // Joining without the puppet patches would run a second truck / second AI on this machine.
            Active = false;
            Log.Error("Enemies: client patches failed, leaving the session");
            Net.Leave();
            return;
        }
        // Whatever the client had on the field from its own offline game must go before host enemies arrive.
        try
        {
            ClientPuppets.ReturnAll();
            var truck = Truck.Instance;
            if (truck != null) truck.ReturnAllActiveEnemies();
        }
        catch (Exception e) { Log.Error("Enemies: client clean-up at session start failed", e); }
    }

    public void OnSessionEnd()
    {
        if (!Active) return;
        try { ClientPuppets.ReturnAll(); }
        catch (Exception e) { Log.Error("Enemies: puppet clean-up at session end failed", e); }
        HostEnemies.Reset();
        WorldSync.Reset();
        Combat.ClearClient();
    }

    public void OnPeerJoined(Peer peer)
    {
        if (!Active || !Net.IsHost) return;
        HostEnemies.SendAllTo(peer);
    }

    public void OnPeerLeft(Peer peer) { }

    public string DebugState()
    {
        if (!Active) return "inactive";
        return Net.IsHost ? $"{HostEnemies.Debug()} {Combat.Debug()} {WorldSync.Debug()}"
             : Net.IsClient ? $"{ClientPuppets.Debug()} {Combat.Debug()} {WorldSync.Debug()}"
             : "offline";
    }

    // ---- API for other modules (documented in NOTES.md) -------------------------------------------------------

    /// <summary>Net id of a replicated enemy (0 when none). Same as NetIds.IdOf.</summary>
    public static uint NetIdOf(Enemy e) => NetIds.IdOf(e);

    /// <summary>True for a client-side puppet enemy (its AI and physics are patched off).</summary>
    public static bool IsPuppet(Enemy e) => Active && Net.IsClient && ClientPuppets.IsPuppet(e);

    /// <summary>
    /// Client: stop (true) / resume (false) applying host snapshots to this enemy, e.g. while the local player holds the
    /// dead body and the cans module streams its transform to the host.
    /// </summary>
    public static void SetExternallyControlled(Enemy e, bool controlled)
    {
        uint id = NetIds.IdOf(e);
        if (id == 0) return;
        if (controlled) ClientPuppets.ExternallyControlled.Add(id);
        else ClientPuppets.ExternallyControlled.Remove(id);
    }
}
