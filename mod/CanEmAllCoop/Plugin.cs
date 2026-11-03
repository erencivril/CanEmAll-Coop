using System;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using CanEmAllCoop.Avatar;
using CanEmAllCoop.Cans;
using CanEmAllCoop.Core;
using CanEmAllCoop.Economy;
using CanEmAllCoop.Enemies;
using CanEmAllCoop.Flow;
using CanEmAllCoop.Scaling;
using CanEmAllCoop.UI;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;

namespace CanEmAllCoop;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "com.eren.canemallcoop";
    public const string Name = "Can 'Em All Co-op";
    public const string Version = "0.1.0";

    public static Harmony Harmony { get; private set; }

    public override void Load()
    {
        Core.Log.Init(base.Log);
        Core.Log.Info($"{Name} {Version} loading");

        CoopConfig.Init(Config);
        LaunchArgs.Parse(Environment.GetCommandLineArgs());
        Harmony = new Harmony(Guid);

        // Modules patch the game and must never take the game down: ModuleHost isolates every call.
        ModuleHost.Init(new Func<ICoopModule>[]
        {
            () => new ScalingModule(),
            () => new EconomyModule(),
            () => new FlowModule(),
            () => new EnemyModule(),
            () => new CanModule(),
            () => new AvatarModule(),
            () => new Debug.DebugModule(),
        }, Harmony);

        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<CoopBehaviour>();
            AddComponent<CoopBehaviour>();
        }
        catch (Exception e)
        {
            Core.Log.Error("Could not create the co-op behaviour (no menu, no networking pump)", e);
        }

        Core.Log.Info($"{Name} {Version} loaded");
    }
}
