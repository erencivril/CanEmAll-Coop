using System;
using CanEmAllCoop.Core;
using HarmonyLib;
using UnityEngine;

namespace CanEmAllCoop.Cans;
using Net = CanEmAllCoop.Net.Net;

/// <summary>
/// Cans / money pipe (plan 2.3): grinder, can spawn / merge, reward pipe run on the host only; clients get
/// kinematic can and crate puppets. Grab / drop / throw / vacuum use the ownership protocol in GrabSync.
/// Messages 400..499.
/// </summary>
public sealed class CanModule : ICoopModule
{
    float _lastTime;

    public string Name => "Cans";

    public void Init(Harmony harmony)
    {
        try
        {
            CanPatches.Apply(harmony);

            Net.Register(CanMsg.Spawn, CanClient.OnSpawn);
            Net.Register(CanMsg.Merge, CanClient.OnMerge);
            Net.Register(CanMsg.Paid, CanClient.OnPaid);
            Net.Register(CanMsg.Gone, CanClient.OnGone);
            Net.Register(CanMsg.Transforms, CanClient.OnTransforms);
            Net.Register(CanMsg.CrateIds, CanClient.OnCrateIds);
            GrabSync.Register();
        }
        catch (Exception e)
        {
            Log.Error("Cans: init failed, module runs without networking", e);
        }
        Log.Info($"Cans: {CanPatches.Applied} patches applied, {CanPatches.Failed} failed");
    }

    public void OnUpdate()
    {
        if (!Net.IsOnline) return;
        float now = Time.realtimeSinceStartup;
        float dt = Mathf.Clamp(now - _lastTime, 0f, 0.25f);
        _lastTime = now;

        if (Net.IsHost) CanHost.Update(now);
        else CanClient.Update(now, dt);
        GrabSync.Update(now);
    }

    public void OnSessionStart()
    {
        _lastTime = Time.realtimeSinceStartup;
        GrabSync.WasHost = Net.IsHost;
        CanHost.Reset();
        CanClient.Reset();
        GrabSync.Reset();
    }

    public void OnSessionEnd()
    {
        try { GrabSync.ReleaseAllOnHost(); }
        catch (Exception e) { Log.Error("Cans: releasing held objects failed", e); }
        CanClient.Shutdown();
        CanHost.Reset();
        GrabSync.Reset();
    }

    public void OnPeerJoined(Peer peer)
    {
        if (!Net.IsHost) return;
        CanHost.OnPeerJoined(peer);
    }

    public void OnPeerLeft(Peer peer)
    {
        if (Net.IsHost) GrabSync.OnPeerLeft(peer);
    }

    public string DebugState()
    {
        string patches = $"patches={CanPatches.Applied}/{CanPatches.Applied + CanPatches.Failed}";
        if (Net.IsHost)
            return $"{patches} tracked={CanHost.TrackedCount} holds={GrabSync.HoldCount} errors={Guard.TotalFailures}";
        if (Net.IsClient)
            return $"{patches} puppets={CanClient.PuppetCount} pending={CanClient.PendingCount} holds={GrabSync.HoldCount} hand={GrabSync.LocalHandId} errors={Guard.TotalFailures}";
        return patches;
    }
}
