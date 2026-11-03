using System;
using CanEmAllCoop.Core;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace CanEmAllCoop.Economy;
using Net = CanEmAllCoop.Net.Net;

// Every prefix starts with `if (!Net.IsOnline) return true;` so an offline player runs vanilla code.
// Each class is applied on its own (EconomyModule.Init) so one missing game method disables only that part.

/// <summary>
/// An exception that leaves a patch body is thrown into the game's own caller (prefix: the original is skipped, postfix:
/// the caller aborts after the original ran). Every body below that can touch the network or game objects goes through
/// here: the failure is logged (first few, then rarely) and the caller gets a safe answer.
/// </summary>
static class PatchGuard
{
    static int _count;

    public static void Failed(string where, Exception e)
    {
        int n = ++_count;
        if (n <= 5 || n % 500 == 0) Log.Error($"Economy: patch {where} threw (x{n})", e);
    }

    /// <summary>Prefix result when the body threw: a client must never fall through to the vanilla payment, the host/offline may.</summary>
    public static bool Fallback() => !Net.IsClient;
}

/// <summary>
/// Clients never earn money. Priority.Last: scaling's host-only AddMoney prefix (pace division) and any other
/// prefix run first; this block only returns false on a client, where scaling does nothing.
/// </summary>
[HarmonyPatch(typeof(MoneyManager), nameof(MoneyManager.AddMoney))]
static class AddMoneyPatch
{
    [HarmonyPriority(Priority.Last)]
    static bool Prefix() => !Net.IsClient;
}

[HarmonyPatch(typeof(MoneyManager), nameof(MoneyManager.ChangeMoney))]
static class ChangeMoneyPatch
{
    // ChangeMoney is the slot machine's balance writer: a replay on a client is allowed to run it.
    [HarmonyPriority(Priority.Last)]
    static bool Prefix() => !Net.IsClient || Net.Applying || SlotSync.ReplayActive;

    static void Postfix()
    {
        // During a host-run spin the balance is announced once, when the spin ends (SlotSync.Update).
        try { if (Net.IsHost && !SlotSync.HostBusy) WalletSync.HostFlushNow(); }
        catch (Exception e) { PatchGuard.Failed("ChangeMoney.Postfix", e); }
    }
}

[HarmonyPatch(typeof(MoneyManager), nameof(MoneyManager.SetMoney))]
static class SetMoneyPatch
{
    [HarmonyPriority(Priority.Last)]
    static bool Prefix() => !Net.IsClient || Net.Applying;
}

[HarmonyPatch(typeof(SkillNode), nameof(SkillNode.OnClick))]
static class SkillNodeClickPatch
{
    static bool Prefix(SkillNode __instance, ref bool __state)
    {
        __state = false;
        try
        {
            if (!Net.IsOnline) return true;
            var skill = __instance.skill;
            if (skill == null) return true;

            if (Net.IsHost)
            {
                __state = !skill.isPurchased;
                return true;
            }

            // Client: anything the vanilla code rejects without paying (owned, locked, too expensive) keeps its own
            // feedback; only a click that would pay becomes a request.
            var mm = GameAccess.Money;
            if (skill.isPurchased || __instance.IsLocked || mm == null || mm.Money < skill.price) return true;
            ShopSync.RequestBuy((int)skill.skillId);
            return false;
        }
        catch (Exception e) { PatchGuard.Failed("SkillNode.OnClick.Prefix", e); return PatchGuard.Fallback(); }
    }

    static void Postfix(SkillNode __instance, bool __state)
    {
        try
        {
            if (__state && Net.IsHost && __instance.skill != null && __instance.skill.isPurchased)
                ShopSync.HostSkillPurchased(__instance);
        }
        catch (Exception e) { PatchGuard.Failed("SkillNode.OnClick.Postfix", e); }
    }
}

