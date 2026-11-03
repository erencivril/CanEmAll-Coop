using System.Collections.Generic;

namespace CanEmAllCoop.Flow;

/// <summary>Host-side set of players that are currently down (waiting to respawn).</summary>
public sealed class DownTracker
{
    readonly HashSet<ulong> _down = new();

    public int Count => _down.Count;
    public bool Contains(ulong id) => _down.Contains(id);
    public IEnumerable<ulong> Ids => _down;

    /// <summary>true if the player was not down before.</summary>
    public bool MarkDown(ulong id) => _down.Add(id);
    public bool MarkUp(ulong id) => _down.Remove(id);
    public void Clear() => _down.Clear();

    /// <summary>Team wipe: at least one player takes part in the day and every one of them is down.</summary>
    public bool TeamWiped(IReadOnlyCollection<ulong> players)
    {
        if (players.Count == 0) return false;
        foreach (var id in players)
            if (!_down.Contains(id)) return false;
        return true;
    }
}
