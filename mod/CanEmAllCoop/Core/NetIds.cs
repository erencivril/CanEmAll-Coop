using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;

namespace CanEmAllCoop.Core;

/// <summary>
/// Session-wide ids for game objects (enemies, cans, crates). The host assigns, clients bind the host's ids.
/// Keyed by the native object pointer. Entries hold the wrapper (which keeps the IL2CPP object alive), so call
/// Release(id) when the object despawns; destroyed Unity objects are also dropped lazily on lookup.
/// </summary>
public static class NetIds
{
    static readonly Dictionary<IntPtr, uint> ByPtr = new();
    static readonly Dictionary<uint, Il2CppObjectBase> ById = new();
    static uint _next = 1;

    public static int Count => ById.Count;

    /// <summary>Host: id for obj, creating one if needed. Returns 0 for null / collected objects.</summary>
    public static uint Assign(Il2CppObjectBase obj)
    {
        if (obj == null || obj.WasCollected) return 0;
        var ptr = obj.Pointer;
        if (ByPtr.TryGetValue(ptr, out var existing)) return existing;
        uint id = _next++;
        if (_next == 0) _next = 1;
        ByPtr[ptr] = id;
        ById[id] = obj;
        return id;
    }

    /// <summary>Client: bind the host's id to a local object (replaces any previous binding of either side).</summary>
    public static void Bind(uint id, Il2CppObjectBase obj)
    {
        if (id == 0 || obj == null || obj.WasCollected) return;
        Release(id);
        var ptr = obj.Pointer;
        if (ByPtr.TryGetValue(ptr, out var oldId)) ById.Remove(oldId);
        ByPtr[ptr] = id;
        ById[id] = obj;
    }

    public static bool TryGet<T>(uint id, out T result) where T : Il2CppObjectBase
    {
        result = null;
        if (!ById.TryGetValue(id, out var obj)) return false;
        if (obj.WasCollected || IsDestroyedUnityObject(obj))
        {
            Release(id);
            return false;
        }
        if (obj is T typed) { result = typed; return true; }
        try { result = obj.TryCast<T>(); }
        catch (Exception) { result = null; }
        return result != null;
    }

    /// <summary>0 when the object has no id.</summary>
    public static uint IdOf(Il2CppObjectBase obj)
    {
        if (obj == null || obj.WasCollected) return 0;
        return ByPtr.TryGetValue(obj.Pointer, out var id) ? id : 0;
    }

    public static void Release(uint id)
    {
        if (!ById.TryGetValue(id, out var obj)) return;
        ById.Remove(id);
        try { if (!obj.WasCollected) ByPtr.Remove(obj.Pointer); }
        catch (Exception) { }
        if (ByPtr.Count > ById.Count) PruneByPtr();
    }

    /// <summary>Session end / scene reset. Ids restart from 1.</summary>
    public static void Clear()
    {
        ByPtr.Clear();
        ById.Clear();
        _next = 1;
    }

    // UnityEngine.Object overloads == so a destroyed (but still wrapped) object compares equal to null.
    static bool IsDestroyedUnityObject(Il2CppObjectBase obj) => obj is UnityEngine.Object uo && uo == null;

    // WasCollected objects can no longer be looked up by pointer; drop their stale reverse entries.
    static void PruneByPtr()
    {
        var dead = new List<IntPtr>();
        foreach (var kv in ByPtr)
            if (!ById.ContainsKey(kv.Value)) dead.Add(kv.Key);
        foreach (var p in dead) ByPtr.Remove(p);
    }
}
