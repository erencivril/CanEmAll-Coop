using System;
using CanEmAllCoop.Core;
using UnityEngine;

namespace CanEmAllCoop.Avatar;

/// <summary>Maps the game's Weapon objects to wire codes and finds the local copy of a weapon for cosmetic sounds.</summary>
internal static class AvatarWeapons
{
    /// <summary>Inventory.unlockedWeapons[currentWeaponIndex - 1] is the active weapon; index 0 = fists (UpdateSelection).</summary>
    public static byte LocalCode(Inventory inv)
    {
        if (inv == null) return WeaponCodes.Unknown;
        int idx = inv.currentWeaponIndex;
        if (idx <= 0) return WeaponCodes.Fists;
        var list = inv.unlockedWeapons;
        if (list == null || idx - 1 >= list.Count) return WeaponCodes.Unknown;
        return Classify(list[idx - 1]);
    }

    public static byte Classify(Weapon w)
    {
        if (w == null) return WeaponCodes.Unknown;
        if (w.TryCast<Pistol>() != null) return WeaponCodes.Pistol;
        if (w.TryCast<SMG>() != null) return WeaponCodes.Smg;
        if (w.TryCast<AK47>() != null) return WeaponCodes.Ak47;
        if (w.TryCast<Shotgun>() != null) return WeaponCodes.Shotgun;
        if (w.TryCast<RocketLauncher>() != null) return WeaponCodes.Rocket;
        if (w.TryCast<FlameThrower>() != null) return WeaponCodes.Flame;
        if (w.TryCast<Chainsaw>() != null) return WeaponCodes.Chainsaw;
        if (w.TryCast<BaseballBat>() != null) return WeaponCodes.Bat;
        if (w.TryCast<Knife>() != null) return WeaponCodes.Knife;
        if (w.TryCast<Vacuum>() != null) return WeaponCodes.Vacuum;
        return WeaponCodes.Unknown;
    }

    /// <summary>Plays the local copy of the weapon's shot sound (or a punch sound) at a world position. Best effort.</summary>
    public static void PlayShot(byte code, Vector3 position)
    {
        try
        {
            var inv = Inventory.Instance;
            if (inv == null) return;
            AudioClip clip = null;
            if (code == WeaponCodes.Fists)
                clip = Pick(inv.punchClips);
            else
            {
                var w = FindWeapon(inv, code);
                if (w != null) clip = ShootClip(w, code);
            }
            if (clip != null) AudioSource.PlayClipAtPoint(clip, position, 0.8f);
        }
        catch (Exception e) { Log.Error("Avatar: PlayShot failed", e); }
    }

    public static void PlayFootstep(Player lp, Vector3 position)
    {
        try
        {
            var clip = Pick(lp.concreteFootSteps);
            if (clip != null) AudioSource.PlayClipAtPoint(clip, position, 0.5f);
        }
        catch (Exception) { /* cosmetic */ }
    }

    static Weapon FindWeapon(Inventory inv, byte code)
    {
        var all = inv.allWeapons;
        if (all == null) return null;
        for (int i = 0; i < all.Count; i++)
        {
            var w = all[i];
            if (w != null && Classify(w) == code) return w;
        }
        return null;
    }

    static AudioClip ShootClip(Weapon w, byte code)
    {
        switch (code)
        {
            case WeaponCodes.Pistol: return w.TryCast<Pistol>()?.shootClip;
            case WeaponCodes.Smg: return Pick(w.TryCast<SMG>()?.shootClip);
            case WeaponCodes.Ak47: return Pick(w.TryCast<AK47>()?.shootClip);
            case WeaponCodes.Shotgun: return Pick(w.TryCast<Shotgun>()?.shootClip);
            default: return null; // rocket launcher, melee, flamethrower: no plain shoot clip field
        }
    }

    static AudioClip Pick(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<AudioClip> clips)
    {
        if (clips == null || clips.Length == 0) return null;
        return clips[UnityEngine.Random.Range(0, clips.Length)];
    }
}
