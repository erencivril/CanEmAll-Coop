using System;
using System.Collections.Generic;
using CanEmAllCoop.Core;
using UnityEngine;

namespace CanEmAllCoop.UI;
using Net = CanEmAllCoop.Net.Net;
using SteamNative = CanEmAllCoop.Net.SteamNative;
using SteamIds = CanEmAllCoop.Net.SteamIds;

/// <summary>
/// IMGUI panel (F7 by default) plus a one-line HUD while online. Only GUI.Box / Label / Button with explicit Rects are
/// used (no GUILayout, no GUI.Window) so nothing needs IL2CPP delegate marshalling. GUI.TextField and GUI.BeginScrollView
/// are stripped in this build ("Method unstripping failed", every OnGUI event), so text input and the friend list are
/// built from Box / Label / Button plus Event.current key and wheel events.
/// Known limitation: in-game the game still receives mouse clicks and key presses while the menu is open.
/// </summary>
internal static class CoopMenu
{
    const float PanelX = 16f, PanelY = 16f, PanelW = 400f, Pad = 10f, RowH = 24f, Gap = 4f;
    const int VisibleFriendRows = 8;

    static bool _open;
    static string _direct = "127.0.0.1";
    static string _steamIdText = "";
    static readonly List<SteamNative.SteamFriend> Friends = new();
    static int _friendTop;          // first visible friend row
    static int _focus;              // text field that has the keyboard: 0 none, 1 SteamID64, 2 direct address
    static long _nextFriendScanMs;
    static bool _requestPresence;
    static long _copiedUntilMs;
    static bool _savedCursor;
    static CursorLockMode _savedLock;
    static bool _savedVisible;

    // --coop-join-steam: keep trying until joined (the host may still be starting) for at most 3 minutes
    static bool _launchJoinDone;
    static long _launchJoinNextMs, _launchJoinDeadlineMs;

    public static void HandleHotkey()
    {
        bool pressed;
        try { pressed = Input.GetKeyDown(CoopConfig.MenuKey); }
        catch (Exception) { return; } // legacy input unavailable: no hotkey, command-line driven only
        if (!pressed) return;

        SetOpen(!_open);
    }

    public static void SetOpen(bool open)
    {
        if (open == _open) return;
        _open = open;
        if (_open)
        {
            _nextFriendScanMs = 0;
            _requestPresence = true;
            _savedCursor = false;
        }
        else RestoreCursor();
    }

    /// <summary>--coop-join-steam=id: joins as soon as Steam is usable, retrying after each failed attempt.</summary>
    public static void TickLaunchJoin()
    {
        if (_launchJoinDone || !LaunchArgs.JoinSteam) return;
        long now = Environment.TickCount64;
        if (_launchJoinDeadlineMs == 0) _launchJoinDeadlineMs = now + 180_000;
        if (now > _launchJoinDeadlineMs)
        {
            _launchJoinDone = true;
            Log.Warn($"--coop-join-steam: gave up joining {LaunchArgs.JoinSteamId} after 3 minutes");
            return;
        }
        if (Net.IsOnline) { _launchJoinDone = true; return; }
        if (Net.Connecting || now < _launchJoinNextMs) return;
        _launchJoinNextMs = now + 4000;
        if (!SteamNative.Init()) return;
        Log.Info($"--coop-join-steam: joining {LaunchArgs.JoinSteamId}");
        Net.JoinSteam(LaunchArgs.JoinSteamId);
    }

    /// <summary>Set by Flow: the local player is walking around (day/night, not paused, not in a game menu), so the cursor belongs locked.</summary>
    public static Func<bool> InGameplay;

    static bool IsInGameplay()
    {
        try { return InGameplay != null && InGameplay(); }
        catch (Exception) { return false; }
    }

    static int _relocks;

