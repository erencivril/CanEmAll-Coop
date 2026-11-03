using System;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using CanEmAllCoop.Core;

namespace CanEmAllCoop.Scaling;

/// <summary>
/// Settings that belong to the scaling module only. They are bound on the plugin's own ConfigFile (the one CoopConfig
/// uses), looked up through the chainloader because the plugin instance is not registered yet while Plugin.Load runs;
/// the first successful call happens a few frames later from ScalingModule.OnUpdate. Until then (and if the lookup ever
/// fails) the defaults apply.
/// </summary>
internal static class ScalingConfig
{
    static ConfigEntry<float> _incomeExponent;
    static ConfigEntry<int> _maxSpawnBatch;
    static long _nextTry;
    static int _failures;

    /// <summary>Income divisor = k^exponent (1.0 = the plan's M1). Lower pays the team more, higher less; 0 turns the division off.</summary>
    public static float IncomeDivisorExponent
    {
        get
        {
            if (_incomeExponent == null) Ensure();
            return _incomeExponent?.Value ?? 1f;
        }
    }

    /// <summary>Largest number of enemies one truck delivery may create (the delivery is spawned in a single frame).</summary>
    public static int MaxSpawnBatch
    {
        get
        {
            if (_maxSpawnBatch == null) Ensure();
            return _maxSpawnBatch?.Value ?? PaceMath.DefaultMaxSpawnBatch;
        }
    }

    public static void Ensure()
    {
        if (_incomeExponent != null && _maxSpawnBatch != null) return;
        long now = Environment.TickCount64;
        if (now < _nextTry) return;
        _nextTry = now + 3000;
        try
        {
            var chain = IL2CPPChainloader.Instance;
            if (chain == null || !chain.Plugins.TryGetValue(Plugin.Guid, out var info) || info.Instance is not BasePlugin plugin) return;
            _maxSpawnBatch = plugin.Config.Bind("Balance", "MaxSpawnBatch", PaceMath.DefaultMaxSpawnBatch,
                new ConfigDescription("Largest truck delivery (enemies created in ONE frame) the player-count scaling may ask for, on top of PerfCap. A 4-player late game would otherwise spawn 758 enemies in a single frame and hitch. Raise it if your machine copes; k (the pace factor) follows the batch that is really delivered.",
                    new AcceptableValueRange<int>(1, 5000)));
            _incomeExponent = plugin.Config.Bind("Balance", "IncomeDivisorExponent", 1.0f,
                new ConfigDescription("Exponent of the income divisor: team income is divided by k^exponent, k = realized enemy-supply ratio. 1.0 keeps a team's pace equal to a solo player's (plan M1). Below 1 the team earns faster, above 1 slower, 0 switches the division off.",
                    new AcceptableValueRange<float>(0f, 2f)));
        }
        catch (Exception e)
        {
            if (++_failures <= 3) Log.Error("[Scaling] could not bind IncomeDivisorExponent (default 1.0 is used)", e);
        }
    }
}
