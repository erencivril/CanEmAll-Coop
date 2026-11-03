using System;
using BepInEx.Configuration;
using UnityEngine;

namespace CanEmAllCoop;

public enum PaceMode { Neutral, Free }
public enum ReadyPolicy { All, Majority, AllWithTimeout }

/// <summary>
/// Every setting of DESIGN section 6. Properties read the live config entry, so edits via the config file or a
/// config manager apply without a restart. Before Init (unit tests, early load) they return the defaults.
/// </summary>
public static class CoopConfig
{
    static ConfigEntry<float> _workloadExponent;
    static ConfigEntry<PaceMode> _paceMode;
    static ConfigEntry<int> _perfCap;
    static ConfigEntry<bool> _scaleConveyor;
    static ConfigEntry<bool> _scaleFirstBloodCounters;
    static ConfigEntry<float> _respawnSeconds;
    static ConfigEntry<ReadyPolicy> _readyPolicy;
    static ConfigEntry<float> _readyTimeoutSeconds;
    static ConfigEntry<float> _maxBetFraction;
    static ConfigEntry<string> _menuKey;
    static ConfigEntry<int> _directPort;
    static ConfigEntry<int> _poseRateHz;
    static ConfigEntry<int> _enemyRateHz;
    static ConfigEntry<int> _canRateHz;
    static ConfigEntry<float> _peerTimeoutSeconds;
    static ConfigEntry<float> _steamConnectTimeoutSeconds;
    static ConfigEntry<bool> _steamFriendsOnly;
    static ConfigEntry<float> _stateLogSeconds;

    // [Balance]
    public static float WorkloadExponent => _workloadExponent?.Value ?? 0.8f;
    public static PaceMode PaceMode => _paceMode?.Value ?? PaceMode.Neutral;
    public static int PerfCap => _perfCap?.Value ?? 1000;
    public static bool ScaleConveyor => _scaleConveyor?.Value ?? false;
    public static bool ScaleFirstBloodCounters => _scaleFirstBloodCounters?.Value ?? false;

    // [Rules]
    public static float RespawnSeconds => _respawnSeconds?.Value ?? 5f;
    public static ReadyPolicy ReadyPolicy => _readyPolicy?.Value ?? ReadyPolicy.All;
    public static float ReadyTimeoutSeconds => _readyTimeoutSeconds?.Value ?? 60f;
    public static float MaxBetFraction => _maxBetFraction?.Value ?? 1f;

    // [Network]
    public static KeyCode MenuKey
    {
        get
        {
            var text = _menuKey?.Value ?? "F7";
            return Enum.TryParse<KeyCode>(text, true, out var key) && key != KeyCode.None ? key : KeyCode.F7;
        }
    }
    public static int DirectPort => _directPort?.Value ?? 27555;
    public static int PoseRateHz => _poseRateHz?.Value ?? 20;
    public static int EnemyRateHz => _enemyRateHz?.Value ?? 12;
    public static int CanRateHz => _canRateHz?.Value ?? 8;
    public static float PeerTimeoutSeconds => _peerTimeoutSeconds?.Value ?? 30f;
    public static float SteamConnectTimeoutSeconds => _steamConnectTimeoutSeconds?.Value ?? 25f;
    public static bool SteamFriendsOnly => _steamFriendsOnly?.Value ?? false;

    // [Debug]
    /// <summary>0 = off. The --coop-statelog command-line switch overrides this.</summary>
    public static float StateLogSeconds => _stateLogSeconds?.Value ?? 0f;

