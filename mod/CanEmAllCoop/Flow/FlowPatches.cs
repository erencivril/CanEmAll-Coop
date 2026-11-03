using System;
using System.Text;
using CanEmAllCoop.Core;
using HarmonyLib;

namespace CanEmAllCoop.Flow;

/// <summary>
/// Harmony patches of the flow module. Every patch is a pass-through while offline (FlowCore checks Net.IsOnline first)
/// and every patch body is wrapped so an exception falls back to the original game code.
/// A target that cannot be resolved/applied is logged and skipped; the rest keeps working.
/// </summary>
internal static class FlowPatches
{
    static readonly StringBuilder Applied = new();
    static int _ok, _failed;

    public static string Summary => $"{_ok}ok/{_failed}failed";

    public static void Apply(Harmony harmony)
    {
        Patch(harmony, typeof(GameManager._GameLoop_d__178), "MoveNext", typeof(LoopPatch), prefix: true, postfix: true);
        Patch(harmony, typeof(GameManager._SuicideCoroutine_d__177), "MoveNext", typeof(SuicideLoopPatch), prefix: true, postfix: true);
        Patch(harmony, typeof(GameManager), "OnTriggerOutsideElevator", typeof(OutsideElevatorPatch), prefix: true);
        Patch(harmony, typeof(GameManager), "OnTriggerInsideElevator", typeof(InsideElevatorPatch), prefix: true);
        Patch(harmony, typeof(ExitDoor), "OnTriggerInsideElevator", typeof(ExitDoorPatch), prefix: true);
        Patch(harmony, typeof(GameManager), "Suicide", typeof(SuicidePatch), prefix: true);
        Patch(harmony, typeof(GameManager), "UpdatePause", typeof(PausePatch), postfix: true);
        Patch(harmony, typeof(SaveManager), "Save", typeof(SavePatch), prefix: true);
        Log.Info($"Flow patches: {_ok} applied, {_failed} failed ({Applied})");
    }

    static void Patch(Harmony harmony, Type target, string method, Type patchClass, bool prefix = false, bool postfix = false)
    {
        string name = target.FullName + "." + method;
        try
        {
            var original = AccessTools.Method(target, method);
            if (original == null) throw new MissingMethodException(name);
            harmony.Patch(original,
                prefix: prefix ? new HarmonyMethod(patchClass.GetMethod("Prefix")) : null,
                postfix: postfix ? new HarmonyMethod(patchClass.GetMethod("Postfix")) : null);
            _ok++;
            Applied.Append(method).Append(' ');
        }
        catch (Exception e)
        {
            _failed++;
            Log.Error($"Flow: patch {name} failed - that part of the flow sync is disabled", e);
        }
    }

    // GameManager._GameLoop_d__178.MoveNext -------------------------------------------------------------------------
    static class LoopPatch
    {
        public static bool Prefix(GameManager._GameLoop_d__178 __instance, ref bool __result)
        {
            try { return FlowCore.LoopPrefix(__instance, __instance.__1__state, ref __result); }
            catch (Exception e) { FlowCore.PatchError("LoopPrefix", e); return true; }
        }

        public static void Postfix(GameManager._GameLoop_d__178 __instance, bool __result)
        {
            try { FlowCore.LoopPostfix(__result ? __instance.__1__state : -1); }
            catch (Exception e) { FlowCore.PatchError("LoopPostfix", e); }
        }
    }

    // GameManager._SuicideCoroutine_d__177.MoveNext -----------------------------------------------------------------
    static class SuicideLoopPatch
    {
        public static bool Prefix(GameManager._SuicideCoroutine_d__177 __instance, ref bool __result)
        {
            try { return FlowCore.SuicidePrefix(__instance, __instance.__1__state, ref __result); }
            catch (Exception e) { FlowCore.PatchError("SuicidePrefix", e); return true; }
        }

        public static void Postfix(GameManager._SuicideCoroutine_d__177 __instance, bool __result)
        {
            try { FlowCore.SuicidePostfix(__result ? __instance.__1__state : -1); }
            catch (Exception e) { FlowCore.PatchError("SuicidePostfix", e); }
        }
    }

    // elevator triggers -----------------------------------------------------------------------------------------------
    static class OutsideElevatorPatch
    {
        public static bool Prefix(GameManager __instance)
        {
            try { return FlowCore.ElevatorTrigger(BarrierKind.Outside, __instance); }
            catch (Exception e) { FlowCore.PatchError("OutsideElevator", e); return true; }
        }
    }

    static class InsideElevatorPatch
    {
        public static bool Prefix(GameManager __instance)
        {
            try { return FlowCore.ElevatorTrigger(BarrierKind.Inside, __instance); }
            catch (Exception e) { FlowCore.PatchError("InsideElevator", e); return true; }
        }
    }

    static class ExitDoorPatch
    {
        public static bool Prefix(ExitDoor __instance)
        {
            try { return FlowCore.ExitTrigger(__instance); }
            catch (Exception e) { FlowCore.PatchError("ExitDoor", e); return true; }
        }
    }

    // death ---------------------------------------------------------------------------------------------------------------
    static class SuicidePatch
    {
        public static bool Prefix(GameManager __instance, float delay, MailId mailId)
        {
            try { return FlowCore.SuicideRequested(__instance, delay, (int)mailId); }
            catch (Exception e) { FlowCore.PatchError("Suicide", e); return true; }
        }
    }

    // pause ---------------------------------------------------------------------------------------------------------------
    static class PausePatch
    {
        public static void Postfix()
        {
            try { FlowCore.AfterUpdatePause(); }
            catch (Exception e) { FlowCore.PatchError("UpdatePause", e); }
        }
    }

    // save ----------------------------------------------------------------------------------------------------------------
    static class SavePatch
    {
        public static bool Prefix()
        {
            try { return FlowCore.AllowSave(); }
            catch (Exception e) { FlowCore.PatchError("Save", e); return true; }
        }
    }
}
