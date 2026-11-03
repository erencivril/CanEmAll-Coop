using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CanEmAllCoop.Core;
using CanEmAllCoop.Economy;
using CanEmAllCoop.Enemies;
using CanEmAllCoop.Flow;
using HarmonyLib;
using UnityEngine;

namespace CanEmAllCoop.Debug;
using CanEmAllCoop.Net;
using Net = CanEmAllCoop.Net.Net;

/// <summary>
/// Test driver for the unattended two-instance runs. Everything here is switched on ONLY by extra launch arguments;
/// without any --coop-debug-* argument the module does nothing at all.
///   --coop-debug-autowalk            offline only: report the elevator triggers and ready up (no player input available)
///   --coop-debug-money=N             host/offline gives itself N money when a day starts
///   --coop-debug-autobuy[=seconds]   clicks the cheapest affordable unpurchased skill node (same call a mouse click ends up in)
///   --coop-debug-autohit[=seconds]   damages a random enemy through Enemy.Damage; every 4th hit kills
///   --coop-debug-screenshot=seconds  saves PNGs to game folder\coopshots\tag_NNN.png (--coop-debug-tag=name)
///   --coop-debug-teleport[=seconds] moves the local player next to the first enemy (host +x side, client -x side, facing each other) so avatars/enemies are on screen
///   --coop-debug-grind[=seconds]    host/offline: moves up to 3 dead enemies into the grinder (CanProcessor.point) every N seconds (what carrying bodies there does)
///   --coop-debug-die=seconds         calls GameManager.Suicide that many seconds into a day (the path a lethal hit takes); --coop-debug-diedays=1,2 limits it to the Nth day of this process
///   --coop-debug-steamcheck          logs the Steam flat API state and runs StartHostSteam() for a few seconds
///   --coop-debug-openmenu=seconds    opens the F7 co-op panel that many seconds after the process started (so a screenshot shows the status / refusal text)
///   --coop-debug-closemenu=seconds   closes it again that many seconds after the process started (opened at the main menu, closed in game: cursor check)
/// Scenario drivers (DebugDrivers.cs); they call the game's own methods and log numbers, run on whichever side gets the switch:
///   --coop-debug-grabbody[=seconds]  picks the nearest dead body up with Player.GrabDeadBody, walks the player with it for N seconds (default 6) and logs body-player
///                                    distance, then Player.ThrowHeldBody and logs the flight; "grabbody #n: RESULT" lines (client: fix 1)
///   --coop-debug-punch[=seconds]     every N seconds (default 3): stands in front of the nearest live enemy, aims the camera at it and starts Inventory.PunchDelayed
///                                    (what Inventory.Punch starts, ends in Enemy.Damage); logs hitsSent / hitsApplied deltas ("punch #n: HIT SENT / NO HIT")
///   --coop-debug-vacuum[=seconds]    every N seconds (default 6): Vacuum.Suck on the nearest can, then Vacuum.Throw aimed at the reward pipe; "vacuum #n" lines
///   --coop-debug-facing[=seconds]    every N seconds (default 5): mean angle between Enemy.visual.forward (and the root) and the walking direction of moving enemies
///   --coop-debug-skillcolors[=sec]   scans the skill tree every N seconds (default 1): purchased nodes must carry purchasedColor / purchasedIconColor
///   --coop-debug-holdwatch[=sec]     host: logs objects a remote player holds / has just thrown (pos, speed, kinematic) every N seconds (default 0.5) and every paid can
///   --coop-debug-nocarry             with --coop-debug-grind: do not carry cans to the reward pipe (leave them to the vacuum driver)
/// </summary>
public sealed class DebugModule : ICoopModule
{
    public string Name => "Debug";

    static bool _autoWalk, _steamCheck;
    static long _money;
    static float _dieAt; static readonly HashSet<int> _dieDays = new();
    static float _buyEvery, _hitEvery, _shotEvery, _tpEvery, _grindEvery, _openMenuAt, _closeMenuAt;
    bool _menuOpened, _menuClosed;
    static string _tag = "inst";

