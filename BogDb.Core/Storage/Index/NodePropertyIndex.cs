using System;
using System.Collections.Generic;
using System.Linq;
using BogDb.Core.Common;
using BogDb.Core.Main;

namespace BogDb.Core.Storage.Index;

/// <summary>
/// In-memory INodeIndex backed by a plain dictionary.
/// Used as the primary index implementation while the disk-backed
/// HashIndex DiskArray path is being completed.
/// C++ parity: in-memory variant of src/storage/index/in_mem_hash_index.h
/// </summary>
internal sealed class InMemoryNodeIndex : INodeIndex
{
    private readonly Dictionary<object, List<long>> _map = new(StructuralValueComparer.Instance);

    public long Count => _map.Count;

    public void Put(object key, long nodeOffset)
    {
        if (key is null) return;

        if (!_map.TryGetValue(key, out var offsets))
        {
            offsets = new List<long>();
            _map[key] = offsets;
        }

        if (offsets.Count == 0 || offsets[offsets.Count - 1] != nodeOffset)
            offsets.Add(nodeOffset);
    }

    public bool TryLookup(object key, out long nodeOffset)
    {
        nodeOffset = -1;
        if (key is null) return false;
        if (!_map.TryGetValue(key, out var offsets) || offsets.Count == 0)
            return false;
        nodeOffset = offsets[offsets.Count - 1];
        return true;
    }

    public bool TryLookupAll(object key, out IReadOnlyList<long> nodeOffsets)
    {
        nodeOffsets = Array.Empty<long>();
        if (key is null) return false;
        if (!_map.TryGetValue(key, out var offsets) || offsets.Count == 0)
            return false;
        nodeOffsets = offsets;
        return true;
    }

    public bool Remove(object key, long nodeOffset)
    {
        if (key is null) return false;
        if (!_map.TryGetValue(key, out var offsets))
            return false;
        var removed = offsets.Remove(nodeOffset);
        if (offsets.Count == 0)
            _map.Remove(key);
        return removed;
    }

    public IEnumerable<KeyValuePair<object, IReadOnlyList<long>>> EnumerateEntries()
    {
        foreach (var (key, offsets) in _map)
            yield return new KeyValuePair<object, IReadOnlyList<long>>(key, offsets);
    }

    public void Clear() => _map.Clear();
}

/// <summary>
/// Read-mostly index hydrated from persisted snapshot data with a mutable overlay
/// for post-reopen writes. This keeps reopen-time ownership generic instead of
/// eagerly converting everything back into the mutable in-memory implementation.
/// </summary>
internal sealed class SnapshotBackedNodeIndex : INodeIndex
{
    private readonly Dictionary<object, IReadOnlyList<long>> _baseEntries;
    private readonly Dictionary<object, List<long>> _overlayEntries;

    public SnapshotBackedNodeIndex(IEnumerable<KeyValuePair<object, IReadOnlyList<long>>> entries)
    {
        _baseEntries = new Dictionary<object, IReadOnlyList<long>>(StructuralValueComparer.Instance);
        foreach (var (key, nodeOffsets) in entries)
        {
            if (key is null)
                continue;

            var materialized = nodeOffsets as long[] ?? nodeOffsets.ToArray();
            if (materialized.Length == 0)
                continue;

            _baseEntries[key] = materialized;
        }

        _overlayEntries = new Dictionary<object, List<long>>(StructuralValueComparer.Instance);
    }

    public long Count => _baseEntries.Count + _overlayEntries.Count;

    public void Put(object key, long nodeOffset)
    {
        if (key is null)
            return;

        if (!_overlayEntries.TryGetValue(key, out var offsets))
        {
            offsets = new List<long>();
            _overlayEntries[key] = offsets;
        }

        if (offsets.Count == 0 || offsets[offsets.Count - 1] != nodeOffset)
            offsets.Add(nodeOffset);
    }

    public bool TryLookup(object key, out long nodeOffset)
    {
        nodeOffset = -1;
        if (!TryLookupAll(key, out var nodeOffsets) || nodeOffsets.Count == 0)
            return false;

        nodeOffset = nodeOffsets[nodeOffsets.Count - 1];
        return true;
    }

    public bool TryLookupAll(object key, out IReadOnlyList<long> nodeOffsets)
    {
        nodeOffsets = Array.Empty<long>();
        if (key is null)
            return false;

        var hasBase = _baseEntries.TryGetValue(key, out var baseOffsets) && baseOffsets.Count > 0;
        var hasOverlay = _overlayEntries.TryGetValue(key, out var overlayOffsets) && overlayOffsets.Count > 0;
        if (!hasBase && !hasOverlay)
            return false;

        if (hasBase && !hasOverlay)
        {
            nodeOffsets = baseOffsets!;
            return true;
        }

        if (!hasBase)
        {
            nodeOffsets = overlayOffsets!;
            return true;
        }

        var merged = new long[baseOffsets!.Count + overlayOffsets!.Count];
        for (var i = 0; i < baseOffsets.Count; i++)
            merged[i] = baseOffsets[i];
        for (var i = 0; i < overlayOffsets.Count; i++)
            merged[baseOffsets.Count + i] = overlayOffsets[i];
        nodeOffsets = merged;
        return true;
    }

