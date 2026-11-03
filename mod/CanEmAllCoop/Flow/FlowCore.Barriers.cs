using System;
using System.Collections.Generic;
using CanEmAllCoop.Core;
using UnityEngine;

namespace CanEmAllCoop.Flow;
using CanEmAllCoop.Net;
using Net = CanEmAllCoop.Net.Net;

/// <summary>
/// Ready-up (shop), elevator-out (day start), elevator-in (day end) and ending quorums.
///
/// Elevators: the game calls GameManager.OnTriggerOutsideElevator / OnTriggerInsideElevator when the player walks into
/// the zone; both only act when elevatorState is 1 / 2 and then set triggerCompleted, which the GameLoop polls. Online
/// we swallow the call, report to the host and call the ORIGINAL once the host broadcasts GO (waiting until the local
/// loop has set elevatorState, so an early GO is not lost).
///
/// Shop: GameLoop states 21/22 (and SuicideCoroutine 3/4) poll Input.GetKeyDown(Space) / isNextDayClicked and then test
/// SlotMachine.IsSpinning. The MoveNext prefix answers "yield null" without running the iterator, so the loop cannot
/// leave until GO. The local "ready" is detected from the same inputs the game reads and only counts while the slot
/// machine is idle. (Cleaner than faking IsSpinning: no other system sees a wrong flag.)
/// </summary>
internal static partial class FlowCore
{
    const float AutoDelay = 3f;

    static readonly BarrierBoard _board = new();
    static readonly HashSet<ulong> _syncedIds = new();   // host: clients that take part in the running day cycle
    static readonly HashSet<ulong> _inGameIds = new();   // host: clients whose GameLoop runs (expected at the Outside barrier)
    /// <summary>Host: number of connected clients whose game is running (they play the day; a peer still in the menu does not). Pace latches N = 1 + this at day start.</summary>
    internal static int InGameCount => Net.IsHost ? _inGameIds.Count : 0;
    static readonly List<ulong> _expected = new();
    static readonly bool[] _reported = new bool[4];
    static readonly bool[] _goPending = new bool[4];
    static readonly float[] _autoSince = { -1f, -1f, -1f, -1f };
    static readonly int[] _statHave = new int[4];
    static readonly int[] _statNeed = new int[4];

    static bool _inSync;          // client: took part in a GO(Outside) of the host, so its loop runs in step
    static bool _readyGo;         // GO(Ready) received, not consumed
    static bool _shopReleased;    // GO consumed; the original runs until the loop leaves the shop
    static bool _shopHeld;
    static float _heldSince;

    static Vector3 _spawnPos;
    static float _spawnYaw;
    static bool _spawnValid;

    static double NowSec() => Environment.TickCount64 / 1000.0;

    static void ClearBarrierState()
    {
        _board.ResetAll();
        for (int i = 0; i < 4; i++) { _reported[i] = false; _goPending[i] = false; _statHave[i] = 0; _statNeed[i] = 0; _autoSince[i] = -1f; }
        _readyGo = false;
        _shopReleased = false;
        _shopHeld = false;
    }

    static void ClearTrigger(BarrierKind kind)
    {
        int k = (int)kind;
        _reported[k] = false;
        _goPending[k] = false;
        _autoSince[k] = -1f;
    }

    static string ReportedMask()
    {
        string s = "";
        if (_reported[0]) s += "O";
        if (_reported[1]) s += "I";
        if (_reported[2]) s += "R";
        if (_reported[3]) s += "E";
        return s.Length == 0 ? "-" : s;
    }

    // ------------------------------------------------------------------------------------------------ local reports

    internal static void ReportBarrier(BarrierKind kind)
    {
        int k = (int)kind;
        if (_reported[k]) return;
        _reported[k] = true;
        Log.Info($"Flow: local player ready at barrier {kind}");
        if (Net.IsHost) HostOnReport(Net.LocalPeer, kind);
        else Net.SendToHost(FlowMsg.BarrierReport, w => w.WriteByte((byte)kind));
    }