    internal static void Init(ConfigFile cfg)
    {
        _workloadExponent = cfg.Bind("Balance", "WorkloadExponent", 0.8f,
            new ConfigDescription("Exponent a of the player-count workload w(N) = N^a. 1.0 = linear (hardest to keep up), lower = easier. Enemies per truck, field cap and grinder speed are scaled by about w(N).",
                new AcceptableValueRange<float>(0.3f, 1.5f)));
        _paceMode = cfg.Bind("Balance", "PaceMode", PaceMode.Neutral,
            "Neutral: a team earns about as fast as a solo player (supply and grinder speed scale up, income is divided by the same factor). Free: only the enemy supply scales, the team simply earns what the shared machines allow.");
        _perfCap = cfg.Bind("Balance", "PerfCap", 1000,
            new ConfigDescription("Upper bound for registered enemies on the host (limits the scaled field cap and truck batch). Lower it if the host stutters at high player counts.",
                new AcceptableValueRange<int>(100, 5000)));
        _scaleConveyor = cfg.Bind("Balance", "ScaleConveyor", false,
            "Also speed up the conveyor belts with the player count (only if belts jam with the scaled enemy supply).");
        _scaleFirstBloodCounters = cfg.Bind("Balance", "ScaleFirstBloodCounters", false,
            "Scale the First Blood / Starting Shockwave per-day counters with the player count so the rebirth perks cover the same share of a bigger day.");

        _respawnSeconds = cfg.Bind("Rules", "RespawnSeconds", 5f,
            new ConfigDescription("Seconds until a downed player respawns while the team is still alive. If every player is down the day ends like a solo death.",
                new AcceptableValueRange<float>(0f, 60f)));
        _readyPolicy = cfg.Bind("Rules", "ReadyPolicy", ReadyPolicy.All,
            "Who must press ready before the next day / elevator moves on. All: every player. Majority: more than half. AllWithTimeout: every player, but the host continues after ReadyTimeoutSeconds.");
        _readyTimeoutSeconds = cfg.Bind("Rules", "ReadyTimeoutSeconds", 60f,
            new ConfigDescription("Seconds before ReadyPolicy=AllWithTimeout lets the host continue without the missing players.",
                new AcceptableValueRange<float>(5f, 600f)));
        _maxBetFraction = cfg.Bind("Rules", "MaxBetFraction", 1.0f,
            new ConfigDescription("Largest slot machine stake as a fraction of the shared wallet. 1.0 = no limit; 0.25 stops one player from wiping the team's money in one spin.",
                new AcceptableValueRange<float>(0.01f, 1f)));

        _menuKey = cfg.Bind("Network", "MenuKey", "F7",
            "Key that opens the co-op menu (any UnityEngine.KeyCode name, e.g. F7, F8, BackQuote).");
        _directPort = cfg.Bind("Network", "DirectPort", 27555,
            new ConfigDescription("TCP port used by Direct host / Direct join (LAN, VPN or port-forwarded play, and local two-instance testing).",
                new AcceptableValueRange<int>(1024, 65535)));
        _poseRateHz = cfg.Bind("Network", "PoseRateHz", 20,
            new ConfigDescription("Player pose updates per second sent to the other players.", new AcceptableValueRange<int>(5, 60)));
        _enemyRateHz = cfg.Bind("Network", "EnemyRateHz", 12,
            new ConfigDescription("Enemy transform updates per second the host sends.", new AcceptableValueRange<int>(2, 30)));
        _canRateHz = cfg.Bind("Network", "CanRateHz", 8,
            new ConfigDescription("Can / crate transform updates per second the host sends.", new AcceptableValueRange<int>(1, 30)));
        _peerTimeoutSeconds = cfg.Bind("Network", "PeerTimeoutSeconds", 30f,
            new ConfigDescription("Seconds without any packet from the other side before an established session is dropped. Frame hitches of 10-15 s must not end a session, so keep this at 30 or more.",
                new AcceptableValueRange<float>(10f, 300f)));
        _steamConnectTimeoutSeconds = cfg.Bind("Network", "SteamConnectTimeoutSeconds", 25f,
            new ConfigDescription("How long a Steam join waits for the host's answer before giving up (Steam may need 10-20 s to find a route).",
                new AcceptableValueRange<float>(5f, 120f)));
        _steamFriendsOnly = cfg.Bind("Network", "SteamFriendsOnly", false,
            "Host (Steam): only accept session requests from users on your Steam friends list. Off by default so joining by SteamID64 also works for someone you have not added yet.");

        _stateLogSeconds = cfg.Bind("Debug", "StateLogSeconds", 0f,
            new ConfigDescription("Write a [STATE] line (role, peers, per-module state) to the BepInEx log every N seconds. 0 = off.",
                new AcceptableValueRange<float>(0f, 3600f)));
    }
}
