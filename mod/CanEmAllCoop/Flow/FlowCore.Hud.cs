using System.Collections.Generic;
using CanEmAllCoop.Core;

namespace CanEmAllCoop.Flow;
using Net = CanEmAllCoop.Net.Net;

internal static partial class FlowCore
{
    internal static void CollectHudLines(List<string> lines)
    {
        if (HasNotice) lines.AddRange(_notice);

        if (Net.IsClient && !_hostedGame && !string.IsNullOrEmpty(JoinStatus))
            lines.Add(JoinStatus);

        if (!Playing) return;

        if (_down)
        {
            int others = Net.IsHost ? _tracker.Count : _downSet.Count;
            lines.Add($"YOU ARE DOWN - back in {DownSecondsLeft:0}s" + (others > 1 ? $"  ({others} down)" : ""));
        }

        if (_shopHeld && !_reported[(int)BarrierKind.Ready])
            lines.Add("Ready for the next day?  Press SPACE / Next Day");

        AddBarrierLine(lines, BarrierKind.Ready, "Next day");
        AddBarrierLine(lines, BarrierKind.Outside, "Day start - leave the elevator");
        AddBarrierLine(lines, BarrierKind.Inside, "Day end - enter the elevator");
        AddBarrierLine(lines, BarrierKind.Ending, "Ending - enter the exit elevator");

        if (Net.IsClient && !_inSync && GameStarted && !_reported[(int)BarrierKind.Outside] && LoopStates.IsOutsideWait(LoopState))
            lines.Add(LoopStates.IsDayInProgress(_hostFlow)
                ? "Day in progress - step out of the elevator to join"
                : "Waiting for the next day to start");
    }

    static void AddBarrierLine(List<string> lines, BarrierKind kind, string label)
    {
        int k = (int)kind;
        if (!_reported[k] && _statNeed[k] == 0) return;
        string counts = _statNeed[k] > 0 ? $"  {_statHave[k]}/{_statNeed[k]}" : "";
        lines.Add(_reported[k] ? $"{label}: waiting for the others{counts}" : $"{label}:{counts}");
    }
}