    public bool Remove(object key, long nodeOffset)
    {
        if (key is null) return false;
        var removed = false;

        // Check overlay first
        if (_overlayEntries.TryGetValue(key, out var overlayOffsets))
        {
            removed = overlayOffsets.Remove(nodeOffset);
            if (overlayOffsets.Count == 0)
                _overlayEntries.Remove(key);
        }

        // Also check base entries
        if (_baseEntries.TryGetValue(key, out var baseOffsets))
        {
            // Base entries are IReadOnlyList, so we need to replace with a filtered copy
            var filtered = baseOffsets.Where(o => o != nodeOffset).ToArray();
            if (filtered.Length < baseOffsets.Count)
            {
                removed = true;
                if (filtered.Length == 0)
                    _baseEntries.Remove(key);
                else
                    _baseEntries[key] = filtered;
            }
        }

        return removed;
    }

    public IEnumerable<KeyValuePair<object, IReadOnlyList<long>>> EnumerateEntries()
    {
        var seen = new HashSet<object>(StructuralValueComparer.Instance);

        foreach (var (key, offsets) in _baseEntries)
        {
            seen.Add(key);
            if (_overlayEntries.TryGetValue(key, out var overlayOffsets) && overlayOffsets.Count > 0)
            {
                var merged = new long[offsets.Count + overlayOffsets.Count];
                for (var i = 0; i < offsets.Count; i++)
                    merged[i] = offsets[i];
                for (var i = 0; i < overlayOffsets.Count; i++)
                    merged[offsets.Count + i] = overlayOffsets[i];
                yield return new KeyValuePair<object, IReadOnlyList<long>>(key, merged);
                continue;
            }

            yield return new KeyValuePair<object, IReadOnlyList<long>>(key, offsets);
        }

        foreach (var (key, offsets) in _overlayEntries)
        {
            if (seen.Contains(key))
                continue;

            yield return new KeyValuePair<object, IReadOnlyList<long>>(key, offsets);
        }
    }

    public void Clear()
    {
        _baseEntries.Clear();
        _overlayEntries.Clear();
    }
}

/// <summary>
/// Per-table collection of per-property indexes.
/// Maintains one INodeIndex per indexed property name.
/// Created on demand by BogDatabase.GetNodePropertyIndex(tableName).
/// C++ parity: src/storage/index (per-NodeTable hash index registration).
/// </summary>
public sealed class NodePropertyIndex
{
    private readonly Dictionary<string, INodeIndex> _indexes
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>All currently indexed property names for this table.</summary>
    public IReadOnlyCollection<string> IndexedProperties => _indexes.Keys;

    /// <summary>True if any property is indexed.</summary>
    public bool HasAnyIndex => _indexes.Count > 0;

    /// <summary>Create (or reset) an index on <paramref name="propertyName"/>.</summary>
    public void CreateIndex(string propertyName)
    {
        if (!_indexes.ContainsKey(propertyName))
            _indexes[propertyName] = new InMemoryNodeIndex();
    }

    /// <summary>
    /// (Re)build the index on <paramref name="propertyName"/> from
    /// existing in-memory node table data.
    /// nodeOffset is the iter order position (0-based) used as the physical id.
    /// </summary>
    internal void Rebuild(string propertyName, NodeTableData table)
    {
        // Reuse existing index (may be DiskBackedNodeIndex); fall back to InMemoryNodeIndex.
        if (!_indexes.TryGetValue(propertyName, out var idx))
        {
            idx = new InMemoryNodeIndex();
            _indexes[propertyName] = idx;
        }
        else
        {
            idx.Clear();
        }

        long offset = 0;
        foreach (var (id, props) in table.EnumerateRows())
        {
            if (props.TryGetValue(propertyName, out var val) && val is not null)
                idx.Put(val, offset);
            offset++;
        }
    }

    internal void Rebuild(string propertyName, IEnumerable<KeyValuePair<object, Dictionary<string, object>>> nodes)
    {
        // Reuse existing index (may be DiskBackedNodeIndex); fall back to InMemoryNodeIndex.
        if (!_indexes.TryGetValue(propertyName, out var idx))
        {
            idx = new InMemoryNodeIndex();
            _indexes[propertyName] = idx;
        }
        else
        {
            idx.Clear();
        }

        long offset = 0;
        foreach (var (id, props) in nodes)
        {
            if (props.TryGetValue(propertyName, out var val) && val is not null)
                idx.Put(val, offset);
            offset++;
        }
    }

