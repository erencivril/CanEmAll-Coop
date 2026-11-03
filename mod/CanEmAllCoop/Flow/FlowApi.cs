using System;
using UnityEngine;

namespace CanEmAllCoop.Flow;

/// <summary>Read-only view of the flow module for the other modules (avatar: down state, enemies/cans: day phase).</summary>
public static class FlowApi
{
    /// <summary>Raised on every peer when its local GameLoop enters another phase (day, night, shop...).</summary>
    public static event Action<FlowPhase> PhaseChanged
    {
        add => FlowCore.PhaseChanged += value;
        remove => FlowCore.PhaseChanged -= value;
    }

    public static FlowPhase Phase => FlowCore.Phase;

    /// <summary>The local player is down and waiting to respawn.</summary>
    public static bool LocalPlayerDown => FlowCore.IsDown;

    /// <summary>Is this player (Peer.Id) currently down? Known on every peer (host tracks, clients get DownList).</summary>
    public static bool IsPlayerDown(ulong peerId) => FlowCore.IsPlayerDown(peerId);

    /// <summary>Where players step out of the elevator / respawn (set once the first player left the elevator today).</summary>
    public static bool TryGetSpawn(out Vector3 position, out float yaw) => FlowCore.TryGetSpawn(out position, out yaw);
}
