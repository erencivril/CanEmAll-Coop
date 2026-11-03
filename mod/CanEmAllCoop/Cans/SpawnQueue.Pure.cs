using System.Collections.Generic;

namespace CanEmAllCoop.Cans;

public struct SpawnInfo
{
    public uint Id;
    public int Processor;
    public int Price;
    public bool Shiny;
    public bool Smoked;
    public int TextureIndex;
    public int Quantity;
    public float Px, Py, Pz;
    public uint Rot; // QuatPack
}

/// <summary>
/// Client-side queue of cans announced by the host but not created yet (the scene may not be loaded, or a late
/// join snapshot carries a thousand of them and creating them in one frame would hitch). Later messages
/// (merge / paid / gone / transform) patch or cancel entries that are still waiting.
/// </summary>
public sealed class SpawnQueue
{
    readonly Dictionary<uint, SpawnInfo> _items = new();
    readonly Queue<uint> _order = new();

    public int Count => _items.Count;

    public void Add(SpawnInfo info)
    {
        if (!_items.ContainsKey(info.Id)) _order.Enqueue(info.Id);
        _items[info.Id] = info;
    }

    public bool Contains(uint id) => _items.ContainsKey(id);

    public bool SetQuantity(uint id, int quantity)
    {
        if (!_items.TryGetValue(id, out var info)) return false;
        info.Quantity = quantity;
        _items[id] = info;
        return true;
    }

    public bool SetPose(uint id, float x, float y, float z, uint rot)
    {
        if (!_items.TryGetValue(id, out var info)) return false;
        info.Px = x; info.Py = y; info.Pz = z; info.Rot = rot;
        _items[id] = info;
        return true;
    }

    public bool Remove(uint id) => _items.Remove(id);

    /// <summary>Oldest still-valid entry (removed ones are skipped lazily).</summary>
    public bool TryDequeue(out SpawnInfo info)
    {
        while (_order.Count > 0)
        {
            uint id = _order.Dequeue();
            if (_items.TryGetValue(id, out info))
            {
                _items.Remove(id);
                return true;
            }
        }
        info = default;
        return false;
    }

    public void Clear()
    {
        _items.Clear();
        _order.Clear();
    }
}
