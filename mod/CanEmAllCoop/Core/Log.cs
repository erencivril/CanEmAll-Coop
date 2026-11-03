using System;
using BepInEx.Logging;

namespace CanEmAllCoop.Core;

/// <summary>Thin wrapper over the plugin's ManualLogSource. Safe to call before Plugin.Load (no-op).</summary>
public static class Log
{
    static ManualLogSource _src;

    internal static void Init(ManualLogSource source) => _src = source;

    public static void Info(string msg) => _src?.LogInfo(msg);
    public static void Warn(string msg) => _src?.LogWarning(msg);
    public static void Error(string msg) => _src?.LogError(msg);
    public static void Error(string msg, Exception e) => _src?.LogError($"{msg}: {e}");
    public static void Debug(string msg) => _src?.LogDebug(msg);
}
