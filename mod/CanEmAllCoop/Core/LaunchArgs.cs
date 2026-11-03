using System;
using System.Globalization;
using CanEmAllCoop.Net;

namespace CanEmAllCoop.Core;

/// <summary>
/// Command-line switches for automated tests (DESIGN section 6). Pure C# on purpose: the unit tests compile this file.
/// --coop-host-direct[=port]  --coop-join-direct=ip:port  --coop-join-steam=steamid64  --coop-autostart  --coop-statelog=seconds
/// </summary>
public static class LaunchArgs
{
    public static bool HostDirect { get; private set; }
    /// <summary>0 = use CoopConfig.DirectPort.</summary>
    public static int HostDirectPort { get; private set; }
    public static bool JoinDirect { get; private set; }
    public static string JoinHost { get; private set; }
    /// <summary>0 = use CoopConfig.DirectPort.</summary>
    public static int JoinPort { get; private set; }
    /// <summary>--coop-join-steam=&lt;SteamID64&gt; was given (and parsed): join that host over Steam as soon as Steam is ready.</summary>
    public static bool JoinSteam { get; private set; }
    public static ulong JoinSteamId { get; private set; }
    public static bool AutoStart { get; private set; }
    /// <summary>null = not given on the command line (the config value applies).</summary>
    public static float? StateLogSeconds { get; private set; }

    public static void Parse(string[] args)
    {
        HostDirect = false; HostDirectPort = 0; JoinDirect = false; JoinHost = null; JoinPort = 0;
        JoinSteam = false; JoinSteamId = 0;
        AutoStart = false; StateLogSeconds = null;
        if (args == null) return;

        foreach (var raw in args)
        {
            if (raw == null || !raw.StartsWith("--coop-", StringComparison.OrdinalIgnoreCase)) continue;
            string name = raw, value = null;
            int eq = raw.IndexOf('=');
            if (eq >= 0) { name = raw.Substring(0, eq); value = raw.Substring(eq + 1); }

            switch (name.ToLowerInvariant())
            {
                case "--coop-host-direct":
                    HostDirect = true;
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port > 0 && port < 65536)
                        HostDirectPort = port;
                    break;
                case "--coop-join-direct":
                    if (TrySplitEndpoint(value, out var host, out var jport))
                    {
                        JoinDirect = true; JoinHost = host; JoinPort = jport;
                    }
                    break;
                case "--coop-join-steam":
                    if (SteamIds.TryParse(value, out var steamId))
                    {
                        JoinSteam = true; JoinSteamId = steamId;
                    }
                    break;
                case "--coop-autostart":
                    AutoStart = true;
                    break;
                case "--coop-statelog":
                    if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s >= 0f)
                        StateLogSeconds = s;
                    break;
            }
        }
    }

    /// <summary>"host:port" or just "host" (port 0 = default). IPv6 literals are not supported.</summary>
    public static bool TrySplitEndpoint(string text, out string host, out int port)
    {
        host = null; port = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        int colon = text.LastIndexOf(':');
        if (colon < 0) { host = text; return true; }
        if (colon == 0 || colon == text.Length - 1) return false;
        if (!int.TryParse(text.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out port) || port <= 0 || port >= 65536)
            return false;
        host = text.Substring(0, colon);
        return true;
    }
}
