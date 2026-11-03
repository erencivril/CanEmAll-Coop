using System;
using CanEmAllCoop.Core;
using CanEmAllCoop.Economy;
using UnityEngine;

namespace CanEmAllCoop.Flow;
using CanEmAllCoop.Net;
using Net = CanEmAllCoop.Net.Net;

/// <summary>
/// Join path, --coop-autostart, the save guard and the pause rule.
///
/// Join (client): main menu is ready -> StartRequest (every 2 s) -> host answers HostStart(snapshot json) -> the snapshot
/// replaces CurrentSaveData in memory (EconomySnapshot.ApplySnapshot, flagged Net.Applying) -> Menu.ContinueGame, which
/// is just GameManager.StartGame: StartGameCoroutine restores money/mails/skill tree from CurrentSaveData and starts the
/// GameLoop -> InGame message. From then on the client's save is blocked until the game is restarted.
///
/// Late joiner: the client's own GameLoop plays the first elevator ride and waits there (elevatorState 1). Its player
/// walks out when it wants to; if the host is in the middle of a day that report is answered with an immediate GO
/// (BarrierPhase.Late), otherwise it simply waits and is part of the next day's Outside barrier.
/// </summary>
internal static partial class FlowCore
{
    static bool _hostedGame;       // client: HostStart applied, the hosted game was started
    static bool _inGameSent;
    static float _nextStartRequest;
    static float _exitAt = -1f;
    static float _menuSeenAt = -1f;
    static bool _autoStartDone;
    static bool _noGameLogged;
    static bool _tainted;          // this process holds host data in memory: never write it to save.sav
    static IntPtr _taintSm;
    static int _saveBlockedCount;
    static readonly Economy.RequestGate _startGate = new();   // host: a waiting client asks every 2 s; building the snapshot is not free

    // Hosted client whose session ended: the process still holds the HOST's save in memory and saving is blocked, so the
    // player is sent back to the main menu (scene reload = SaveManager reads the player's own save.sav again).
    const float LeaveDelaySeconds = 4f;
    static bool _leavePending;
    static float _leaveAt;
    static bool _menuNoticePending;
    static string[] _notice = System.Array.Empty<string>();
    static float _noticeUntil;

    internal static string JoinStatus { get; private set; } = "";

    // ------------------------------------------------------------------------------------------------ menu

    /// <summary>The main menu is on screen and the singletons it needs exist. Needs a second of calm so Menu.Awake has finished.</summary>
    static bool MenuReady()
    {
        bool ready = false;
        try
        {
            var menu = Menu.Instance;
            var panel = menu != null ? menu.menuPanel : null;
            ready = menu != null && panel != null && panel.activeInHierarchy
                    && GM != null && SaveManager.Instance != null && !GameStarted;
        }
        catch (Exception) { ready = false; }

        if (!ready) { _menuSeenAt = -1f; return false; }
        if (_menuSeenAt < 0f) _menuSeenAt = Time.unscaledTime;
        return Time.unscaledTime - _menuSeenAt >= 1f;
    }

    static void StartGameFromMenu(bool continueSave)
    {
        var menu = Menu.Instance;
        if (menu != null)
        {
            if (continueSave) menu.ContinueGame(); else menu.NewGame();
        }
        else if (GM != null) GM.StartGame();
    }

    // ------------------------------------------------------------------------------------------------ leave to menu

    /// <summary>Session ended on a client that was playing the host's game (host quit, connection lost, own Leave).</summary>
    static void BeginLeaveToMenu(string why)
    {
        _leavePending = true;
        _leaveAt = Time.unscaledTime + LeaveDelaySeconds;
        _menuNoticePending = true;
        Log.Warn($"Flow: {why}; this process holds the host's save in memory, so the game returns to the main menu in {LeaveDelaySeconds:0}s (your own save.sav was not touched)");
    }