    float _nextGrind, _nextBuy, _nextHit, _nextShot, _nextTp, _walkSince = -1f;
    int _shotNo, _hitNo, _buys, _hits, _kills, _shotsTaken;
    FlowPhase _lastPhase = FlowPhase.None;
    string _shotDir;
    int _steamStage, _dayOrdinal;
    float _dayStart;
    bool _died;
    float _steamAt;
    bool _loggedArgs;
    float _ftAvg, _ftMax;
    readonly DebugDrivers _drivers = new();

    public void Init(Harmony harmony)
    {
        foreach (var raw in Environment.GetCommandLineArgs())
        {
            if (raw == null || !raw.StartsWith("--coop-debug-", StringComparison.OrdinalIgnoreCase)) continue;
            string name = raw, value = null;
            int eq = raw.IndexOf('=');
            if (eq >= 0) { name = raw.Substring(0, eq); value = raw.Substring(eq + 1); }
            switch (name.ToLowerInvariant())
            {
                case "--coop-debug-autowalk": _autoWalk = true; break;
                case "--coop-debug-steamcheck": _steamCheck = true; break;
                case "--coop-debug-openmenu": _openMenuAt = Secs(value, 30f); break;
                case "--coop-debug-closemenu": _closeMenuAt = Secs(value, 60f); break;
                case "--coop-debug-money": long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _money); break;
                case "--coop-debug-autobuy": _buyEvery = Secs(value, 4f); break;
                case "--coop-debug-autohit": _hitEvery = Secs(value, 1.5f); break;
                case "--coop-debug-screenshot": _shotEvery = Secs(value, 15f); break;
                case "--coop-debug-teleport": _tpEvery = Secs(value, 20f); break;
                case "--coop-debug-grind": _grindEvery = Secs(value, 5f); break;
                case "--coop-debug-die": _dieAt = Secs(value, 20f); break;
                case "--coop-debug-diedays":
                    foreach (var d in (value ?? "").Split(',')) if (int.TryParse(d, out var dn)) _dieDays.Add(dn);
                    break;
                case "--coop-debug-tag": if (!string.IsNullOrEmpty(value)) _tag = value; break;
                default: DebugDrivers.Parse(name.ToLowerInvariant(), value); break;
            }
        }
        _loggedArgs = false;
    }

    static float Secs(string v, float def) =>
        float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0f ? s : def;

    bool AnyActive => _autoWalk || _steamCheck || _money > 0 || _dieAt > 0 || _grindEvery > 0 || _tpEvery > 0 || _buyEvery > 0 || _hitEvery > 0 || _shotEvery > 0 || _openMenuAt > 0 || _drivers.Active;

    public void OnUpdate()
    {
        float now = Time.unscaledTime;
        float dt = Time.unscaledDeltaTime * 1000f;
        _ftAvg += (dt - _ftAvg) * 0.02f;
        if (dt > _ftMax) _ftMax = dt;
        if (!AnyActive) return;
        if (!_loggedArgs)
        {
            _loggedArgs = true;
            Log.Info($"[DEBUG] active: autowalk={_autoWalk} money={_money} autobuy={_buyEvery} autohit={_hitEvery} screenshot={_shotEvery} steamcheck={_steamCheck} tag={_tag}");
        }

        TrackPhase();
        if (_drivers.Active) _drivers.Tick(now);
        if (_autoWalk && !Net.IsOnline) AutoWalk(now);
        if (_buyEvery > 0f && now >= _nextBuy) { _nextBuy = now + _buyEvery; Safe(AutoBuy, "autobuy"); }
        if (_hitEvery > 0f && now >= _nextHit) { _nextHit = now + _hitEvery; Safe(AutoHit, "autohit"); }
        if (_dieAt > 0f && !_died && FlowCore.Phase == FlowPhase.Day && now - _dayStart >= _dieAt && (_dieDays.Count == 0 || _dieDays.Contains(_dayOrdinal)))
        {
            _died = true;
            Safe(() => { Log.Info($"[DEBUG] die: GameManager.Suicide on day ordinal {_dayOrdinal}, role {Net.Role}"); GameManager.Instance.Suicide(1f, (MailId)0); }, "die");
        }
        if (_grindEvery > 0f && !Net.IsClient && now >= _nextGrind) { _nextGrind = now + _grindEvery; Safe(Grind, "grind"); }
        if (_tpEvery > 0f && now >= _nextTp && FlowCore.Phase == FlowPhase.Day) { _nextTp = now + _tpEvery; Safe(Teleport, "teleport"); }
        if (_shotEvery > 0f && now >= _nextShot) { _nextShot = now + _shotEvery; Safe(Shot, "screenshot"); }
        if (_steamCheck) Safe(() => SteamCheck(now), "steamcheck");
        if (_openMenuAt > 0f && !_menuOpened && now >= _openMenuAt)
        {
            _menuOpened = true;
            Safe(() => { CanEmAllCoop.UI.CoopMenu.SetOpen(true); Log.Info("[DEBUG] co-op panel opened"); }, "openmenu");
        }
        if (_closeMenuAt > 0f && _menuOpened && !_menuClosed && now >= _closeMenuAt)
        {
            _menuClosed = true;
            Safe(() =>
            {
                CanEmAllCoop.UI.CoopMenu.SetOpen(false);
                Log.Info($"[DEBUG] co-op panel closed in phase {FlowCore.Phase}: cursor {Cursor.lockState}, visible={Cursor.visible}");
            }, "closemenu");
        }
    }

    static void Safe(Action a, string what)
    {
        try { a(); }
        catch (Exception e) { Log.Error($"[DEBUG] {what} failed", e); }
    }

    // ---------------------------------------------------------------------------------------------- money at day start

    void TrackPhase()
    {
        var p = FlowCore.Phase;
        if (p == _lastPhase) return;
        _lastPhase = p;
        if (p == FlowPhase.Day) { _dayOrdinal++; _dayStart = Time.unscaledTime; _died = false; }
        if (p != FlowPhase.Day || _money <= 0 || Net.IsClient) return;
        Safe(() =>
        {
            var mm = MoneyManager.Instance;
            if (mm == null) return;
            long before = mm.Money;
            mm.ChangeMoney(_money);
            Log.Info($"[DEBUG] day start: gave {_money} money ({before} -> {mm.Money})");
        }, "money");
    }

    // ---------------------------------------------------------------------------------------------- offline walker

    void AutoWalk(float now)
    {
        var gm = GameManager.Instance;
        if (gm == null) { _walkSince = -1f; return; }
        int es = (int)gm.elevatorState;
        int flow = FlowCore.CurrentFlow;
        bool shop = LoopStates.IsShopWait(flow);
        bool want = es == 1 || es == 2 || shop;
        if (!want) { _walkSince = -1f; return; }
        if (_walkSince < 0f) { _walkSince = now; return; }
        if (now - _walkSince < 3f) return;
        _walkSince = -1f;
        if (es == 1) { Log.Info("[DEBUG] autowalk: OnTriggerOutsideElevator"); gm.OnTriggerOutsideElevator(); }
        else if (es == 2) { Log.Info("[DEBUG] autowalk: OnTriggerInsideElevator"); gm.OnTriggerInsideElevator(); }
        else { Log.Info("[DEBUG] autowalk: next day"); gm.isNextDayClicked = true; }
    }

    // ---------------------------------------------------------------------------------------------- skill purchase

    void AutoBuy()
    {
        if (!FlowCore.GameStarted) return;
        var tree = GameAccess.Tree;
        var nodes = tree?.nodes;
        var mm = GameAccess.Money;
        if (nodes == null || mm == null) return;
        SkillNode best = null;
        long money = mm.Money;
        for (int i = 0; i < nodes.Length; i++)
        {
            var n = nodes[i];
            if (n == null || n.skill == null || n.skill.isPurchased || n.IsLocked) continue;
            if (n.skill.price > money || n.skill.price <= 0) continue;
            if (best == null || n.skill.price < best.skill.price) best = n;
        }
        if (best == null) return;
        _buys++;
        Log.Info($"[DEBUG] autobuy #{_buys}: clicking skill {(int)best.skill.skillId} ({best.skill.skillId}) price {best.skill.price} money {money} role {Net.Role}");
        best.OnClick();
    }

    // ---------------------------------------------------------------------------------------------- enemy hits

    void AutoHit()
    {
        if (!FlowCore.GameStarted) return;
        var pool = Candidates();
        if (pool.Count == 0) { NoTarget("autohit", 0, ClientPuppets.Count); return; }
        var t = pool[UnityEngine.Random.Range(0, pool.Count)];
        _hitNo++;
        bool kill = _hitNo % 4 == 0;
        float dmg = kill ? Math.Max(t.maxHealth * 10f, 1000f) : 5f;
        var pos = t.transform.position + Vector3.up;
        _hits++; if (kill) _kills++;
        Log.Info($"[DEBUG] autohit #{_hitNo}: {(kill ? "KILL" : "hit")} enemy at {pos} hp {t.health}/{t.maxHealth} dmg {dmg} role {Net.Role} candidates {pool.Count}");
        t.Damage(dmg, Vector3.forward, pos, 1f, true, true, null, false, true, 1f);
    }

    /// <summary>Live enemies: on a client the replicated puppets (taken from the puppet table: inactive ones are invisible to FindObjectsOfType), otherwise every enemy in the scene.</summary>
    static List<Enemy> Candidates()
    {
        var pool = new List<Enemy>();
        if (Net.IsClient)
        {
            foreach (var p in ClientPuppets.All)
                if (p.E != null && !p.E.IsDead) pool.Add(p.E);
        }
        else
        {
            foreach (var e in UnityEngine.Object.FindObjectsOfType<Enemy>())
                if (e != null && !e.IsDead) pool.Add(e);
        }
        if (Net.IsClient && pool.Count > 0 && !_loggedActive)
        {
            _loggedActive = true;
            var e0 = pool[0];
            var par = e0.transform.parent;
            Log.Info($"[DEBUG] first puppet: activeSelf={e0.gameObject.activeSelf} parent={(par != null ? par.name + "/active=" + par.gameObject.activeInHierarchy : "none")} inPool={Truck.Instance?.pooledEnemies?.Contains(e0)} activeInHierarchy={e0.gameObject.activeInHierarchy} pos={e0.transform.position} state={e0.state} puppets={pool.Count} FindObjectsOfType<Enemy>={UnityEngine.Object.FindObjectsOfType<Enemy>().Length}");
        }
        return pool;
    }
    static bool _loggedActive;

    float _nextNoTarget;
    void NoTarget(string what, int found, int puppets)
    {
        if (Time.unscaledTime < _nextNoTarget) return;
        _nextNoTarget = Time.unscaledTime + 10f;
        Log.Info($"[DEBUG] {what}: no target (FindObjectsOfType<Enemy>={found}, puppets known={puppets}, role {Net.Role})");
    }

    // ---------------------------------------------------------------------------------------------- teleport

    // ---------------------------------------------------------------------------------------------- grinder feed

    int _ground;
    void Grind()
    {
        if (!FlowCore.GameStarted) return;
        CanProcessor cp = null;
        foreach (var c in UnityEngine.Object.FindObjectsOfType<CanProcessor>())
            if (c != null && c.isOn && c.point != null) { cp = c; break; }
        if (cp == null) return;
        var target = cp.point.position;
        int moved = 0;
        foreach (var e in UnityEngine.Object.FindObjectsOfType<Enemy>())
        {
            if (e == null || !e.IsDead || moved >= 3) continue;
            var rb = e.rb;
            if (rb != null) { rb.velocity = Vector3.zero; rb.position = target; }
            e.transform.position = target;
            moved++;
        }
        if (moved > 0) { _ground += moved; Log.Info($"[DEBUG] grind: moved {moved} dead enemies to the grinder at {target} (total {_ground}) role {Net.Role}"); }

        // cans that came out of the grinder: carry them to the reward pipe like a player would
        if (DebugDrivers.NoCarry) return;
        RewardMachine rm = null;
        foreach (var r in UnityEngine.Object.FindObjectsOfType<RewardMachine>()) { rm = r; break; }   // FindObjectOfType<T> is stripped in this build
        if (rm == null || rm.point == null) return;
        int cans = 0, carried = 0;
        foreach (var c in UnityEngine.Object.FindObjectsOfType<Can>())
        {
            if (c == null || c.IsInPool || c.IsProcessed) continue;
            cans++;
            if (carried >= 6) continue;
            c.transform.position = rm.point.position;
            carried++;
        }
        if (cans > 0) Log.Info($"[DEBUG] grind: {cans} unpaid cans in the scene, carried {carried} to the reward pipe at {rm.point.position} (money {MoneyManager.Instance?.Money}) role {Net.Role}");
    }

    // Fixed spot in the hall (open floor, seen in earlier runs); both instances use it so they end up facing each other.
    static readonly Vector3 TeleportAnchor = new Vector3(-6.0f, 0.6f, -5.0f);

    void Teleport()
    {
        var player = Player.Instance;
        if (player == null) { Log.Info("[DEBUG] teleport: no Player.Instance"); return; }
        float side = Net.IsClient ? -1f : 1f;
        var pos = TeleportAnchor + new Vector3(3f * side, 0f, 0f);
        var look = Quaternion.LookRotation(new Vector3(-side, 0f, 0f));
        var rb = player.rb;
        if (rb != null) { rb.position = pos; rb.velocity = Vector3.zero; }
        player.transform.SetPositionAndRotation(pos, look);
        Log.Info($"[DEBUG] teleport: player -> {pos}, role {Net.Role}");
    }

    // ---------------------------------------------------------------------------------------------- screenshots

    void Shot()
    {
        if (_shotDir == null)
        {
            _shotDir = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "coopshots");
            Directory.CreateDirectory(_shotDir);
        }
        string file = Path.Combine(_shotDir, $"{_tag}_{++_shotNo:000}.png");
        // ScreenCapture.CaptureScreenshot(string) is broken in the interop (ReadOnlySpan marshalling): grab a texture and write it ourselves
        var tex = ScreenCapture.CaptureScreenshotAsTexture();
        File.WriteAllBytes(file, ImageConversion.EncodeToPNG(tex));
        UnityEngine.Object.Destroy(tex);
        _shotsTaken++;
        Log.Info($"[DEBUG] screenshot {file} flow={FlowCore.CurrentFlow} role={Net.Role}");
    }

    // ---------------------------------------------------------------------------------------------- steam flat API check

    void SteamCheck(float now)
    {
        if (_steamStage == 0)
        {
            if (now < 8f) return;
            _steamStage = 1;
            bool ok = SteamNative.Init();
            Log.Info($"[DEBUG] steamcheck: Init={ok} error={SteamNative.InitError ?? "-"} localSteamId={SteamNative.LocalSteamId} persona='{SteamNative.LocalName}' " +
                     $"friendCount={SteamNative.DebugFriendCount()} networkingMessages=0x{SteamNative.DebugMessagesPtr().ToInt64():X}");
            var list = new List<SteamNative.SteamFriend>();
            SteamNative.GetFriendsInGame(list);
            var sb = new StringBuilder();
            foreach (var f in list) sb.Append(f.Name).Append('(').Append(f.Id).Append(") ");
            Log.Info($"[DEBUG] steamcheck: friends in this game: {list.Count} {sb}");
            bool host = Net.StartHostSteam();
            Log.Info($"[DEBUG] steamcheck: StartHostSteam -> {host}, role={Net.Role}, transport={Net.TransportName}, status='{Net.Status}', lastError='{Net.LastError}'");
            Log.Info($"[DEBUG] steamcheck: rich presence {SteamNative.HostPresenceKey}='{SteamNative.DebugOwnHostPresence() ?? "(unset)"}' sessionRequestCallbackSet={SteamNative.SessionRequestCallbackSet}");
            _steamAt = now;
        }
        else if (_steamStage == 1 && now - _steamAt > 20f)
        {
            _steamStage = 2;
            Log.Info($"[DEBUG] steamcheck: after 20 s role={Net.Role} status='{Net.Status}' lastError='{Net.LastError}' sent={Net.PacketsSent} recv={Net.PacketsReceived}");
            Net.Leave();
            Log.Info($"[DEBUG] steamcheck: Leave() -> role={Net.Role}, rich presence {SteamNative.HostPresenceKey}='{SteamNative.DebugOwnHostPresence() ?? "(unset)"}' sessionRequestCallbackSet={SteamNative.SessionRequestCallbackSet}");
        }
    }

    public void OnSessionStart() { }
    public void OnSessionEnd() { }
    public void OnPeerJoined(Peer peer) { }
    public void OnPeerLeft(Peer peer) { }

    public string DebugState()
    {
        string ft = $"frameMs avg={_ftAvg:F1} max={_ftMax:F0}";
        _ftMax = 0f;
        return AnyActive ? $"buys={_buys} hits={_hits} kills={_kills} ground={_ground} shots={_shotsTaken} {ft}" : ft;
    }
}
