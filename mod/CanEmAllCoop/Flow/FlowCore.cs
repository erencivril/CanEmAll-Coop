using System;
using System.Collections.Generic;
using CanEmAllCoop.Core;
using CanEmAllCoop.Economy;
using CanEmAllCoop.Scaling;
using UnityEngine;

namespace CanEmAllCoop.Flow;
using CanEmAllCoop.Net;
using Net = CanEmAllCoop.Net.Net;

/// <summary>
/// Shared state and the per-frame driver of the flow module. Split over several files:
/// FlowCore (loop tracking, timer slaving, session), .Barriers (ready-up / elevators / ending), .Down (death, respawn,
/// team wipe), .Join (start from the host, autostart, save guard, pause), .Hud.
///
/// Principle: every peer runs the real GameManager.GameLoop. The host decides WHEN to move on. A peer is held at a wait
/// state (MoveNext prefix answers "yield null" without running the iterator body) or has the original trigger method
/// called once the host says go. Offline (and for a client that has not joined) every entry point returns before touching
/// anything.
/// </summary>
internal static partial class FlowCore
{
    /// <summary>true while this module itself calls an original game method that its own patch would intercept.</summary>
    internal static bool Bypass;

    static IntPtr _gmPtr, _smPtr;
    internal static bool GameStarted;     // the GameLoop of the current GameManager instance has run at least one step
    internal static int LoopState = -1;   // state the GameLoop iterator resumes from (-1 = not running)
    internal static int SuicideState = -1;
    static bool _suicideActive;

    static int _hostFlow = -1;            // client: last flow state the host reported
    static float _hostTimer;
    static int _hostTimerFlow = -1;
    static bool _hostTimerFresh;
    static float _nextTimerSend;
    static float _now;                    // Time.unscaledTime of this frame

    internal static FlowPhase Phase { get; private set; }
    /// <summary>Raised on every peer when the local GameLoop moves into another phase (day/night/shop...).</summary>
    internal static event Action<FlowPhase> PhaseChanged;

    internal static int CurrentFlow => LoopStates.Flow(LoopState, _suicideActive ? SuicideState : -1);

    static GameManager GM
    {
        get
        {
            var g = GameManager.Instance;
            return g != null ? g : null;
        }
    }

    internal static bool Online => Net.IsOnline;

    /// <summary>Online and taking part in the host's game (a client that has not started the hosted game is not).</summary>
    internal static bool Playing => Net.IsHost || (Net.IsClient && _hostedGame);

    /// <summary>The local player is walking around the factory: day/night, alive, not paused, no game menu on screen.</summary>
    internal static bool IsLocalGameplay()
    {
        if (Phase is not (FlowPhase.Day or FlowPhase.Night) || IsDown) return false;
        var gm = GM;
        if (gm == null || gm.IsPaused || Player.Instance == null) return false;
        var menu = Menu.Instance;
        if (menu != null && menu.menuPanel != null && menu.menuPanel.activeInHierarchy) return false;
        var settings = Settings.Instance;
        if (settings != null && settings.settingsPanel != null && settings.settingsPanel.activeInHierarchy) return false;
        return true;
    }

    // ------------------------------------------------------------------------------------------------ loop tracking

    /// <summary>MoveNext prefix of GameManager._GameLoop_d__178. Returns true = run the original.</summary>
    internal static bool LoopPrefix(GameManager._GameLoop_d__178 it, int state, ref bool result)
    {
        if (!Online || !Playing) return true;

        if (state == LoopStates.DayStartSetup && Net.IsHost)
        {
            Safe(Pace.Latch, "Pace.Latch");
            Safe(EconomyModule.BroadcastSync, "EconomyModule.BroadcastSync");   // clients verify their unlock lists once per day
        }

        if (LoopStates.IsShopWait(state) && HoldShop())
        {
            it.__2__current = null;
            result = true;
            return false;
        }
        return true;
    }

    /// <summary>MoveNext prefix of GameManager._SuicideCoroutine_d__177 (the day-abort path).</summary>
    internal static bool SuicidePrefix(GameManager._SuicideCoroutine_d__177 it, int state, ref bool result)
    {
        if (!Online || !Playing) return true;
        if (LoopStates.IsSuicideShopWait(state) && HoldShop())
        {
            it.__2__current = null;
            result = true;
            return false;
        }
        return true;
    }

    internal static void LoopPostfix(int newState)
    {
        if (newState == LoopState) return;       // the day timer yields in the same state every frame
        LoopState = newState;
        if (newState >= 0)
        {
            GameStarted = true;
            // a fresh GameLoop (day restart after a team wipe): the suicide coroutine is over
            if (_suicideActive) { _suicideActive = false; SuicideState = -1; }
        }
        AfterFlowChange();
    }

    internal static void SuicidePostfix(int newState)
    {
        _suicideActive = newState >= 0;
        if (newState == SuicideState) return;
        SuicideState = newState;
        if (_suicideActive) LoopState = -1;      // StopAllCoroutines killed the old GameLoop
        AfterFlowChange();
    }