    /// <summary>Prefix of GameManager.OnTriggerOutsideElevator / OnTriggerInsideElevator. true = run the original.</summary>
    internal static bool ElevatorTrigger(BarrierKind kind, GameManager gm)
    {
        if (!Online || Bypass || !Playing || gm == null) return true;
        int need = kind == BarrierKind.Outside ? 1 : 2;
        if ((int)gm.elevatorState != need) return true;       // the original does nothing in this state
        if (kind == BarrierKind.Outside) RecordSpawn();
        ReportBarrier(kind);
        return false;
    }

    /// <summary>Prefix of ExitDoor.OnTriggerInsideElevator (the ending elevator). true = run the original.</summary>
    internal static bool ExitTrigger(ExitDoor door)
    {
        if (!Online || Bypass || !Playing || door == null) return true;
        if (door.elevatorStarted || !door.HasMasterKey()) return true;   // original is a no-op here
        ReportBarrier(BarrierKind.Ending);
        return false;
    }

    static void RecordSpawn()
    {
        var p = Player.Instance;
        if (p == null) return;
        _spawnPos = p.transform.position;
        _spawnYaw = p.transform.eulerAngles.y;
        _spawnValid = true;
    }

    // ------------------------------------------------------------------------------------------------ shop hold

    static bool SlotBusy()
    {
        var s = SlotMachine.Instance;
        return s != null && s.IsSpinning;
    }

    /// <summary>true = the iterator step is swallowed (the loop must not advance).</summary>
    static bool HoldShop()
    {
        if (_shopReleased) return false;
        var gm = GM;
        if (gm == null) return false;

        if (_readyGo)
        {
            _readyGo = false;
            _shopReleased = true;
            _shopHeld = false;
            _reported[(int)BarrierKind.Ready] = false;
            gm.isNextDayClicked = true;        // the original polls Space OR this flag, then re-checks the slot machine
            return false;
        }

        if (!_shopHeld) { _shopHeld = true; _heldSince = Time.unscaledTime; }

        bool ready = !SlotBusy() && (Input.GetKeyDown(KeyCode.Space) || gm.isNextDayClicked);
        if (!ready && LaunchArgs.AutoStart && Time.unscaledTime - _heldSince > AutoDelay) ready = true;
        if (ready) ReportBarrier(BarrierKind.Ready);
        return true;
    }

    // ------------------------------------------------------------------------------------------------ host side

    static void HostOnReport(Peer peer, BarrierKind kind)
    {
        if (peer == null) return;
        if (kind == BarrierKind.Inside || kind == BarrierKind.Ready || kind == BarrierKind.Ending)
        {
            // a peer that reaches the day-end/shop is certainly running the day cycle
            if (!peer.IsHost && _inGameIds.Contains(peer.Id)) _syncedIds.Add(peer.Id);
        }

        var timing = BarrierPhase.Classify(kind, CurrentFlow);
        if (timing == BarrierTiming.Late)
        {
            // the host already passed this barrier: a late joiner / laggard is released at once
            if (kind == BarrierKind.Outside && !peer.IsHost) _syncedIds.Add(peer.Id);
            Log.Info($"Flow: late report {kind} from {peer} (host flow {CurrentFlow}) -> released immediately");
            SendGo(peer, kind);
            return;
        }

        _board.Report(kind, peer.Id, NowSec());
        if (timing == BarrierTiming.Open) HostEvaluate(kind);
    }

    static void HostEvaluateBarriers()
    {
        for (int k = 0; k < 4; k++)
            if (_board.Any((BarrierKind)k)) HostEvaluate((BarrierKind)k);
    }

    static void ExpectedFor(BarrierKind kind, List<ulong> buf)
    {
        buf.Clear();
        var peers = Net.Peers;
        for (int i = 0; i < peers.Count; i++)
        {
            var p = peers[i];
            bool member;
            if (p.IsHost) member = GameStarted;
            else member = kind == BarrierKind.Outside ? _inGameIds.Contains(p.Id) : _syncedIds.Contains(p.Id);
            if (member) buf.Add(p.Id);
        }
    }

