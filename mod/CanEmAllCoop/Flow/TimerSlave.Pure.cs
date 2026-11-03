using System;

namespace CanEmAllCoop.Flow;

public enum TimerAction { None, Set, ForceZero }

public readonly struct TimerDecision
{
    public readonly TimerAction Action;
    public readonly float Value;
    public TimerDecision(TimerAction action, float value) { Action = action; Value = value; }
    public static readonly TimerDecision None = new(TimerAction.None, 0f);
}

/// <summary>
/// Client-side Timer slaving. Every peer runs the real GameLoop, which decrements GameManager.Timer itself and ends the
/// phase when it reaches 0. The host decides when a phase ends: the client's timer is held above zero until the host
/// reports a later loop state, then forced to 0 so the local loop follows on its next step.
/// The GameLoop checks `Timer > 0` at the START of each step, so adjusting Timer in a plugin Update (which runs before
/// the coroutine step of the same frame) is early enough.
/// </summary>
public static class TimerSlave
{
    /// <summary>Lowest value the client timer is held at while the host is still in the same phase.</summary>
    public const float Floor = 0.5f;

    /// <param name="localState">GameLoop state the local iterator will resume from.</param>
    /// <param name="hostState">Latest GameLoop state reported by the host, -1 if unknown.</param>
    /// <param name="freshHostTimer">true exactly once per received Timer message.</param>
    /// <param name="hostTimerState">Loop state the host was in when it sent that timer.</param>
    public static TimerDecision Decide(int localState, int hostState, float localTimer,
        bool freshHostTimer, int hostTimerState, float hostTimer)
    {
        if (!LoopStates.IsTimed(localState)) return TimerDecision.None;

        if (hostState > localState)
            return localTimer > 0f ? new TimerDecision(TimerAction.ForceZero, 0f) : TimerDecision.None;

        if (hostState == localState && freshHostTimer && hostTimerState == localState)
            return new TimerDecision(TimerAction.Set, Math.Max(hostTimer, Floor));

        // host unknown, still behind, or no new sample: never let the local clock expire on its own
        return localTimer < Floor ? new TimerDecision(TimerAction.Set, Floor) : TimerDecision.None;
    }
}
