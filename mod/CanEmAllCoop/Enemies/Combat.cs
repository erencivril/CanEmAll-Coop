using System;
using System.Collections.Generic;
using CanEmAllCoop.Core;
using UnityEngine;

namespace CanEmAllCoop.Enemies;
using CanEmAllCoop.Net;
using Net = CanEmAllCoop.Net.Net;

/// <summary>
/// Weapon hits. A client never rolls damage: it forwards the call to the host (which runs the real Enemy.Damage with
/// its own crit / instant-kill stats) and keeps only the local feedback. The host echoes the applied damage back so
/// the shooter still sees the number.
/// </summary>
internal static class Combat
{
    // ---- client -----------------------------------------------------------------------------------------------

    static readonly LogGate Gate = new();

    static void Note(string key, string msg)
    {
        if (Gate.Allow(key, Time.realtimeSinceStartup, out var n))
            Log.Info(n > 0 ? $"Enemies: {msg} (+{n} more)" : $"Enemies: {msg}");
    }

    static readonly List<HitEntry> PendingHits = new();
    static readonly Dictionary<uint, float> PendingBurn = new();
    static readonly List<BurnEntry> BurnScratch = new();
    public static long HitsSent, HitResults;
    /// <summary>Id of the enemy the last client hit was sent for (debug drivers compare it with the enemy they aimed at).</summary>
    public static uint LastClientHitId;

    /// <summary>Prefix of Enemy.Damage. Returns false when the call was turned into a network request.</summary>
    public static bool ClientDamage(Enemy e, float damage, Vector3 dir, Vector3 pos, float radiusMult,
        bool blood, bool text, bool scream, bool flash, float probMult)
    {
        if (!ClientPuppets.TryGet(e, out var puppet))
        {
            Note("hit-not-puppet", $"client hit on an enemy that is not a puppet ({e.name} at {e.transform.position}), applied locally and not sent to the host");
            return true;   // not a replicated enemy: vanilla
        }

        if (float.IsNaN(damage) || float.IsInfinity(damage))
        {
            Note("hit-bad-damage", $"client hit on #{puppet.Id} dropped, damage is {damage}");
            return false;
        }
        byte flags = (byte)((blood ? HitFlags.Blood : 0) | (text ? HitFlags.Text : 0) |
                            (scream ? HitFlags.Scream : 0) | (flash ? HitFlags.Flash : 0));
        PendingHits.Add(new HitEntry
        {
            Id = puppet.Id, Damage = damage,
            DirX = dir.x, DirY = dir.y, DirZ = dir.z,
            PosX = pos.x, PosY = pos.y, PosZ = pos.z,
            RadiusMult = radiusMult, ProbMult = probMult, Flags = flags,
            WeaponIdx = (sbyte)Math.Clamp(CurrentWeaponIndex(), -1, 127),
        });

        LastClientHitId = puppet.Id;
        LocalFeedback(e, dir, pos, radiusMult, blood, scream, flash);
        return false;
    }

    static int CurrentWeaponIndex()
    {
        try { var inv = Inventory.Instance; return inv != null ? inv.currentWeaponIndex : -1; }
        catch (Exception) { return -1; }
    }

    // What Enemy.Damage does on the client side of the hit besides the numbers: flash, rubber deform, wound decal, sound.
    static int _fxFailures;
    static void LocalFeedback(Enemy e, Vector3 dir, Vector3 pos, float radiusMult, bool blood, bool scream, bool flash)
    {
        if (_fxFailures >= 5) return;
        try
        {
            if (flash)
            {
                e.FlashHit();
                e.PlayHitRubber(dir.sqrMagnitude > 1e-6f ? dir.normalized : dir);
            }
            if (blood) e.TryAddDamageVisual(pos, radiusMult);
            e.PlayHitSound(true, scream);
        }
        catch (Exception ex)
        {
            _fxFailures++;
            Log.Error($"Enemies: local hit feedback failed ({_fxFailures}/5)", ex);
        }
    }

    /// <summary>Prefix of Enemy.AddBurn on a client: keep the local visual, tell the host.</summary>
    public static void ClientBurn(Enemy e, float delta)
    {
        if (!ClientPuppets.TryGet(e, out var p)) return;
        PendingBurn.TryGetValue(p.Id, out var sum);
        PendingBurn[p.Id] = sum + delta;
    }

    /// <summary>Called every frame on a client: one Hit message and one Burn message at most (chunked).</summary>
    public static void FlushClient()
    {
        if (PendingHits.Count > 0)
        {
            var copy = new List<HitEntry>(PendingHits);
            PendingHits.Clear();
            for (int start = 0; start < copy.Count; start += 24)
            {
                int s0 = start, n = Math.Min(24, copy.Count - start);
                Net.SendToHost(EnemyMsg.Hit, w => PoseCodec.WriteHitBatch(w, copy, s0, n));
            }
            HitsSent += copy.Count;
        }

        if (PendingBurn.Count > 0)
        {
            BurnScratch.Clear();
            foreach (var kv in PendingBurn) BurnScratch.Add(new BurnEntry { Id = kv.Key, Delta = kv.Value });
            PendingBurn.Clear();
            var copy = new List<BurnEntry>(BurnScratch);
            Net.SendToHost(EnemyMsg.Burn, w => PoseCodec.WriteBurnBatch(w, copy));
        }
    }

    public static void ClearClient()
    {
        PendingHits.Clear();
        PendingBurn.Clear();
    }