    static void LockCursor()
    {
        if (Cursor.lockState == CursorLockMode.Locked && !Cursor.visible) return;
        if (_relocks++ < 5) Log.Info($"Cursor: locked for gameplay (was {Cursor.lockState}, visible={Cursor.visible})");
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    /// <summary>
    /// While the menu is open keep the cursor free. While it is closed and online, keep it locked during gameplay: the game only
    /// locks it on its own transitions, which a mid-day join (started by the mod, not by the menu's Play button) skips.
    /// </summary>
    public static void KeepCursorFree()
    {
        if (!_open)
        {
            if (Net.IsOnline && IsInGameplay()) LockCursor();
            return;
        }
        if (!_savedCursor)
        {
            _savedLock = Cursor.lockState;
            _savedVisible = Cursor.visible;
            _savedCursor = true;
        }
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    static void RestoreCursor()
    {
        if (!_savedCursor) return;
        _savedCursor = false;
        // the state saved when the menu opened goes stale if the game changed scene meanwhile (opened at the main menu, closed in game)
        if (IsInGameplay()) { LockCursor(); return; }
        Cursor.lockState = _savedLock;
        Cursor.visible = _savedVisible;
    }

    public static void Draw()
    {
        float scale = Mathf.Max(0.75f, Screen.height / 1080f);
        var saved = GUI.matrix;
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
        try
        {
            DrawHud(Screen.width / scale);
            if (_open) DrawPanel();
        }
        finally { GUI.matrix = saved; }
    }

    static void DrawHud(float virtualWidth)
    {
        if (!Net.IsOnline && !Net.Connecting) return;
        string text = Net.Connecting ? Net.Status : $"CO-OP {Net.Role}  {Net.PlayerCount} player(s)";
        var pace = CoopUI.GetPaceText();
        if (!Net.Connecting && !string.IsNullOrEmpty(pace)) text += "  " + pace;
        if (Net.IsClient)
        {
            int ping = Net.GetPingMs(Net.HostPeer);
            if (ping >= 0) text += $"  {ping} ms";
        }
        float w = Net.Connecting ? Mathf.Min(virtualWidth - 20f, 640f) : 360f;
        var r = new Rect((virtualWidth - w) / 2f, 6f, w, 22f);
        GUI.Box(r, "");
        GUI.Label(new Rect(r.x + 8f, r.y + 2f, r.width - 16f, 20f), text);
    }

    // The panel is laid out twice: once to measure the height for the background box, once to draw.
    static void DrawPanel()
    {
        float height = Content(false);
        GUI.Box(new Rect(PanelX, PanelY, PanelW, height + Pad * 2f), "");
        Content(true);
    }

    static float Content(bool draw)
    {
        float x = PanelX + Pad, w = PanelW - Pad * 2f;
        float y = PanelY + Pad;
        float start = y;

        void Label(string text)
        {
            if (draw) GUI.Label(new Rect(x, y, w, RowH), text);
            y += RowH;
        }
        // Label that wraps over as many rows as the text needs (about 6.4 px per character at the default font).
        void WrappedLabel(string text)
        {
            int lines = Math.Max(1, (int)Math.Ceiling(text.Length * 6.4f / w));
            float h = lines * 16f + 8f;
            if (draw) GUI.Label(new Rect(x, y, w, h), text);
            y += h;
        }
        bool Button(string text, bool enabled = true)
        {
            bool hit = false;
            if (draw)
            {
                bool was = GUI.enabled;
                GUI.enabled = enabled;
                hit = GUI.Button(new Rect(x, y, w, RowH), text);
                GUI.enabled = was;
            }
            y += RowH + Gap;
            return hit && enabled;
        }

        Label($"Can 'Em All Co-op   ({CoopConfig.MenuKey} closes)");
        WrappedLabel($"Status: {Net.Status}");
        var pace = CoopUI.GetPaceText();
        Label($"Role: {Net.Role}   Players: {Net.PlayerCount}" + (string.IsNullOrEmpty(pace) ? "" : "   " + pace));

        var peers = Net.Peers;
        for (int i = 0; i < peers.Count; i++)
        {
            var p = peers[i];
            int ping = Net.GetPingMs(p);
            Label($"  {(p.IsHost ? "[host] " : "")}{p.Name}{(p.Id == Net.LocalId ? " (you)" : "")}{(ping >= 0 ? $"  {ping} ms" : "")}");
        }

        y += Gap;
        bool steam = TrySteam();
        if (steam) OwnIdRow(draw, x, w, ref y);

        if (Net.IsOnline || Net.Connecting)
        {
            if (!string.IsNullOrEmpty(Net.LastRefusal)) WrappedLabel("! " + Net.LastRefusal);
            if (Button(Net.Connecting ? "Cancel" : "Leave") && draw) Net.Leave();
            return y - start;
        }

        if (Button(steam ? "Host (Steam)" : "Host (Steam unavailable - retrying)", steam) && draw) Net.StartHostSteam();

        if (steam)
        {
            RefreshFriends();
            Label($"Friends ({Friends.Count}) - hosting first:");
            FriendList(draw, x, w, ref y);
            WrappedLabel("Greyed = not hosting right now (or hidden by Steam). Ask them to press Host (Steam), then click again - or paste their SteamID64:");

            if (draw) _steamIdText = EditField(new Rect(x, y, w - 90f, RowH), _steamIdText ?? "", 80, 1);
            bool parsed = SteamIds.TryParse(_steamIdText, out var typedId);
            if (draw)
            {
                bool was = GUI.enabled;
                GUI.enabled = parsed;
                if (GUI.Button(new Rect(x + w - 86f, y, 86f, RowH), "Join") && parsed) Net.JoinSteam(typedId);
                GUI.enabled = was;
            }
            y += RowH + Gap;
        }

        y += Gap;
        if (Button($"Host (Direct, TCP {CoopConfig.DirectPort})") && draw) Net.StartHostDirect();

        Label("Direct join (ip or ip:port):");
        if (draw) _direct = EditField(new Rect(x, y, w - 90f, RowH), _direct, 64, 2);
        if (draw && GUI.Button(new Rect(x + w - 86f, y, 86f, RowH), "Join") && LaunchArgs.TrySplitEndpoint(_direct, out var host, out var port))
            Net.JoinDirect(host, port);
        y += RowH + Gap;

        if (!string.IsNullOrEmpty(Net.LastError)) WrappedLabel("! " + Net.LastError);
        return y - start;
    }

    /// <summary>The player's own SteamID64 (what a friend types into "Join by SteamID64") with a Copy button.</summary>
    static void OwnIdRow(bool draw, float x, float w, ref float y)
    {
        string id = SteamNative.LocalSteamId.ToString();
        if (draw)
        {
            GUI.Label(new Rect(x, y, w, RowH), "Your SteamID64 (give it to your friend):");
        }
        y += RowH;
        if (draw)
        {
            var box = new Rect(x, y, w - 90f, RowH);
            GUI.Box(box, "");
            GUI.Label(new Rect(box.x + 4f, box.y + 2f, box.width - 8f, RowH - 4f), id);
            bool copied = Environment.TickCount64 < _copiedUntilMs;
            if (GUI.Button(new Rect(x + w - 86f, y, 86f, RowH), copied ? "Copied" : "Copy"))
            {
                try { GUIUtility.systemCopyBuffer = id; _copiedUntilMs = Environment.TickCount64 + 2000; }
                catch (Exception e) { Log.Warn("Copy to clipboard failed (select the id in the field and press Ctrl+C): " + e.Message); }
            }
        }
        y += RowH + Gap;
    }

    /// <summary>Scrollable list of every friend: hosting ones normal, the rest dimmed but still clickable.</summary>
    static void FriendList(bool draw, float x, float w, ref float y)
    {
        if (Friends.Count == 0)
        {
            if (draw) GUI.Label(new Rect(x, y, w, RowH), "  (no Steam friends found)");
            y += RowH;
            return;
        }

        float rowStep = RowH + Gap;
        int rows = Math.Min(Friends.Count, VisibleFriendRows);
        float viewH = rows * rowStep;
        bool more = Friends.Count > rows;
        _friendTop = Math.Max(0, Math.Min(_friendTop, Friends.Count - rows));
        if (draw)
        {
            var view = new Rect(x, y, w, viewH);
            var ev = Event.current;
            if (more && ev != null && ev.type == EventType.ScrollWheel && view.Contains(ev.mousePosition))
            {
                _friendTop += ev.delta.y > 0f ? 1 : -1;
                _friendTop = Math.Max(0, Math.Min(_friendTop, Friends.Count - rows));
                ev.Use();
            }
            float innerW = more ? w - 26f : w;
            var oldColor = GUI.color;
            for (int i = 0; i < rows; i++)
            {
                var f = Friends[_friendTop + i];
                string tag = f.Hosting ? "   [hosting]" : f.InGame ? "   (in game, not hosting)" : f.PersonaState > 0 ? "" : "   (offline)";
                GUI.color = f.Hosting ? oldColor : new Color(oldColor.r, oldColor.g, oldColor.b, oldColor.a * 0.55f);
                if (GUI.Button(new Rect(x, y + i * rowStep, innerW, RowH), $"Join {f.Name}{tag}")) Net.JoinSteam(f.Id);
            }
            GUI.color = oldColor;
            if (more)
            {
                if (GUI.Button(new Rect(x + w - 24f, y, 24f, RowH), "^")) _friendTop--;
                if (GUI.Button(new Rect(x + w - 24f, y + viewH - rowStep, 24f, RowH), "v")) _friendTop++;
            }
        }
        y += viewH + Gap;
    }

    /// <summary>
    /// Text input without GUI.TextField: a box with the text and a caret while it has the keyboard. Click selects it,
    /// typing appends, Backspace deletes, Ctrl+V pastes the clipboard (first line), Enter / Escape / a click elsewhere release it.
    /// </summary>
    static string EditField(Rect r, string text, int maxLen, int id)
    {
        var ev = Event.current;
        if (ev != null && ev.type == EventType.MouseDown)
            _focus = r.Contains(ev.mousePosition) ? id : (_focus == id ? 0 : _focus);
        bool focused = _focus == id;
        if (focused && ev != null && ev.type == EventType.KeyDown)
        {
            if (ev.keyCode == KeyCode.Backspace || ev.character == (char)8) { if (text.Length > 0) text = text.Substring(0, text.Length - 1); ev.Use(); }
            else if (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter || ev.keyCode == KeyCode.Escape || ev.character == (char)10 || ev.character == (char)13) { _focus = 0; ev.Use(); }
            else if (ev.keyCode == KeyCode.V && (ev.control || ev.command))
            {
                string clip = null;
                try { clip = GUIUtility.systemCopyBuffer; } catch (Exception) { }
                if (!string.IsNullOrEmpty(clip))
                {
                    int nl = clip.IndexOfAny(new[] { (char)13, (char)10 });
                    if (nl >= 0) clip = clip.Substring(0, nl);
                    text = (text + clip.Trim());
                    if (text.Length > maxLen) text = text.Substring(0, maxLen);
                }
                ev.Use();
            }
            else if (ev.character >= ' ' && ev.character != 127 && text.Length < maxLen) { text += ev.character; ev.Use(); }
        }
        GUI.Box(r, "");
        bool caret = focused && (int)(Time.unscaledTime * 2f) % 2 == 0;
        GUI.Label(new Rect(r.x + 4f, r.y + 2f, r.width - 8f, r.height - 4f), focused ? text + (caret ? "|" : " ") : text);
        return text;
    }

    static bool TrySteam()
    {
        try { return SteamNative.Init(); }
        catch (Exception) { return false; }
    }

    static void RefreshFriends()
    {
        long now = Environment.TickCount64;
        if (now < _nextFriendScanMs) return;
        _nextFriendScanMs = now + 2000;
        SteamNative.GetFriends(Friends, _requestPresence);
        _requestPresence = false;
    }
}
