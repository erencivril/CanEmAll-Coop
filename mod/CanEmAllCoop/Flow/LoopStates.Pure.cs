using System;

namespace CanEmAllCoop.Flow;

public enum FlowPhase { None, ElevatorIn, WaitingToStart, Day, Night, DayEnding, Shop, Dead }

/// <summary>
/// State numbers of the game's coroutines, as observed in the coroutine state machines:
/// GameManager+_GameLoop_d__178 and GameManager+_SuicideCoroutine_d__177.
/// Pure so the tables are unit-tested; a game update that renumbers them breaks a test-visible assumption in one place.
///
/// "Flow state" = one int that identifies where a peer is: GameLoop state n (0..22) or 100 + SuicideCoroutine state.
/// </summary>
public static class LoopStates
{
    public const int SuicideBase = 100;

    /// <summary>Day setup runs in this state's MoveNext (Timer = dayDuration, Truck.StartTruck).</summary>
    public const int DayStartSetup = 8;
    public const int Day = 9;
    public const int Night = 10;

    public static int Flow(int loopState, int suicideState) => suicideState >= 0 ? SuicideBase + suicideState : loopState;

    /// <summary>Waiting for GameManager.triggerCompleted after OnTriggerOutsideElevator (elevatorState == 1).</summary>
    public static bool IsOutsideWait(int s) => s == 5 || s == 6;
    /// <summary>Waiting for triggerCompleted after OnTriggerInsideElevator (elevatorState == 2).</summary>
    public static bool IsInsideWait(int s) => s == 15 || s == 16;
    /// <summary>Shop: waiting for Space / isNextDayClicked and an idle slot machine.</summary>
    public static bool IsShopWait(int s) => s == 21 || s == 22;
    /// <summary>Day or night countdown: GameManager.Timer is decremented here (flow states, so suicide states are never timed).</summary>
    public static bool IsTimed(int s) => s == Day || s == Night;
    /// <summary>States that belong to the shop (the release token is reset when the loop leaves them).</summary>
    public static bool IsShopFamily(int s) => s >= 20 && s <= 22;

    /// <summary>Same wait as the shop, after a day was aborted (team wipe): SuicideCoroutine states 3 and 4.</summary>
    public static bool IsSuicideShopWait(int s) => s == 3 || s == 4;
    public static bool IsSuicideShopFamily(int s) => s >= 2 && s <= 4;

    public static FlowPhase PhaseOfLoop(int s)
    {
        if (s < 0) return FlowPhase.None;
        if (s <= 4) return FlowPhase.ElevatorIn;
        if (s <= 7) return FlowPhase.WaitingToStart;
        if (s == DayStartSetup || s == Day) return FlowPhase.Day;
        if (s == Night) return FlowPhase.Night;
        if (s <= 19) return FlowPhase.DayEnding;
        return FlowPhase.Shop;
    }

    public static FlowPhase PhaseOfSuicide(int s)
    {
        if (s < 0) return FlowPhase.None;
        return s >= 3 ? FlowPhase.Shop : FlowPhase.Dead;
    }

    public static FlowPhase PhaseOfFlow(int flow) =>
        flow >= SuicideBase ? PhaseOfSuicide(flow - SuicideBase) : PhaseOfLoop(flow);

    /// <summary>The host is in the field part of a day (late joiners may walk straight in).</summary>
    public static bool IsDayInProgress(int flow) => flow >= DayStartSetup && flow <= Night;
}

public enum BarrierTiming
{
    /// <summary>The host currently waits for this barrier: count the report.</summary>
    Open,
    /// <summary>The report arrived before the host reached the barrier (late joiner waiting in its first elevator): keep it.</summary>
    Early,
    /// <summary>The host already passed the barrier: release the reporter at once.</summary>
    Late,
}

public static class BarrierPhase
{
    /// <param name="hostFlow">flow state of the host (LoopStates.Flow)</param>
    public static BarrierTiming Classify(BarrierKind kind, int hostFlow)
    {
        switch (kind)
        {
            case BarrierKind.Outside:
                if (hostFlow < 0) return BarrierTiming.Early;
                if (hostFlow <= 6) return BarrierTiming.Open;
                if (hostFlow <= LoopStates.Night) return BarrierTiming.Late;
                return BarrierTiming.Early;
            case BarrierKind.Inside:
                if (hostFlow < 11) return BarrierTiming.Early;
                if (hostFlow <= 16) return BarrierTiming.Open;
                return BarrierTiming.Late;
            case BarrierKind.Ready:
                if (hostFlow >= 19 && hostFlow <= 22) return BarrierTiming.Open;
                if (hostFlow >= LoopStates.SuicideBase + 2 && hostFlow <= LoopStates.SuicideBase + 4) return BarrierTiming.Open;
                // the host's suicide coroutine may be a frame or two behind a client that already reached its shop wait
                if (hostFlow >= LoopStates.SuicideBase && hostFlow < LoopStates.SuicideBase + 2) return BarrierTiming.Early;
                return BarrierTiming.Late;
            default:
                return BarrierTiming.Open;
        }
    }
}