    static void AfterFlowChange()
    {
        int flow = CurrentFlow;
        var p = LoopStates.PhaseOfFlow(flow);
        if (p != Phase)
        {
            Phase = p;
            Safe(() => PhaseChanged?.Invoke(p), "PhaseChanged");
        }

        if (!Online) return;

        bool inShop = LoopStates.IsShopFamily(LoopState) || (_suicideActive && LoopStates.IsSuicideShopFamily(SuicideState));
        if (!inShop) { _shopReleased = false; _shopHeld = false; _readyGo = false; }

        // trigger bookkeeping: drop a swallowed/pending trigger once the loop has left its wait
        if (flow >= 7 || flow < 3) ClearTrigger(BarrierKind.Outside);
        if (flow >= 17 || flow < 11) ClearTrigger(BarrierKind.Inside);

        if (!LoopStates.IsTimed(flow) && flow != LoopStates.DayStartSetup) EndDownPhase();

        if (Net.IsHost && Playing)
        {
            if (flow >= LoopStates.SuicideBase)
            {
                _board.Reset(BarrierKind.Outside);
                _board.Reset(BarrierKind.Inside);
            }
            BroadcastFlow();
        }
    }

    // ------------------------------------------------------------------------------------------------ per frame

    internal static void Update()
    {
        _now = Time.unscaledTime;
        TrackScene();
        UpdateAutoStart();
        UpdateLeaveToMenu();

        if (!Online) return;

        UpdateClientStart();
        if (!Playing) return;

        if (Net.IsHost) { HostBroadcastTimer(); HostEvaluateBarriers(); }
        else UpdateClientTimer();

        KeepWorldRunning();
        ApplyPendingGo();
        UpdateDown();
        UpdateAutoPlay();
    }

    static void TrackScene()
    {
        var gm = GM;
        var ptr = gm != null ? gm.Pointer : IntPtr.Zero;
        var sm = SaveManager.Instance;
        var smPtr = sm != null ? sm.Pointer : IntPtr.Zero;
        if (smPtr != IntPtr.Zero && smPtr != _smPtr)
        {
            _smPtr = smPtr;
            // a reloaded SaveManager has read save.sav from disk again, so the host data we applied is gone from memory
            if (_tainted && smPtr != _taintSm)
            {
                _tainted = false;
                Log.Info("Flow: SaveManager was reloaded - saving is allowed again");
            }
        }

        if (ptr == _gmPtr) return;
        bool hadOne = _gmPtr != IntPtr.Zero;
        _gmPtr = ptr;
        if (ptr == IntPtr.Zero) return;       // scene is being torn down; the new instance resets us
        if (hadOne) Log.Info("Flow: GameManager instance changed (scene reload) - resetting flow state");
        ResetForNewScene();
    }

    static void ResetForNewScene()
    {
        GameStarted = false;
        LoopState = -1;
        SuicideState = -1;
        _suicideActive = false;
        _hostFlow = -1;
        _hostedGame = false;
        _inGameSent = false;
        _inSync = false;
        _exitAt = -1f;
        _menuSeenAt = -1f;
        _autoStartDone = false;
        _shopReleased = false;
        _shopHeld = false;
        _readyGo = false;
        ClearBarrierState();
        ResetDownState();
        AfterFlowChange();
        ShowMenuNotice();
    }

    // ------------------------------------------------------------------------------------------------ timer slaving

    static void HostBroadcastTimer()
    {
        int flow = CurrentFlow;
        if (!LoopStates.IsTimed(flow) || Net.Peers.Count < 2) return;
        if (_now < _nextTimerSend) return;
        _nextTimerSend = _now + 0.5f;
        var gm = GM;
        if (gm == null) return;
        short f = (short)flow;
        float t = gm.Timer;
        Net.Broadcast(FlowMsg.Timer, w => { w.WriteShort(f); w.WriteFloat(t); }, reliable: false);
    }

    static void UpdateClientTimer()
    {
        if (!_inSync) return;
        var gm = GM;
        if (gm == null) return;
        var d = TimerSlave.Decide(CurrentFlow, _hostFlow, gm.Timer, _hostTimerFresh, _hostTimerFlow, _hostTimer);
        _hostTimerFresh = false;
        if (d.Action == TimerAction.Set) gm.Timer = d.Value;
        else if (d.Action == TimerAction.ForceZero) gm.Timer = 0f;
    }

    static void BroadcastFlow()
    {
        short f = (short)CurrentFlow;
        Net.Broadcast(FlowMsg.FlowState, w => w.WriteShort(f));
    }

    // ------------------------------------------------------------------------------------------------ session