    internal void LoadEntries(
        string propertyName,
        IEnumerable<KeyValuePair<object, IReadOnlyList<long>>> entries)
    {
        _indexes[propertyName] = new SnapshotBackedNodeIndex(entries);
    }

    /// <summary>Insert or update one entry in the named property index.</summary>
    public void Put(string propertyName, object key, long nodeOffset)
    {
        if (_indexes.TryGetValue(propertyName, out var idx))
            idx.Put(key, nodeOffset);
    }

    /// <summary>
    /// Lookup a key in the named property index.
    /// Returns false if the property is not indexed or the key is not found.
    /// </summary>
    public bool TryLookup(string propertyName, object key, out long nodeOffset)
    {
        nodeOffset = -1;
        return _indexes.TryGetValue(propertyName, out var idx)
               && idx.TryLookup(key, out nodeOffset);
    }

    public bool TryLookupAll(string propertyName, object key, out IReadOnlyList<long> nodeOffsets)
    {
        nodeOffsets = Array.Empty<long>();
        return _indexes.TryGetValue(propertyName, out var idx)
               && idx.TryLookupAll(key, out nodeOffsets);
    }

    /// <summary>
    /// Collect all offsets in the named index whose key starts with <paramref name="prefix"/>.
    /// Returns false if the property is not indexed or no keys match.
    /// </summary>
    public bool TryLookupByPrefix(string propertyName, string prefix, out IReadOnlyList<long> nodeOffsets)
    {
        nodeOffsets = Array.Empty<long>();
        return _indexes.TryGetValue(propertyName, out var idx)
               && idx.TryLookupByPrefix(prefix, out nodeOffsets);
    }

    /// <summary>Remove a specific (key, offset) entry from the named property index.</summary>
    public bool Remove(string propertyName, object key, long nodeOffset)
    {
        return _indexes.TryGetValue(propertyName, out var idx) && idx.Remove(key, nodeOffset);
    }

    /// <summary>True if this table has an index on <paramref name="propertyName"/>.</summary>

    public bool HasIndex(string propertyName)
        => _indexes.ContainsKey(propertyName);

    public bool TryGetIndex(string propertyName, out INodeIndex? index)
        => _indexes.TryGetValue(propertyName, out index);

    public void DropIndex(string propertyName)
        => _indexes.Remove(propertyName);

    public void RenameIndex(string propertyName, string newPropertyName)
    {
        if (!_indexes.TryGetValue(propertyName, out var idx))
            return;

        _indexes.Remove(propertyName);
        _indexes[newPropertyName] = idx;
    }

    /// <summary>Number of keys stored in the named index.</summary>
    public long Count(string propertyName)
        => _indexes.TryGetValue(propertyName, out var idx) ? idx.Count : 0;

    /// <summary>Removes all entries from the named index while keeping the (disk-backed) index in place.</summary>
    public void ClearIndex(string propertyName)
    {
        if (_indexes.TryGetValue(propertyName, out var idx))
            idx.Clear();
    }

    internal IEnumerable<KeyValuePair<string, IEnumerable<KeyValuePair<object, IReadOnlyList<long>>>>> EnumerateEntries()
    {
        foreach (var (propertyName, idx) in _indexes)
        {
            yield return new KeyValuePair<string, IEnumerable<KeyValuePair<object, IReadOnlyList<long>>>>(
                propertyName,
                idx.EnumerateEntries().ToArray());
        }
    }

    /// <summary>
    /// Create (or reset) a disk-backed index on <paramref name="propertyName"/> that
    /// checkpoints to <paramref name="filePath"/>.
    /// </summary>
    public void CreateDiskBackedIndex(string propertyName, string filePath)
    {
        _indexes[propertyName] = new DiskBackedNodeIndex(filePath);
    }

    /// <summary>
    /// Load a disk-backed index from an existing file. Used during database reopen.
    /// </summary>
    internal void LoadDiskBackedIndex(string propertyName, string filePath)
    {
        if (System.IO.File.Exists(filePath))
            _indexes[propertyName] = new DiskBackedNodeIndex(filePath);
    }

    /// <summary>
    /// Returns true if the index for <paramref name="propertyName"/> is a <see cref="DiskBackedNodeIndex"/>.
    /// </summary>
    public bool IsDiskBacked(string propertyName)
        => _indexes.TryGetValue(propertyName, out var idx) && idx is DiskBackedNodeIndex;

    /// <summary>
    /// Checkpoint all disk-backed indexes to their respective files.
    /// Called during <c>BogDatabase.Checkpoint()</c>.
    /// </summary>
    public void CheckpointDiskIndexes()
    {
        foreach (var idx in _indexes.Values)
        {
            if (idx is DiskBackedNodeIndex diskIdx)
                diskIdx.Checkpoint();
        }
    }
}