    static void UpdateLeaveToMenu()
    {
        if (_leavePending)
        {
            float left = _leaveAt - _now;
            if (Online) _leavePending = false;                       // joined something again: UpdateClientStart takes over
            else if (!GameStarted) { if (left < -30f) _leavePending = false; }   // already back in the menu
            else if (left > 0f)
            {
                _notice = new[]
                {
                    $"The host's game ended - back to the main menu in {Mathf.CeilToInt(left)}s",
                    "Your own save is not touched.",
                };
                _noticeUntil = _now + 1f;
            }
            else
            {
                _leavePending = false;
                try { GM?.Exit(); }
                catch (Exception e) { Log.Error("Flow: GameManager.Exit failed - go back to the main menu manually before you play your own save", e); }
            }
        }
    }

    /// <summary>The scene was reloaded after a hosted game: tell the player what state their save is in.</summary>
    static void ShowMenuNotice()
    {
        if (!_menuNoticePending) return;
        _menuNoticePending = false;
        _notice = _tainted
            ? new[] { "You left the hosted game.", "Restart the game before you save your own progress." }
            : new[] { "You left the hosted game.", "Your own save is loaded again - press Continue." };
        _noticeUntil = Time.unscaledTime + 15f;
    }

    internal static bool HasNotice => _noticeUntil > Time.unscaledTime && _notice.Length > 0;

    // ------------------------------------------------------------------------------------------------ error toast

    const float ToastSeconds = 7f;
    static string _toast = "";
    static float _toastUntil;

    internal static bool HasToast => _toastUntil > Time.unscaledTime && _toast.Length > 0;
    internal static string ToastText => _toast;

    /// <summary>Refusal / join failure / lost connection: shown on screen for a few seconds, no need to open the F7 panel.</summary>
    internal static void ShowToast(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _toast = text;
        _toastUntil = Time.unscaledTime + ToastSeconds;
        FlowOverlay.Ensure();
    }

    // ------------------------------------------------------------------------------------------------ --coop-autostart

    static void UpdateAutoStart()
    {
        if (!LaunchArgs.AutoStart || _autoStartDone || GameStarted) return;
        if (LaunchArgs.JoinDirect || LaunchArgs.JoinSteam || Net.IsClient || Net.Connecting) return;     // clients start through the join path
        if (!MenuReady()) return;
        _autoStartDone = true;
        bool hasSave = false;
        try { hasSave = SaveManager.Instance.HasSave(); } catch (Exception) { }
        Log.Info($"Flow: --coop-autostart -> {(hasSave ? "Continue" : "New Game")}");
        StartGameFromMenu(hasSave);
    }

    // ------------------------------------------------------------------------------------------------ client join

    static void UpdateClientStart()
    {
        if (!Net.IsClient || _hostedGame)
        {
            if (_hostedGame && GameStarted && !_inGameSent) SendInGame();
            JoinStatus = "";
            return;
        }

        if (GameStarted)
        {
            // already inside a solo game: go back to the menu (reloads the scene; save.sav on disk is untouched)
            if (_exitAt < 0f) { _exitAt = Time.unscaledTime + 1.5f; JoinStatus = "Leaving your solo game to join the host..."; Log.Info("Flow: client is in a solo game - returning to the main menu to join"); }
            else if (Time.unscaledTime >= _exitAt)
            {
                _exitAt = float.MaxValue;
                try { GM?.Exit(); }
                catch (Exception e) { Log.Error("Flow: GameManager.Exit failed - go back to the main menu manually to join", e); }
            }
            return;
        }

        if (!MenuReady()) { JoinStatus = "Waiting for the main menu..."; return; }
        JoinStatus = "Waiting for the host's game...";
        if (_now < _nextStartRequest) return;
        _nextStartRequest = _now + 2f;
        Net.SendToHost(FlowMsg.StartRequest, w => { });
    }

    static void SendInGame()
    {
        _inGameSent = true;
        Net.SendToHost(FlowMsg.InGame, w => { });
        Log.Info("Flow: client game is running, told the host");
    }