    internal static void OnSessionStart()
    {
        ClearBarrierState();
        ResetDownState();
        _hostFlow = -1;
        _inSync = false;
        _hostedGame = false;
        _inGameSent = false;
        _exitAt = -1f;
        _nextStartRequest = 0f;
        _syncedIds.Clear();
        _inGameIds.Clear();
        _startGate.Clear();
        RestorePauseAfterSession();
        _noGameLogged = false;
        FlowOverlay.Ensure();
        // clients get their flow state with the first FlowState message; the host's is known locally
        Log.Info($"Flow: session started as {Net.Role} (game running: {GameStarted})");
    }

    /// <summary>
    /// While online AfterUpdatePause forces timeScale = 1 / audio running so one player's pause menu cannot freeze the shared
    /// world. When the session ends during such a pause the game would keep running behind its own pause menu: put the vanilla
    /// pause state back.
    /// </summary>
    static void RestorePauseAfterSession()
    {
        try
        {
            var gm = GM;
            if (gm == null || !gm.IsPaused) return;
            Time.timeScale = 0f;
            AudioListener.pause = true;
        }
        catch (Exception e) { Log.Error("Flow: restoring the pause state failed", e); }
    }

    internal static void OnSessionEnd()
    {
        bool wasHostedClient = _hostedGame && _tainted;
        // let a loop that waits for a trigger we swallowed continue on its own
        ReleaseSwallowedTriggers();
        EndDownPhase();
        _shopReleased = false;
        _shopHeld = false;
        _readyGo = false;
        _hostedGame = false;
        _inGameSent = false;
        _inSync = false;
        _hostFlow = -1;
        ClearBarrierState();
        ResetDownState();
        _syncedIds.Clear();
        _inGameIds.Clear();
        if (wasHostedClient) BeginLeaveToMenu("session ended while playing the host's game");
        else if (_tainted) Log.Warn("Flow: session ended; saving stays disabled until the game is restarted so the host's data cannot overwrite your own save");
    }

    internal static void OnPeerJoined(Peer peer)
    {
        if (!Net.IsHost || peer == null) return;
        // not in game / synced until it tells us (StartRequest -> HostStart -> InGame)
        _syncedIds.Remove(peer.Id);
        _inGameIds.Remove(peer.Id);
        Log.Info($"Flow: {peer} joined (host flow {CurrentFlow}, phase {Phase})");
    }

    internal static void OnPeerLeft(Peer peer)
    {
        if (!Net.IsHost || peer == null) return;
        _syncedIds.Remove(peer.Id);
        _inGameIds.Remove(peer.Id);
        _board.Remove(peer.Id);
        HostPeerGone(peer.Id);
        Log.Info($"Flow: {peer} left");
    }

    internal static string DebugState()
    {
        var sb = new System.Text.StringBuilder(200);
        sb.Append("phase=").Append(Phase).Append(" flow=").Append(CurrentFlow);
        if (Net.IsClient) sb.Append(" hostFlow=").Append(_hostFlow).Append(" joined=").Append(_hostedGame ? 1 : 0).Append(" sync=").Append(_inSync ? 1 : 0);
        if (Net.IsHost) sb.Append(" synced=").Append(_syncedIds.Count).Append(" inGame=").Append(_inGameIds.Count);
        var gm = GM;
        if (gm != null) sb.Append(" timer=").Append(gm.Timer.ToString("0.0")).Append(" day=").Append(CurrentDay());
        sb.Append(" down=").Append(_down ? 1 : 0).Append('/').Append(Net.IsHost ? _tracker.Count : _downSet.Count);
        sb.Append(" held=").Append(_shopHeld ? 1 : 0).Append(" rep=").Append(ReportedMask());
        if (_tainted) sb.Append(" tainted=1");
        if (_saveBlockedCount > 0) sb.Append(" saveBlocked=").Append(_saveBlockedCount);
        sb.Append(" patches=").Append(FlowPatches.Summary);
        return sb.ToString();
    }

    // ------------------------------------------------------------------------------------------------ helpers

    internal static int CurrentDay()
    {
        try
        {
            var sm = SaveManager.Instance;
            var sd = sm != null ? sm.CurrentSaveData : null;
            return sd != null ? sd.day : -1;
        }
        catch (Exception) { return -1; }
    }

    internal static void Safe(Action a, string what)
    {
        try { a(); }
        catch (Exception e) { Log.Error($"Flow: {what} failed", e); }
    }

    static int _patchErrors;
    internal static void PatchError(string where, Exception e)
    {
        _patchErrors++;
        if (_patchErrors <= 5 || _patchErrors % 500 == 0) Log.Error($"Flow: patch {where} threw (x{_patchErrors}), falling back to the original", e);
    }

    static QuorumPolicy Policy()
    {
        switch (CoopConfig.ReadyPolicy)
        {
            case ReadyPolicy.Majority: return QuorumPolicy.Majority;
            case ReadyPolicy.AllWithTimeout: return QuorumPolicy.AllWithTimeout;
            default: return QuorumPolicy.All;
        }
    }
}