    static void HostEvaluate(BarrierKind kind)
    {
        if (!_board.Any(kind)) return;
        if (BarrierPhase.Classify(kind, CurrentFlow) != BarrierTiming.Open) return;
        ExpectedFor(kind, _expected);
        if (_expected.Count == 0) return;

        var policy = Policy();
        if (_board.IsMet(kind, policy, _expected, NowSec(), CoopConfig.ReadyTimeoutSeconds))
        {
            HostGo(kind);
            return;
        }

        int k = (int)kind;
        int have = _board.CountIn(kind, _expected);
        int need = Quorum.Required(policy, _expected.Count);
        if (_statHave[k] != have || _statNeed[k] != need)
        {
            _statHave[k] = have; _statNeed[k] = need;
            BroadcastStatus(kind, have, need);
        }
    }

    static void HostGo(BarrierKind kind)
    {
        int k = (int)kind;
        int day = CurrentDay();
        if (kind == BarrierKind.Outside)
            foreach (var id in _expected) _syncedIds.Add(id);
        _board.Reset(kind);
        Log.Info($"Flow: barrier {kind} met ({_expected.Count} players, policy {Policy()}) -> GO (day {day})");
        Net.Broadcast(FlowMsg.BarrierGo, w => WriteGo(w, kind, day));
        OnGo(kind, day);
        _statHave[k] = 0; _statNeed[k] = 0;
        BroadcastStatus(kind, 0, 0);
    }

    static void SendGo(Peer peer, BarrierKind kind)
    {
        int day = CurrentDay();
        if (peer.IsHost) { OnGo(kind, day); return; }
        Net.SendTo(peer, FlowMsg.BarrierGo, w => WriteGo(w, kind, day));
    }

    static void WriteGo(NetWriter w, BarrierKind kind, int day)
    {
        w.WriteByte((byte)kind);
        w.WriteInt(day);
        w.WriteBool(_spawnValid);
        if (_spawnValid) { w.WriteVec3(_spawnPos); w.WriteFloat(_spawnYaw); }
    }

    static void BroadcastStatus(BarrierKind kind, int have, int need)
    {
        Net.Broadcast(FlowMsg.BarrierStatus, w => { w.WriteByte((byte)kind); w.WriteByte((byte)have); w.WriteByte((byte)need); });
    }

    // ------------------------------------------------------------------------------------------------ network handlers

    internal static void OnBarrierReport(Peer from, NetReader r)
    {
        if (!Net.IsHost) return;
        var kind = (BarrierKind)r.ReadByte();
        if ((byte)kind > 3) return;
        HostOnReport(from, kind);
    }

    internal static void OnBarrierGo(Peer from, NetReader r)
    {
        if (Net.IsHost) return;
        var kind = (BarrierKind)r.ReadByte();
        int day = r.ReadInt();
        if (r.ReadBool())
        {
            _spawnPos = r.ReadVec3();
            _spawnYaw = r.ReadFloat();
            _spawnValid = true;
        }
        if ((byte)kind > 3) return;
        OnGo(kind, day);
    }

    internal static void OnBarrierStatus(Peer from, NetReader r)
    {
        if (Net.IsHost) return;
        int k = r.ReadByte();
        int have = r.ReadByte();
        int need = r.ReadByte();
        if (k > 3) return;
        _statHave[k] = have; _statNeed[k] = need;
    }

    // ------------------------------------------------------------------------------------------------ GO (all roles)

    internal static void OnGo(BarrierKind kind, int hostDay)
    {
        if (!Playing) return;
        int k = (int)kind;
        _statHave[k] = 0; _statNeed[k] = 0;

        if (kind == BarrierKind.Outside)
        {
            if (!Net.IsHost)
            {
                if (!_inGameSent) return;     // our game is not up yet; the host does not count us for this day
                _inSync = true;
            }
        }
        else if (!Net.IsHost && !_inSync)
        {
            return;                           // late joiner still in its first elevator: this barrier is not ours
        }

        if (kind == BarrierKind.Ready)
        {
            FixDay(hostDay);
            _readyGo = true;
        }
        else _goPending[k] = true;
    }