    static void StartClientGame(string json)
    {
        // never let the host's data reach our own save.sav
        _tainted = true;
        var sm = SaveManager.Instance;
        _taintSm = sm != null ? sm.Pointer : IntPtr.Zero;

        if (string.IsNullOrEmpty(json)) Log.Warn("Flow: the host sent an empty snapshot - starting with the local save (economy module missing?)");
        else
        {
            Net.Applying = true;
            // StartGame's own restore chain replays the save: a full ApplySnapshot here would apply every ADD-type skill twice
            try { EconomySnapshot.PrepareSnapshotForGameStart(json); }
            catch (Exception e) { Log.Error("Flow: applying the host snapshot failed", e); }
            finally { Net.Applying = false; }
        }

        _hostedGame = true;
        _inGameSent = false;
        JoinStatus = "Starting the hosted game...";
        Log.Info($"Flow: starting the hosted game (snapshot {(json?.Length ?? 0)} chars, host flow {_hostFlow})");
        StartGameFromMenu(true);
    }

    // ------------------------------------------------------------------------------------------------ network handlers

    internal static void OnStartRequest(Peer from, NetReader r)
    {
        if (!Net.IsHost) return;
        if (!_startGate.TryEnter(from.Index, (long)(Time.unscaledTime * 1000f), 1500)) return;
        if (!GameStarted)
        {
            if (!_noGameLogged) { _noGameLogged = true; Log.Info($"Flow: {from} wants to join but the host has not started a game yet"); }
            return;                                   // the client asks again in 2 s
        }
        string json = "";
        try { json = EconomySnapshot.BuildSnapshotJson() ?? ""; }
        catch (Exception e) { Log.Error("Flow: building the snapshot failed", e); }
        short flow = (short)CurrentFlow;
        int day = CurrentDay();
        Log.Info($"Flow: sending the game snapshot to {from} ({json.Length} chars, flow {flow}, day {day})");
        Net.SendTo(from, FlowMsg.HostStart, w => { w.WriteString(json); w.WriteShort(flow); w.WriteInt(day); });
    }

    internal static void OnHostStart(Peer from, NetReader r)
    {
        if (Net.IsHost || _hostedGame) return;
        string json = r.ReadString();
        _hostFlow = r.ReadShort();
        r.ReadInt();                                  // host day (the snapshot carries it)
        if (!MenuReady()) return;                     // the next StartRequest brings a fresh snapshot
        StartClientGame(json);
    }

    internal static void OnInGame(Peer from, NetReader r)
    {
        if (!Net.IsHost) return;
        _inGameIds.Add(from.Id);
        short flow = (short)CurrentFlow;
        Net.SendTo(from, FlowMsg.FlowState, w => w.WriteShort(flow));
        Log.Info($"Flow: {from} is in game (host flow {flow}, phase {Phase})");
    }

    internal static void OnFlowState(Peer from, NetReader r)
    {
        if (Net.IsHost) return;
        _hostFlow = r.ReadShort();
    }

    internal static void OnTimer(Peer from, NetReader r)
    {
        if (Net.IsHost) return;
        _hostTimerFlow = r.ReadShort();
        _hostTimer = r.ReadFloat();
        _hostTimerFresh = true;
    }

    // ------------------------------------------------------------------------------------------------ save guard (patch bodies)

    /// <summary>Prefix of SaveManager.Save. true = write the file.</summary>
    internal static bool AllowSave()
    {
        if (_tainted || Net.IsClient)
        {
            _saveBlockedCount++;
            return false;
        }
        return true;
    }

    // ------------------------------------------------------------------------------------------------ pause (patch body)

    /// <summary>Postfix of GameManager.UpdatePause: keep the pause menu local, undo the two global writes.</summary>
    internal static void AfterUpdatePause()
    {
        if (!Online) return;
        Time.timeScale = 1f;
        AudioListener.pause = false;
    }
}
