namespace CanEmAllCoop.Flow;

/// <summary>Message ids 200-299 (DESIGN section 2). Direction in the comment; all reliable unless noted.</summary>
internal static class FlowMsg
{
    /// <summary>client -> host: "my main menu is ready, send me the game state". Repeated every 2 s until answered.</summary>
    public const ushort StartRequest = 200;
    /// <summary>host -> client: string snapshotJson, short hostFlow, int hostDay. Client applies the snapshot (memory only) and starts the game.</summary>
    public const ushort HostStart = 201;
    /// <summary>client -> host: the client's GameLoop is running (it takes part in the Outside barrier).</summary>
    public const ushort InGame = 202;
    /// <summary>host -> clients, UNRELIABLE 2 Hz: short flowState, float timer.</summary>
    public const ushort Timer = 203;
    /// <summary>host -> clients: short flowState after every change of the host's coroutine state.</summary>
    public const ushort FlowState = 204;
    /// <summary>client -> host: byte BarrierKind.</summary>
    public const ushort BarrierReport = 205;
    /// <summary>host -> clients: byte kind, int hostDay, bool late, bool spawnValid, [Vec3 pos, float yaw]. Everyone (or, if late, one peer) proceeds.</summary>
    public const ushort BarrierGo = 206;
    /// <summary>host -> clients: byte kind, byte have, byte need (HUD only; need 0 = cleared).</summary>
    public const ushort BarrierStatus = 207;
    /// <summary>client -> host: float delay, int mailId of the suppressed Suicide call.</summary>
    public const ushort PlayerDown = 208;
    /// <summary>client -> host: the player respawned.</summary>
    public const ushort PlayerUp = 209;
    /// <summary>host -> clients: float delay, int mailId. Everybody runs the original GameManager.Suicide.</summary>
    public const ushort TeamWipe = 210;
    /// <summary>host -> clients: byte count, ulong[] ids of the players that are down (HUD / avatars).</summary>
    public const ushort DownList = 211;
}
