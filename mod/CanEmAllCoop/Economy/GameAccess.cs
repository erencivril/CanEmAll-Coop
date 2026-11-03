using System;
using System.Collections.Generic;
using CanEmAllCoop.Core;

namespace CanEmAllCoop.Economy;
using Net = CanEmAllCoop.Net.Net;

/// <summary>Sets Net.Applying for the duration of a block so the client-side money blocks let the originals run.</summary>
internal readonly struct ApplyScope : IDisposable
{
    readonly bool _prev;

    public ApplyScope()
    {
        _prev = Net.Applying;
        Net.Applying = true;
    }

    public void Dispose() => Net.Applying = _prev;
}

/// <summary>Null-safe accessors for the game singletons the economy touches.</summary>
internal static class GameAccess
{
    static SkillTreeView _tree;
    static readonly Dictionary<int, SkillNode> SkillNodes = new();
    static readonly Dictionary<int, PermanentUpgrade> PermNodes = new();

    public static SaveData Save
    {
        get
        {
            var sm = SaveManager.Instance;
            return sm != null ? sm.CurrentSaveData : null;
        }
    }

    public static MoneyManager Money
    {
        get
        {
            var mm = MoneyManager.Instance;
            return mm != null ? mm : null;
        }
    }

    public static bool TryReadWallet(out long money, out long totalMoney)
    {
        money = totalMoney = 0;
        var mm = Money;
        var save = Save;
        if (mm == null || save == null) return false;
        money = mm.Money;
        totalMoney = save.totalMoney;
        return true;
    }

    public static SkillTreeView Tree
    {
        get
        {
            if (_tree != null) return _tree;
            var pm = PermanentUpgradeManager.Instance;
            if (pm != null && pm.skillTree != null) _tree = pm.skillTree;
            else _tree = UnityEngine.Object.FindObjectOfType<SkillTreeView>();
            return _tree;
        }
    }

    public static SkillNode FindSkillNode(int skillId)
    {
        if (SkillNodes.TryGetValue(skillId, out var cached) && cached != null) return cached;
        SkillNodes.Clear();
        var tree = Tree;
        var nodes = tree?.nodes;
        if (nodes == null) return null;
        for (int i = 0; i < nodes.Length; i++)
        {
            var n = nodes[i];
            if (n == null || n.skill == null) continue;
            SkillNodes[(int)n.skill.skillId] = n;
        }
        return SkillNodes.TryGetValue(skillId, out var found) ? found : null;
    }

    public static PermanentUpgrade FindPermanent(int skillId)
    {
        if (PermNodes.TryGetValue(skillId, out var cached) && cached != null) return cached;
        PermNodes.Clear();
        var nodes = PermanentUpgradeManager.Instance?.nodes;
        if (nodes == null) return null;
        for (int i = 0; i < nodes.Length; i++)
        {
            var n = nodes[i];
            if (n == null || n.skill == null) continue;
            PermNodes[(int)n.skill.skillId] = n;
        }
        return PermNodes.TryGetValue(skillId, out var found) ? found : null;
    }

    /// <summary>Scene reloads destroy the nodes; drop every cached reference.</summary>
    public static void ForgetScene()
    {
        _tree = null;
        SkillNodes.Clear();
        PermNodes.Clear();
    }

    public static int[] ToArray(Il2CppSystem.Collections.Generic.List<SkillId> list)
    {
        if (list == null) return Array.Empty<int>();
        var a = new int[list.Count];
        for (int i = 0; i < a.Length; i++) a[i] = (int)list[i];
        return a;
    }

    public static bool Contains(Il2CppSystem.Collections.Generic.List<SkillId> list, int id)
    {
        if (list == null) return false;
        for (int i = 0; i < list.Count; i++)
            if ((int)list[i] == id) return true;
        return false;
    }

    public static long NowMs => Environment.TickCount64;
}
