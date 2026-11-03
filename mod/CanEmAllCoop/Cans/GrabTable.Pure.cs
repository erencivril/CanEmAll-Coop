using System.Collections.Generic;

namespace CanEmAllCoop.Cans;

public enum GrabKind : byte { Hand = 0, Vacuum = 1 }

/// <summary>
/// Who currently holds which networked object. The host is the authority (TryGrab decides); clients keep a
/// mirror of it updated from OwnerSet messages (Force). Holder = Peer.Index (host 0).
/// </summary>
public sealed class GrabTable
{
    public const int None = -1;

    public readonly struct Hold
    {
        public readonly int Holder;
        public readonly GrabKind Kind;
        public Hold(int holder, GrabKind kind) { Holder = holder; Kind = kind; }
    }

    readonly Dictionary<uint, Hold> _holds = new();

    public int Count => _holds.Count;

    public IEnumerable<KeyValuePair<uint, Hold>> All => _holds;

    public int HolderOf(uint id) => _holds.TryGetValue(id, out var h) ? h.Holder : None;

    public bool TryGetHold(uint id, out Hold hold) => _holds.TryGetValue(id, out hold);

    public bool IsHeld(uint id) => _holds.ContainsKey(id);

    /// <summary>Grants the object to peer unless somebody else holds it. Re-grabbing by the same peer succeeds.</summary>
    public bool TryGrab(uint id, int peer, GrabKind kind)
    {
        if (id == 0 || peer < 0) return false;
        if (_holds.TryGetValue(id, out var h) && h.Holder != peer) return false;
        _holds[id] = new Hold(peer, kind);
        return true;
    }

    /// <summary>Only the current holder can release. Returns the kind it was held with.</summary>
    public bool TryRelease(uint id, int peer, out GrabKind kind)
    {
        kind = GrabKind.Hand;
        if (!_holds.TryGetValue(id, out var h) || h.Holder != peer) return false;
        kind = h.Kind;
        _holds.Remove(id);
        return true;
    }

    /// <summary>Mirror update on a client (no validation). A negative peer clears the entry.</summary>
    public void Force(uint id, int peer, GrabKind kind)
    {
        if (id == 0) return;
        if (peer < 0) _holds.Remove(id);
        else _holds[id] = new Hold(peer, kind);
    }

    public bool Remove(uint id) => _holds.Remove(id);

    /// <summary>Drops every hold of a peer (disconnect). The released ids are appended to result.</summary>
    public void ReleaseAll(int peer, List<KeyValuePair<uint, GrabKind>> result)
    {
        var ids = new List<uint>();
        foreach (var kv in _holds)
            if (kv.Value.Holder == peer) ids.Add(kv.Key);
        foreach (var id in ids)
        {
            result?.Add(new KeyValuePair<uint, GrabKind>(id, _holds[id].Kind));
            _holds.Remove(id);
        }
    }

    public void Clear() => _holds.Clear();
}