[HarmonyPatch(typeof(PermanentUpgrade), nameof(PermanentUpgrade.OnClick))]
static class PermanentUpgradeClickPatch
{
    static bool Prefix(PermanentUpgrade __instance, ref bool __state)
    {
        __state = false;
        try
        {
            if (!Net.IsOnline) return true;
            var skill = __instance.skill;
            if (skill == null) return true;

            if (Net.IsHost)
            {
                __state = !skill.isPurchased;
                return true;
            }

            var save = GameAccess.Save;
            if (skill.isPurchased || save == null || save.upgradePoints < skill.price) return true;
            ShopSync.RequestPermanent((int)skill.skillId);
            return false;
        }
        catch (Exception e) { PatchGuard.Failed("PermanentUpgrade.OnClick.Prefix", e); return PatchGuard.Fallback(); }
    }

    static void Postfix(PermanentUpgrade __instance, bool __state)
    {
        try
        {
            if (__state && Net.IsHost && __instance.skill != null && __instance.skill.isPurchased)
                ShopSync.HostPermanentPurchased(__instance);
        }
        catch (Exception e) { PatchGuard.Failed("PermanentUpgrade.OnClick.Postfix", e); }
    }
}

[HarmonyPatch(typeof(PermanentUpgradeManager), nameof(PermanentUpgradeManager.Sell))]
static class SellPatch
{
    static bool Prefix(ref bool __state)
    {
        __state = false;
        try
        {
            if (!Net.IsOnline) return true;
            if (Net.IsHost) { __state = true; return true; }
            ShopSync.RequestSell(); // a client's sale is the host's sale; the result comes back as Sold
            return false;
        }
        catch (Exception e) { PatchGuard.Failed("Sell.Prefix", e); return PatchGuard.Fallback(); }
    }

    static void Postfix(bool __state)
    {
        try { if (__state && Net.IsHost) ShopSync.HostSold(); }
        catch (Exception e) { PatchGuard.Failed("Sell.Postfix", e); }
    }
}

/// <summary>The popup that opens after a purchase would pop up on the host's screen for every friend's purchase.</summary>
[HarmonyPatch(typeof(SkillPopUp), nameof(SkillPopUp.Show), new[] { typeof(SkillNode) })]
static class SkillPopUpNodePatch
{
    static bool Prefix() => !ShopSync.SuppressPopup;
}

[HarmonyPatch(typeof(SkillPopUp), nameof(SkillPopUp.Show), new[] { typeof(PermanentUpgrade) })]
static class SkillPopUpPermanentPatch
{
    static bool Prefix() => !ShopSync.SuppressPopup;
}

[HarmonyPatch(typeof(SlotMachine), nameof(SlotMachine.Spin))]
static class SlotSpinPatch
{
    static bool Prefix(SlotMachine __instance)
    {
        try
        {
            if (!Net.IsOnline) return true;
            if (Net.IsClient) return SlotSync.ClientSpinClicked(__instance);
            SlotSync.HostBeforeSpin(__instance);
            return true;
        }
        catch (Exception e) { PatchGuard.Failed("SlotMachine.Spin.Prefix", e); return PatchGuard.Fallback(); }
    }

    static void Postfix(SlotMachine __instance)
    {
        try { if (Net.IsHost) SlotSync.HostAfterSpin(__instance); }
        catch (Exception e) { PatchGuard.Failed("SlotMachine.Spin.Postfix", e); }
    }
}

[HarmonyPatch(typeof(SlotMachine), nameof(SlotMachine.GenerateResult))]
static class SlotGenerateResultPatch
{
    static bool Prefix(ref Il2CppStructArray<int> __result)
    {
        try
        {
            if (!Net.IsClient) return true;
            return !SlotSync.TryForcedResult(out __result);
        }
        catch (Exception e) { PatchGuard.Failed("SlotMachine.GenerateResult.Prefix", e); return true; }   // a client roll is only a visual: the wallet comes from the host
    }

    static void Postfix(Il2CppStructArray<int> __result)
    {
        try { if (Net.IsHost) SlotSync.HostGenerated(__result); }
        catch (Exception e) { PatchGuard.Failed("SlotMachine.GenerateResult.Postfix", e); }
    }
}
