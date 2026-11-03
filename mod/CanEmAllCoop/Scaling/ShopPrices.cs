using System;
using System.Collections.Generic;
using CanEmAllCoop.Core;
using UnityEngine;

namespace CanEmAllCoop.Scaling;

/// <summary>
/// Co-op shop prices (Neutral mode): every skill tree node costs its solo price times k, on the host and on every client, so
/// each can pays its full value while the team still progresses at the solo pace. Permanent upgrades (paid in rebirth points)
/// are left alone. The solo price is remembered per Skill object and restored when scaling stops or the session ends.
/// </summary>
internal static class ShopPrices
{
    sealed class Entry { public Skill Skill; public int Solo; public int Written; }

    static readonly Dictionary<IntPtr, Entry> BySkill = new();
    static float _nextCheck;

    /// <summary>Nodes currently priced above their solo price.</summary>
    public static int Scaled { get; private set; }

    /// <summary>Called every frame; re-applies once a second (new tree after a scene reload, k changed, mode toggled).</summary>
    public static void Update()
    {
        float now = Time.unscaledTime;
        if (now < _nextCheck) return;
        _nextCheck = now + 1f;
        Apply();
    }

    public static void Apply()
    {
        try
        {
            bool scale = Pace.PricesScaled;
            if (!scale && BySkill.Count == 0) return;
            long num = 1, den = 1;
            if (scale) Pace.IncomeDivisor(out num, out den);

            var nodes = CanEmAllCoop.Economy.GameAccess.Tree?.nodes;
            if (nodes == null) return;
            int scaled = 0;
            for (int i = 0; i < nodes.Length; i++)
            {
                var skill = nodes[i]?.skill;
                if (skill == null) continue;
                var ptr = skill.Pointer;
                if (!BySkill.TryGetValue(ptr, out var e) || e.Skill == null)
                {
                    e = new Entry { Skill = skill, Solo = skill.price, Written = skill.price };
                    BySkill[ptr] = e;
                }
                else if (skill.price != e.Written)
                {
                    e.Solo = skill.price;   // the game wrote a new price (or this is a new object at a reused address)
                }

                int want = PaceMath.ScalePrice(e.Solo, num, den);
                if (skill.price != want) skill.price = want;
                e.Written = want;
                if (want != e.Solo) scaled++;
            }
            if (scaled != Scaled) Log.Info($"[Pace] shop prices: {scaled} skill tree nodes at x{(double)num / den:0.###}");
            Scaled = scaled;
            if (!scale) BySkill.Clear();
        }
        catch (Exception e)
        {
            Log.Error("[Pace] applying shop prices failed", e);
            _nextCheck = Time.unscaledTime + 10f;
        }
    }

    /// <summary>Session end: every remembered skill gets its solo price back.</summary>
    public static void Restore()
    {
        foreach (var e in BySkill.Values)
        {
            try { if (e.Skill != null && e.Skill.price == e.Written) e.Skill.price = e.Solo; }
            catch (Exception) { /* destroyed with its scene */ }
        }
        BySkill.Clear();
        if (Scaled != 0) Log.Info("[Pace] shop prices restored");
        Scaled = 0;
    }
}
