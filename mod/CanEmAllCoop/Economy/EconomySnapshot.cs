using System;
using CanEmAllCoop.Core;
using CanEmAllCoop.Scaling;
using UnityEngine;

namespace CanEmAllCoop.Economy;

/// <summary>
/// The shared progression as the game's own save file: JsonUtility.ToJson(SaveData), identical to save.sav.
/// Applying it on a peer that already has a game scene uses the same recipe as PermanentUpgradeManager.Sell:
/// reset every ResetToDefaultValues twin and the skill tree, then replay the host's ordered unlockedSkills.
/// </summary>
public static class EconomySnapshot
{
    /// <summary>Raised on a client when the host sent a snapshot that was not applied automatically (join).</summary>
    public static event Action<string> SnapshotReceived;

    /// <summary>The newest snapshot the host pushed to this client (join push); null if none arrived.</summary>
    public static string LastReceivedJson { get; internal set; }

    /// <summary>
    /// True once a snapshot replaced this peer's SaveData (the join is done). Until then the periodic unlock-list
    /// check does not trigger a repair, so it cannot fight the join sequence. Cleared when the session ends.
    /// </summary>
    public static bool HasAppliedSnapshot { get; internal set; }

    /// <summary>Host: serialise the current SaveData (money, day, unlocks, points, mails, stats).</summary>
    public static string BuildSnapshotJson()
    {
        var save = GameAccess.Save;
        if (save == null) return "";
        try
        {
            // MoneyManager.Money is the authoritative balance during a spin / purchase; SaveData.money mirrors it.
            // Before the game starts (a friend joins while the host is still in the main menu) MoneyManager.Money is 0 and
            // writing it into CurrentSaveData would wipe the host's real money before StartGame restores it from there.
            var mm = GameAccess.Money;
            long before = save.money;
            if (mm != null) save.money = mm.Money;
            string json = JsonUtility.ToJson(save, false);
            if (!Flow.FlowCore.GameStarted) save.money = before;
            return json;
        }
        catch (Exception e)
        {
            Log.Error("economy: BuildSnapshotJson failed", e);
            return "";
        }
    }

    /// <summary>
    /// Replace CurrentSaveData with the host's and bring the live game objects to that state (reset recipe + ApplySave),
    /// then tell the pace module to re-scale. Safe to call when the game scene is not up yet: then it only swaps the save.
    /// </summary>
    public static void ApplySnapshot(string json) => Apply(json, applySave: true);

    /// <summary>
    /// Same, but stops after the reset: for a join that is followed by GameManager.StartGame, whose own restore chain
    /// (MoneyManager.StartGame, SkillTreeView.ApplySave, PermanentUpgradeManager.ApplySave) then replays the snapshot
    /// exactly once. Calling ApplySnapshot before StartGame would apply every ADD-type skill twice.
    /// </summary>
    public static void PrepareSnapshotForGameStart(string json) => Apply(json, applySave: false);

    internal static void NotifyReceived(string json)
    {
        LastReceivedJson = json;
        try { SnapshotReceived?.Invoke(json); }
        catch (Exception e) { Log.Error("economy: SnapshotReceived handler failed", e); }
    }

    static void Apply(string json, bool applySave)
    {
        if (string.IsNullOrEmpty(json)) { Log.Warn("economy: empty snapshot ignored"); return; }

        SaveData data = null;
        try { data = JsonUtility.FromJson<SaveData>(json); } // the same generic instance SaveManager.Awake uses
        catch (Exception e) { Log.Warn("economy: FromJson<SaveData> failed (" + e.Message + "), trying FromJsonOverwrite"); }
        if (data == null)
        {
            try
            {
                data = new SaveData();
                JsonUtility.FromJsonOverwrite(json, data);
            }
            catch (Exception e) { Log.Error("economy: snapshot json could not be parsed", e); return; }
        }
        if (data == null) { Log.Error("economy: snapshot parsed to null"); return; }

        var sm = SaveManager.Instance;
        if (sm == null) { Log.Warn("economy: no SaveManager; snapshot not applied"); return; }

        // Nothing below may let a client pay or roll: money writes are only legal under Applying.
        using var scope = new ApplyScope();
        sm.CurrentSaveData = data;
        HasAppliedSnapshot = true;

        var tree = GameAccess.Tree;
        var pm = PermanentUpgradeManager.Instance;
        var mm = GameAccess.Money;
        bool sceneUp = tree != null && pm != null && mm != null && GameManager.Instance != null;
        if (!sceneUp)
        {
            Log.Info("economy: snapshot stored (game scene not up, nothing to reset)");
            return;
        }

        ResetLiveState(tree, pm);

        if (applySave)
        {
            ShopSync.Guard("MoneyManager.SetMoney", () => mm.SetMoney(data.money));
            ShopSync.Guard("SkillTreeView.ApplySave", () => tree.ApplySave());
            ShopSync.Guard("PermanentUpgradeManager.ApplySave", () => pm.ApplySave());
            ShopSync.Guard("UpdateOfferTexts", () => pm.UpdateOfferTexts());
            ShopSync.Guard("Pace.OnSkillApplied", Pace.OnSkillApplied);
        }
        Log.Info($"economy: snapshot applied (applySave={applySave}, skills={data.unlockedSkills?.Count}, perms={data.unlockedPermanentUpgrades?.Count}, points={data.upgradePoints}, money={data.money})");
    }

    /// <summary>The reset half of PermanentUpgradeManager.Sell, without touching SaveData.</summary>
    static void ResetLiveState(SkillTreeView tree, PermanentUpgradeManager pm)
    {
        ShopSync.Guard("GameManager.ResetToDefaultValues", () => GameManager.Instance.ResetToDefaultValues());
        ShopSync.Guard("Inventory.ResetToDefaultValues", () => Inventory.Instance?.ResetToDefaultValues());
        ShopSync.Guard("Truck.ResetToDefaultValues", () => Truck.Instance?.ResetToDefaultValues());
        ShopSync.Guard("Vacuum.ResetToDefaultValues", () => Vacuum.Instance?.ResetToDefaultValues());
        ShopSync.Guard("RocketLauncher.ResetToDefaultValues", () => RocketLauncher.Instance?.ResetToDefaultValues());
        ShopSync.Guard("Shotgun.ResetToDefaultValues", () => Shotgun.Instance?.ResetToDefaultValues());
        ShopSync.Guard("RewardMachine.ResetToDefaultValues", () => RewardMachine.Instance?.ResetToDefaultValues());
        ShopSync.Guard("SkillTreeView.ResetSkillTree", () => tree.ResetSkillTree());
        ShopSync.Guard("SkillTreeView.CenterView", () => tree.CenterView());
        ShopSync.Guard("reset permanent flags", () =>
        {
            var nodes = pm.nodes;
            for (int i = 0; nodes != null && i < nodes.Length; i++)
                if (nodes[i]?.skill != null) nodes[i].skill.isPurchased = false;
        });
    }
}