    static void FixDay(int hostDay)
    {
        if (Net.IsHost || hostDay < 0) return;
        try
        {
            var sd = SaveManager.Instance?.CurrentSaveData;
            if (sd != null && sd.day != hostDay)
            {
                Log.Warn($"Flow: local day {sd.day} differs from the host's {hostDay}; using the host's");
                sd.day = hostDay;
            }
        }
        catch (Exception e) { Log.Error("Flow: FixDay failed", e); }
    }

    /// <summary>Calls the original trigger methods once the local loop is waiting for them.</summary>
    static void ApplyPendingGo()
    {
        var gm = GM;
        if (gm != null)
        {
            TryApplyElevator(BarrierKind.Outside, gm, 1);
            TryApplyElevator(BarrierKind.Inside, gm, 2);
        }
        if (_goPending[(int)BarrierKind.Ending])
        {
            var door = UnityEngine.Object.FindObjectOfType<ExitDoor>();
            if (door != null)
            {
                _goPending[(int)BarrierKind.Ending] = false;
                _reported[(int)BarrierKind.Ending] = false;
                Bypass = true;
                try { door.OnTriggerInsideElevator(); }
                finally { Bypass = false; }
            }
        }
    }

    static void TryApplyElevator(BarrierKind kind, GameManager gm, int needState)
    {
        int k = (int)kind;
        if (!_goPending[k] || (int)gm.elevatorState != needState) return;
        _goPending[k] = false;
        _reported[k] = false;
        Bypass = true;
        try
        {
            if (kind == BarrierKind.Outside) gm.OnTriggerOutsideElevator();
            else gm.OnTriggerInsideElevator();
        }
        finally { Bypass = false; }
    }

    /// <summary>Session ended while we were holding a trigger back: let the local loop go on.</summary>
    static void ReleaseSwallowedTriggers()
    {
        try
        {
            var gm = GM;
            Bypass = true;
            try
            {
                if (gm != null)
                {
                    if (_reported[0] || _goPending[0]) gm.OnTriggerOutsideElevator();
                    if (_reported[1] || _goPending[1]) gm.OnTriggerInsideElevator();
                }
                if (_reported[3] || _goPending[3])
                {
                    var door = UnityEngine.Object.FindObjectOfType<ExitDoor>();
                    if (door != null) door.OnTriggerInsideElevator();
                }
            }
            finally { Bypass = false; }
        }
        catch (Exception e) { Log.Error("Flow: releasing swallowed triggers failed", e); }
    }

    // ------------------------------------------------------------------------------------------------ test / unattended play

    /// <summary>--coop-autostart: nobody walks, so report the elevator triggers after a short delay (the shop is handled in HoldShop).</summary>
    static void UpdateAutoPlay()
    {
        if (!LaunchArgs.AutoStart) return;
        var gm = GM;
        if (gm == null) return;
        int es = (int)gm.elevatorState;
        AutoElevator(BarrierKind.Outside, es == 1);
        AutoElevator(BarrierKind.Inside, es == 2);
    }

    static void AutoElevator(BarrierKind kind, bool waiting)
    {
        int k = (int)kind;
        if (!waiting || _reported[k]) { _autoSince[k] = -1f; return; }
        if (_autoSince[k] < 0f) { _autoSince[k] = _now; return; }
        if (_now - _autoSince[k] < AutoDelay) return;
        _autoSince[k] = -1f;
        if (kind == BarrierKind.Outside) RecordSpawn();
        ReportBarrier(kind);
    }

    // ------------------------------------------------------------------------------------------------ world

    /// <summary>The pause menu of any single player must not freeze the shared world (F5). The patch handles the call; this covers sessions that start while paused.</summary>
    static void KeepWorldRunning()
    {
        var gm = GM;
        if (gm == null || !gm.IsPaused) return;
        if (Time.timeScale != 1f) Time.timeScale = 1f;
        if (AudioListener.pause) AudioListener.pause = false;
    }
}
