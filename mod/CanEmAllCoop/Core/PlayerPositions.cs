using System;
using System.Collections.Generic;
using UnityEngine;

namespace CanEmAllCoop.Core;

/// <summary>
/// Where the players are. The avatar module registers a provider returning the remote avatars' positions so that
/// systems asking "where is the nearest player" (enemy flee paths) see everyone.
/// </summary>
public static class PlayerPositions
{
    static readonly List<Func<IEnumerable<Vector3>>> Providers = new();

    public static void Register(Func<IEnumerable<Vector3>> provider)
    {
        if (provider != null && !Providers.Contains(provider)) Providers.Add(provider);
    }

    /// <summary>Local player (when it exists) followed by every provider's positions. A fresh list on every call.</summary>
    public static List<Vector3> All()
    {
        var list = new List<Vector3>(4);
        try
        {
            var p = Player.Instance;
            if (p != null) list.Add(p.transform.position);
        }
        catch (Exception e) { Log.Error("PlayerPositions: local player lookup failed", e); }

        foreach (var provider in Providers)
        {
            try
            {
                var seq = provider();
                if (seq != null) list.AddRange(seq);
            }
            catch (Exception e) { Log.Error("PlayerPositions: provider failed", e); }
        }
        return list;
    }
}
