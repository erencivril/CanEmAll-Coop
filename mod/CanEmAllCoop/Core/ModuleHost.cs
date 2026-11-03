using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace CanEmAllCoop.Core;
using Net = CanEmAllCoop.Net.Net; // inside the namespace: a plain `using CanEmAllCoop.Net;` would resolve `Net` to the namespace

/// <summary>Owns the module list: Init, per-frame update, session/peer event fan-out, launch-arg actions and the [STATE] probe.</summary>
public static class ModuleHost
{
    static readonly List<ICoopModule> Modules = new();
    static readonly Dictionary<string, int> UpdateFailures = new();
    static bool _wired;
    static bool _launchHandled;
    static long _nextStateLogMs;
    static bool _bgForced;
    static bool _bgSaved;

    public static IReadOnlyList<ICoopModule> All => Modules;

    /// <summary>Factories (not instances) so a throwing constructor is isolated like a throwing Init.</summary>
    internal static void Init(IEnumerable<Func<ICoopModule>> factories, Harmony harmony)
    {
        foreach (var create in factories)
        {
            ICoopModule m = null;
            try
            {
                m = create();
                m.Init(harmony);
                Modules.Add(m);
                Log.Info($"Module {m.Name} initialised");
            }
            catch (Exception e)
            {
                // A module that throws from construction / Init is dropped entirely so the game keeps running vanilla for it.
                Log.Error($"Module {m?.Name ?? "?"} failed to initialise and is disabled", e);
            }
        }

        if (_wired) return;
        _wired = true;
        Net.SessionStarted += () => ForEach("OnSessionStart", m => m.OnSessionStart());
        Net.SessionEnded += () => ForEach("OnSessionEnd", m => m.OnSessionEnd());
        Net.PeerJoined += p => ForEach("OnPeerJoined", m => m.OnPeerJoined(p));
        Net.PeerLeft += p => ForEach("OnPeerLeft", m => m.OnPeerLeft(p));
    }

    /// <summary>Called every frame from CoopBehaviour.Update.</summary>
    internal static void Update()
    {
        Net.Update();
        KeepRunningInBackground();

        foreach (var m in Modules)
        {
            try { m.OnUpdate(); }
            catch (Exception e)
            {
                UpdateFailures.TryGetValue(m.Name, out var n);
                UpdateFailures[m.Name] = ++n;
                if (n <= 3 || n % 1000 == 0) Log.Error($"Module {m.Name} OnUpdate failed (x{n})", e);
            }
        }

        HandleLaunchArgs();
        StateLog();
    }

    // An unfocused window would stop Update (and with it the network pump) and the others would time us out.
    static void KeepRunningInBackground()
    {
        bool want = Net.IsOnline || Net.Connecting;
        if (want == _bgForced) return;
        _bgForced = want;
        try
        {
            if (want) { _bgSaved = Application.runInBackground; Application.runInBackground = true; }
            else Application.runInBackground = _bgSaved;
        }
        catch (Exception e) { Log.Error("runInBackground toggle failed", e); }
    }

    static void ForEach(string what, Action<ICoopModule> call)
    {
        foreach (var m in Modules)
        {
            try { call(m); }
            catch (Exception e) { Log.Error($"Module {m.Name} {what} failed", e); }
        }
    }

    static void HandleLaunchArgs()
    {
        if (_launchHandled) return;
        _launchHandled = true;
        if (LaunchArgs.HostDirect)
            Net.StartHostDirect(LaunchArgs.HostDirectPort);
        else if (LaunchArgs.JoinDirect)
            Net.JoinDirect(LaunchArgs.JoinHost, LaunchArgs.JoinPort, retrySeconds: 120);
    }

    static void StateLog()
    {
        float seconds = LaunchArgs.StateLogSeconds ?? CoopConfig.StateLogSeconds;
        if (seconds <= 0f) return;
        long now = Environment.TickCount64;
        if (now < _nextStateLogMs) return;
        _nextStateLogMs = now + (long)(seconds * 1000f);

        var sb = new StringBuilder(256);
        sb.Append("[STATE] role=").Append(Net.Role)
          .Append(" transport=").Append(Net.TransportName)
          .Append(" players=").Append(Net.PlayerCount)
          .Append(" peers=");
        var peers = Net.Peers;
        for (int i = 0; i < peers.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(peers[i].Name).Append('#').Append(peers[i].Index);
        }
        sb.Append(" sent=").Append(Net.BytesSent).Append(" recv=").Append(Net.BytesReceived)
          .Append(" | net: ").Append(Net.DebugState());
        foreach (var m in Modules)
        {
            string s;
            try { s = m.DebugState(); }
            catch (Exception e) { s = "debugstate-failed:" + e.GetType().Name; }
            sb.Append(" | ").Append(m.Name).Append(": ").Append(s);
        }
        Log.Info(sb.ToString());
    }
}