    /// <summary>Host -> shooter: the damage popup (and the free-bullet refund) for a kill / hit they caused.</summary>
    public static void HandleHitResult(Peer sender, NetReader r)
    {
        var list = new List<HitResultEntry>();
        PoseCodec.ReadHitResultBatch(r, list);
        foreach (var h in list)
        {
            HitResults++;
            try
            {
                if (h.Damage > 0f && ClientPuppets.TryGet(h.Id, out var p) && p.E != null)
                {
                    var dm = DamageTextManager.Instance;
                    if (dm != null)
                    {
                        var target = p.E.textPoint;
                        if (target == null) target = p.E.transform;
                        dm.ShowDamage(h.Damage, p.E.GetTextSpawnPoint(), target, (DamageTextManager.DamageTextType)(int)h.Tag);
                    }
                }
                if (h.FreeBullet) RefundBullet(h.WeaponIdx);
            }
            catch (Exception e) { Log.Error("Enemies: hit result handling failed", e); }
        }
    }

    static void RefundBullet(sbyte weaponIdx)
    {
        var inv = Inventory.Instance;
        if (inv == null || weaponIdx < 0) return;
        var weapons = inv.unlockedWeapons;
        if (weapons == null || weaponIdx >= weapons.Count) return;
        var w = weapons[weaponIdx];
        if (w != null) w.AddAmmo(1);
    }

    // ---- host -------------------------------------------------------------------------------------------------

    static bool _remoteActive;
    static float _capDamage;
    static byte _capTag;
    static float _capBase;

    public static long HitsApplied;

    /// <summary>Postfix of GameManager.OnHitEnemy (host): remember what the original rolled for the hit being applied.</summary>
    public static void CaptureHit(float baseDamage, float result)
    {
        if (!_remoteActive) return;
        _capDamage = result;
        _capBase = baseDamage;
        if (result >= 99999f) _capTag = 2;
        else
        {
            var gm = GameManager.Instance;
            float normal = baseDamage * (gm != null ? gm.damageMultiplier : 1f);
            _capTag = result > normal * 1.5f ? (byte)1 : (byte)0;
        }
    }

    /// <summary>Host handler of EnemyMsg.Hit.</summary>
    public static void HandleHit(Peer sender, NetReader r)
    {
        var hits = new List<HitEntry>();
        PoseCodec.ReadHitBatch(r, hits);
        var results = new List<HitResultEntry>();

        foreach (var h in hits)
        {
            try
            {
                if (!NetIds.TryGet(h.Id, out Enemy e) || e == null)
                {
                    Note("hit-unknown", $"host dropped a hit from player {sender?.Index}: no enemy with id #{h.Id}");
                    continue;
                }
                if (!(h.Damage >= 0f) || h.Damage > 1e7f)
                {
                    Note("hit-bad-damage-host", $"host dropped a hit on #{h.Id} from player {sender?.Index}: damage {h.Damage}");
                    continue;
                }

                bool wasDead = e.IsDead;
                _capDamage = 0f; _capTag = 0; _capBase = h.Damage;
                _remoteActive = true;
                try
                {
                    e.Damage(h.Damage,
                        new Vector3(h.DirX, h.DirY, h.DirZ), new Vector3(h.PosX, h.PosY, h.PosZ),
                        h.RadiusMult,
                        (h.Flags & HitFlags.Blood) != 0,
                        false,                       // the damage popup is shown by the shooter, from the HitResult
                        null,                        // weapon: only used for the free-bullet refund (rolled below)
                        (h.Flags & HitFlags.Scream) != 0,
                        (h.Flags & HitFlags.Flash) != 0,
                        h.ProbMult);
                }
                finally { _remoteActive = false; }
                HitsApplied++;

                bool died = !wasDead && e.IsDead;
                if (_capDamage <= 0f && !died && !wasDead)
                    Note("hit-no-effect", $"host: hit on #{h.Id} from player {sender?.Index} did nothing (state {e.state}, dead {e.IsDead}, damage {h.Damage}); Enemy.Damage ignores enemies that are not roaming");
                bool free = false;
                if (died && h.WeaponIdx >= 0)
                {
                    var gm = GameManager.Instance;
                    free = gm != null && UnityEngine.Random.value < gm.freeBulletProbability;
                }
                if (_capDamage > 0f || died || free)
                    results.Add(new HitResultEntry
                    {
                        Id = h.Id, Damage = _capDamage, Tag = _capTag, Died = died, FreeBullet = free, WeaponIdx = h.WeaponIdx,
                    });
            }
            catch (Exception ex) { Log.Error("Enemies: applying a client hit failed", ex); }
        }

        if (results.Count > 0 && sender != null)
            Net.SendTo(sender, EnemyMsg.HitResult, w => PoseCodec.WriteHitResultBatch(w, results));
    }

    public static void HandleBurn(Peer sender, NetReader r)
    {
        var list = new List<BurnEntry>();
        PoseCodec.ReadBurnBatch(r, list);
        foreach (var b in list)
        {
            try
            {
                if (NetIds.TryGet(b.Id, out Enemy e) && e != null && b.Delta > 0f && b.Delta < 1000f) e.AddBurn(b.Delta);
            }
            catch (Exception ex) { Log.Error("Enemies: applying client burn failed", ex); }
        }
    }

    public static string Debug() => $"hitsSent={HitsSent} hitsApplied={HitsApplied} hitResults={HitResults}";
}
