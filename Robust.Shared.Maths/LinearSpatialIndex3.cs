using System;
using System.Collections.Generic;

namespace Robust.Shared.Maths;

/// <summary>
/// Correctness-first O(n) implementation used to define 3D broadphase semantics before the optimized BVH lands.
/// </summary>
public sealed class LinearSpatialIndex3<T> : ISpatialIndex3<T> where T : notnull
{
    private readonly Dictionary<T, Box3> _entries = new();

    public int Count => _entries.Count;

    public void Add(T item, Box3 bounds)
    {
        if (!_entries.TryAdd(item, bounds))
            throw new ArgumentException("The item is already present in the spatial index.", nameof(item));
    }

    public void Update(T item, Box3 bounds)
    {
        if (!_entries.ContainsKey(item))
            throw new KeyNotFoundException("The item is not present in the spatial index.");

        _entries[item] = bounds;
    }

    public bool Remove(T item) => _entries.Remove(item);

    public bool TryGetBounds(T item, out Box3 bounds) => _entries.TryGetValue(item, out bounds);

    public void Query(Box3 bounds, ICollection<T> results)
    {
        foreach (var (item, itemBounds) in _entries)
        {
            if (itemBounds.Intersects(bounds))
                results.Add(item);
        }
    }
}
