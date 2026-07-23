using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BogDb.Core.Common;
using BogDb.Core.Common.Diagnostics;
using BogDb.Core.Common.FileSystem;
using BogDb.Core.Extraction;
using BogDb.Core.Main.QueryResult;
using BogDb.Core.Storage;
using BogDb.Core.Storage.Table;
using BogDb.Core.Storage.Index;
using BogDb.Core.Transaction;
using CatalogModel = BogDb.Core.Catalog.Catalog;

namespace BogDb.Core.Main;

internal class NodeTableData : IVersionedTransactionParticipant, IRowUndoReplayHost
{
    private readonly List<object> _nodeIds = new();
    private readonly Dictionary<object, int> _rowIndexById = new();
    private readonly Dictionary<string, List<object?>> _columns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Column> _columnStores = new(StringComparer.OrdinalIgnoreCase);
    private readonly RowVersionStore _rowVersions = new();
    private readonly CompatibilityColumnMirror _columnMirror;
    private readonly VersionedPropertyStore _propertyStore;
    private readonly CompatibilityKeyedRowStore<object> _compatibilityRows;
    private readonly RowKeyIndex<object> _rowKeyIndex;
    private RowUndoReplayHandler? _undoReplayHandler;
    private string? _tableName;
    private GraphLogWriter? _graphLog;
    private WAL? _wal;
    private uint _tableId;
    private ColumnFactory? _columnFactory;

    // Compatibility view for existing call sites; do not mutate directly in new code.
    public Dictionary<object, Dictionary<string, object>> Data { get; } = new();
    private RowUndoReplayHandler UndoReplayHandler => _undoReplayHandler ??= new RowUndoReplayHandler(_rowVersions, _columnStores, this);

    public NodeTableData()
    {
        _columnMirror = new CompatibilityColumnMirror(_columns, _columnStores);
        _propertyStore = new VersionedPropertyStore(_columnStores, () => _nodeIds.Count);
        _compatibilityRows = new CompatibilityKeyedRowStore<object>(_nodeIds, Data, _columnMirror);
        _rowKeyIndex = new RowKeyIndex<object>(_nodeIds, _rowIndexById);
    }

    internal void BindPersistenceSurface(string tableName, GraphLogWriter graphLog, WAL? wal = null, uint tableId = 0, ColumnFactory? columnFactory = null)
    {
        _tableName = tableName;
        _graphLog = graphLog;
        _wal = wal;
        _tableId = tableId;
        _columnFactory = columnFactory;
        _propertyStore.SetColumnFactory(columnFactory);
        _columnMirror.SetColumnFactory(columnFactory);
    }

    internal void RenameProperty(string propertyName, string newPropertyName)
    {
        SyncFromCompatibilityViewIfNeeded();
        if (string.Equals(propertyName, newPropertyName, StringComparison.OrdinalIgnoreCase))
            return;
        if (!_columnStores.TryGetValue(propertyName, out var columnStore))
            throw new KeyNotFoundException($"Property {propertyName} not found.");
        if (_columnStores.ContainsKey(newPropertyName))
            throw new InvalidOperationException($"Property {newPropertyName} already exists.");

        _columnStores.Remove(propertyName);
        _columnStores[newPropertyName] = columnStore;

        if (_columns.TryGetValue(propertyName, out var compatibilityColumn))
        {
            _columns.Remove(propertyName);
            _columns[newPropertyName] = compatibilityColumn;
        }

        foreach (var row in Data.Values)
        {
            if (!row.TryGetValue(propertyName, out var value))
                continue;
            row.Remove(propertyName);
            row[newPropertyName] = value;
        }
    }

    public int Count
    {
        get
        {
            SyncFromCompatibilityViewIfNeeded();
            return _nodeIds.Count;
        }
    }

    public void Upsert(object id, Dictionary<string, object> properties)
    {
        UpsertInternal(id, properties, VersionInfo.AlwaysInsertedVersion);
    }

    public void Upsert(Transaction.Transaction tx, object id, Dictionary<string, object> properties)
    {
        SyncFromCompatibilityViewIfNeeded();
        if (_rowKeyIndex.TryGetIndex(id, out var latestRowIndexForKey))
        {
            var latestRowVersion = _rowVersions.Get(latestRowIndexForKey);
            if (latestRowVersion.DeleteVersion == tx.ID)
            {
                var appendedRowIndex = AppendNewRowVersion(id, properties, tx.ID);
                RegisterInsertUndo(tx, appendedRowIndex, id);
                LogNodeInsertionToWal(properties);
                return;
            }
        }

        if (TryFindVisibleRowIndex(tx, id, out var rowIndex))
        {
            var rowVersion = _rowVersions.Get(rowIndex);
            if (HasDeleteConflict(tx, rowVersion))
                throw new InvalidOperationException($"Write-write conflict updating row {rowIndex}.");

            if (rowVersion.DeleteVersion == tx.ID)
            {
                _rowVersions.Set(rowIndex, rowVersion with { DeleteVersion = VersionInfo.InvalidVersion });
            }

            ThrowIfColumnWriteConflict(rowIndex, tx, "updating");
            RegisterRowUpdateUndo(tx, rowIndex, id);
            _propertyStore.ClearRowProperties(tx, rowIndex);
            _propertyStore.ApplyRowProperties(tx, rowIndex, properties);
            LogNodeInsertionToWal(properties);
            return;
        }

        if (_rowKeyIndex.TryGetIndex(id, out rowIndex))
        {
            var rowVersion = _rowVersions.Get(rowIndex);
            if (rowVersion.DeleteVersion != VersionInfo.InvalidVersion &&
                rowVersion.DeleteVersion != tx.ID &&
                VersionInfo.IsVersionVisible(tx, rowVersion.DeleteVersion))
            {
                rowIndex = AppendNewRowVersion(id, properties, tx.ID);
                RegisterInsertUndo(tx, rowIndex, id);
                LogNodeInsertionToWal(properties);
                return;
            }

            if (TryGetLatestCommittedSnapshotCommitTs(rowIndex, out var latestCommitTs) &&
                latestCommitTs != VersionInfo.AlwaysInsertedVersion &&
                latestCommitTs > tx.StartTS)
            {
                throw new InvalidOperationException($"Write-write conflict updating row {rowIndex}.");
            }
        }

        rowIndex = UpsertInternal(id, properties, tx.ID);
        RegisterInsertUndo(tx, rowIndex, id);
        LogNodeInsertionToWal(properties);
    }

    private int UpsertInternal(object id, Dictionary<string, object> properties, ulong insertVersion)
    {
        if (_rowKeyIndex.TryGetIndex(id, out var rowIndex))
        {
            _compatibilityRows.ReplaceRow(rowIndex, properties);
            if (insertVersion == VersionInfo.AlwaysInsertedVersion)
                CommitColumnSnapshot(rowIndex, properties, VersionInfo.AlwaysInsertedVersion);
            var rowVersion = _rowVersions.Get(rowIndex);
            if (rowVersion.InsertVersion == VersionInfo.InvalidVersion)
                rowVersion = rowVersion with { InsertVersion = insertVersion };
            if (rowVersion.DeleteVersion == insertVersion)
                rowVersion = rowVersion with { DeleteVersion = VersionInfo.InvalidVersion };
            _rowVersions.Set(rowIndex, rowVersion);
            return rowIndex;
        }

        rowIndex = _rowKeyIndex.Count;
        _rowKeyIndex.Add(id);
        Data[id] = properties;
        _rowVersions.Add(insertVersion);
        AppendColumnRow(properties);
        if (insertVersion == VersionInfo.AlwaysInsertedVersion)
            CommitColumnSnapshot(rowIndex, properties, VersionInfo.AlwaysInsertedVersion);
        return rowIndex;
    }

    private int AppendNewRowVersion(object id, Dictionary<string, object> properties, ulong insertVersion)
    {
        var rowIndex = _rowKeyIndex.Count;
        _rowKeyIndex.Add(id);
        _rowVersions.Add(insertVersion);
        AppendColumnRow(properties);
        return rowIndex;
    }

    internal void NormalizeCommittedValues(BogDb.Core.Catalog.TableCatalogEntry? entry)
    {
        SyncFromCompatibilityViewIfNeeded();
        for (var rowIndex = 0; rowIndex < _rowKeyIndex.Count; rowIndex++)
        {
            if (!_rowVersions.IsCommittedVisible(rowIndex))
                continue;

            var props = BuildVisibleProperties(ulong.MaxValue, rowIndex);
            if (props is null)
                continue;

            var normalized = BogDb.Core.Catalog.PropertyValueCoercion.CoerceProperties(entry, props);
            if (!HaveEquivalentProperties(props, normalized))
            {
                _compatibilityRows.ReplaceRow(rowIndex, normalized);
                CommitColumnSnapshot(rowIndex, normalized, VersionInfo.AlwaysInsertedVersion);
            }
        }
    }

    public bool Remove(object id)
    {
        if (!_rowKeyIndex.TryGetIndex(id, out var rowIndex))
            return false;
        var (lastIndex, _, _) = _rowKeyIndex.RemoveSwap(id, rowIndex);
        Data.Remove(id);
        _rowVersions.Copy(rowIndex, lastIndex);
        _rowVersions.RemoveLast();

        // Swap-remove each column vector entry.
        foreach (var col in _columns.Values)
        {
            col[rowIndex] = col[lastIndex];
            col.RemoveAt(lastIndex);
        }
        foreach (var columnStore in _columnStores.Values)
        {
            if (rowIndex != lastIndex)
                columnStore.MoveRow(lastIndex, rowIndex);
            columnStore.Truncate(lastIndex);
        }

        return true;
    }

    public bool Remove(Transaction.Transaction tx, object id)
    {
        SyncFromCompatibilityViewIfNeeded();
        if (!TryFindVisibleRowIndex(tx, id, out var rowIndex))
            return false;

        var rowVersion = _rowVersions.Get(rowIndex);
        if (rowVersion.DeleteVersion == tx.ID)
            return true;
        if (rowVersion.DeleteVersion != VersionInfo.InvalidVersion && rowVersion.DeleteVersion > tx.StartTS)
            throw new InvalidOperationException($"Write-write conflict deleting row {rowIndex}.");
        ThrowIfColumnWriteConflict(rowIndex, tx, "deleting");
        tx.TrackVersionedRowAction(Storage.UndoRecordType.DELETE_INFO, UndoReplayHandler, rowIndex);
        _rowVersions.Set(rowIndex, rowVersion with { DeleteVersion = tx.ID });
        _wal?.LogNodeDeletion(_tableId, rowIndex, id);
        return true;
    }

    public bool TryGetProperties(object id, out Dictionary<string, object>? props)
    {
        SyncFromCompatibilityViewIfNeeded();
        if (!_rowKeyIndex.TryGetIndex(id, out var rowIndex))
        {
            props = null;
            return false;
        }

        var rowVersion = _rowVersions.Get(rowIndex);
        if (rowVersion.InsertVersion == VersionInfo.InvalidVersion ||
            rowVersion.DeleteVersion != VersionInfo.InvalidVersion)
        {
            props = null;
            return false;
        }

        return Data.TryGetValue(id, out props);
    }

    public bool TryGetProperties(Transaction.Transaction tx, object id, out Dictionary<string, object>? props)
    {
        SyncFromCompatibilityViewIfNeeded();
        if (!TryFindVisibleRowIndex(tx, id, out var rowIndex))
        {
            props = null;
            return false;
        }

        props = GetVisibleProperties(tx, rowIndex, id);
        return props is not null;
    }

    public bool SetProperty(Transaction.Transaction tx, object id, string propertyName, object? value)
    {
        SyncFromCompatibilityViewIfNeeded();
        if (!TryFindVisibleRowIndex(tx, id, out var rowIndex))
            return false;
        var rowVersion = _rowVersions.Get(rowIndex);
        if (HasDeleteConflict(tx, rowVersion))
            throw new InvalidOperationException($"Write-write conflict updating row {rowIndex}.");

        ThrowIfColumnWriteConflict(rowIndex, tx, "updating");
        RegisterRowUpdateUndo(tx, rowIndex, id, propertyName);
        var columnStore = GetOrCreateColumnStore(propertyName);
        columnStore.Update(tx, rowIndex, value);
        _wal?.LogNodeUpdate(_tableId, GetColumnOrdinal(propertyName), rowIndex, value);
        return true;
    }

    private uint GetColumnOrdinal(string propertyName)
    {
        uint ordinal = 0;
        foreach (var colName in _columnStores.Keys)
        {
            if (string.Equals(colName, propertyName, StringComparison.OrdinalIgnoreCase))
                return ordinal;
            ordinal++;
        }
        return ordinal;
    }

    /// <summary>
    /// WAL recovery: directly updates a property value at the given row offset,
    /// bypassing transaction checks and WAL logging (since we're replaying the WAL).
    /// </summary>
    internal void SetPropertyByRowIndex(long rowOffset, string propertyName, object? value)
    {
        SyncFromCompatibilityViewIfNeeded();
        var rowIndex = (int)rowOffset;
        if (rowIndex < 0 || rowIndex >= _nodeIds.Count)
            return;

        var columnStore = GetOrCreateColumnStore(propertyName);
        columnStore.DirectSet(rowIndex, value);

        // Also update the compatibility Data view
        var id = _nodeIds[rowIndex];
        if (Data.TryGetValue(id, out var props))
            props[propertyName] = value!;
    }

    private void LogNodeInsertionToWal(Dictionary<string, object> properties)
    {
        if (_wal == null) return;
        var colNames = properties.Keys.ToArray();
        var row = new List<object?>(properties.Values.Select(v => (object?)v));
        _wal.LogTableInsertion(_tableId, 0 /* NODE */, colNames, new List<List<object?>> { row });
    }

    public bool TryGetOffset(object id, out long offset)
    {
        SyncFromCompatibilityViewIfNeeded();
        if (_rowKeyIndex.TryGetIndex(id, out var idx))
        {
            offset = idx;
            return true;
        }

        offset = -1;
        return false;
    }

    public bool TryGetByOffset(long offset, out object nodeId, out Dictionary<string, object>? props)
    {
        SyncFromCompatibilityViewIfNeeded();
        if (offset < 0 || offset >= _rowKeyIndex.Count)
        {
            nodeId = string.Empty;
            props = null;
            return false;
        }

        if (!_rowVersions.IsCommittedVisible((int)offset))
        {
            nodeId = string.Empty;
            props = null;
            return false;
        }

        nodeId = _rowKeyIndex[(int)offset];
        props = Data[nodeId];
        return true;
    }

    public bool TryGetByOffset(
        Transaction.Transaction tx,
        long offset,
        out object nodeId,
        out Dictionary<string, object>? props)
    {
        SyncFromCompatibilityViewIfNeeded();
        if (offset < 0 || offset >= _rowKeyIndex.Count)
        {
            nodeId = string.Empty;
            props = null;
            return false;
        }

        if (!_rowVersions.IsVisible(tx, (int)offset))
        {
            nodeId = string.Empty;
            props = null;
            return false;
        }

        nodeId = _rowKeyIndex[(int)offset];
        props = GetVisibleProperties(tx, (int)offset, nodeId);
        return props is not null;
    }

    public IEnumerable<KeyValuePair<object, Dictionary<string, object>>> EnumerateRows()
    {
        SyncFromCompatibilityViewIfNeeded();
        for (var i = 0; i < _rowKeyIndex.Count; i++)
        {
            if (!_rowVersions.IsCommittedVisible(i))
                continue;
            var id = _rowKeyIndex[i];
            var props = BuildVisibleProperties(ulong.MaxValue, i);
            if (props is not null)
                yield return new KeyValuePair<object, Dictionary<string, object>>(id, props);
        }
    }

    public IEnumerable<KeyValuePair<object, Dictionary<string, object>>> EnumerateRows(Transaction.Transaction tx)
    {
        SyncFromCompatibilityViewIfNeeded();
        for (var i = 0; i < _rowKeyIndex.Count; i++)
        {
            if (!_rowVersions.IsVisible(tx, i))
                continue;
            var id = _rowKeyIndex[i];
            var props = GetVisibleProperties(tx, i, id);
            if (props is not null)
                yield return new KeyValuePair<object, Dictionary<string, object>>(id, props);
        }
    }

    public IReadOnlyDictionary<string, List<object?>> Columns => _columns;

    private void AppendColumnRow(Dictionary<string, object> properties)
        => _columnMirror.AppendRow(_nodeIds.Count, properties);

    private void OverwriteColumnRow(int rowIndex, Dictionary<string, object> properties)
        => _columnMirror.OverwriteRow(rowIndex, _nodeIds.Count, properties);

    private void SyncFromCompatibilityViewIfNeeded()
    {
        if (Data.Count <= _rowKeyIndex.Count)
        {
            foreach (var id in Data.Keys)
            {
                if (!_rowKeyIndex.ContainsKey(id))
                {
                    RebuildFromCompatibilityView();
                    return;
                }
            }
            return;
        }

        RebuildFromCompatibilityView();
    }

    private void RebuildFromCompatibilityView()
    {
        _rowKeyIndex.Clear();
        _columns.Clear();
        _columnStores.Clear();
        _rowVersions.Clear();

        foreach (var (id, props) in Data)
        {
            var rowIndex = _rowKeyIndex.Count;
            _rowKeyIndex.Add(id);
            _rowVersions.Add(VersionInfo.AlwaysInsertedVersion);

            foreach (var col in _columns.Values)
                col.Add(null);

            foreach (var (key, value) in props)
            {
                if (!_columns.TryGetValue(key, out var col))
                {
                    col = new List<object?>(_rowKeyIndex.Count);
                    for (var i = 0; i < _rowKeyIndex.Count; i++)
                        col.Add(null);
                    _columns[key] = col;
                }

                col[rowIndex] = value;
            }
            CommitColumnSnapshot(rowIndex, props, VersionInfo.AlwaysInsertedVersion);
        }
    }

    private static bool HaveEquivalentProperties(
        Dictionary<string, object> left,
        Dictionary<string, object> right)
    {
        if (left.Count != right.Count)
            return false;

        foreach (var (key, leftValue) in left)
        {
            if (!right.TryGetValue(key, out var rightValue) || !Equals(leftValue, rightValue))
                return false;
        }

        return true;
    }

    private void RefreshCommittedCompatibilityView()
    {
        Data.Clear();
        for (var rowIndex = 0; rowIndex < _rowKeyIndex.Count; rowIndex++)
        {
            if (!_rowVersions.IsCommittedVisible(rowIndex))
                continue;

            var key = _rowKeyIndex[rowIndex];
            var props = _propertyStore.BuildVisibleProperties(ulong.MaxValue, rowIndex);
            Data[key] = props;
        }
    }

    public void CommitVersionedChanges(Transaction.Transaction tx, ulong commitTS)
    {
        var insertedRows = new HashSet<int>();
        var rowsWithColumnUpdates = new HashSet<int>();

        for (var i = 0; i < _rowVersions.Count; i++)
        {
            if (_propertyStore.HasRowColumnVersion(i, tx.ID))
                rowsWithColumnUpdates.Add(i);
        }
        _rowVersions.Commit(tx, commitTS, insertedRows);

        foreach (var columnStore in _columnStores.Values)
            columnStore.CommitUpdates(tx, commitTS);

        var committedReader = new Transaction.Transaction(
            TransactionType.READ_ONLY,
            id: 1,
            startTS: commitTS);

        foreach (var rowIndex in rowsWithColumnUpdates)
        {
            var nodeId = _rowKeyIndex[rowIndex];
            var props = _propertyStore.BuildVisibleProperties(committedReader, rowIndex);
            _compatibilityRows.ReplaceRow(rowIndex, props);
        }

        foreach (var rowIndex in insertedRows)
        {
            if (rowsWithColumnUpdates.Contains(rowIndex))
                continue;
            var props = _propertyStore.BuildVisibleProperties(committedReader, rowIndex);
            _propertyStore.CommitSnapshot(rowIndex, props, commitTS);
        }

        RefreshCommittedCompatibilityView();
    }

    public void CommitVersions(Transaction.Transaction tx, ulong commitTS)
        => CommitVersionedChanges(tx, commitTS);

    public void RollbackVersionedChanges(Transaction.Transaction tx)
    {
        _rowVersions.Rollback(tx);

        foreach (var columnStore in _columnStores.Values)
            columnStore.RollbackUpdates(tx);
        TrimRolledBackTail();
        RefreshCommittedCompatibilityView();
    }

    public void RollbackVersions(Transaction.Transaction tx)
        => RollbackVersionedChanges(tx);

    private Dictionary<string, object>? GetVisibleProperties(Transaction.Transaction tx, int rowIndex, object id)
        => GetCommittedPropertiesVisibleTo(tx, rowIndex, id);

    private bool HasDeleteConflict(Transaction.Transaction tx, RowVersionState rowVersion)
        => rowVersion.DeleteVersion != VersionInfo.InvalidVersion &&
           rowVersion.DeleteVersion != tx.ID &&
           rowVersion.DeleteVersion > tx.StartTS;

    private void ThrowIfColumnWriteConflict(int rowIndex, Transaction.Transaction tx, string operation)
        => _propertyStore.ThrowIfWriteConflict(rowIndex, tx, operation);

    private void TrimRolledBackTail()
    {
        var newCount = _rowVersions.GetTrimmedRowCount();

        if (newCount == _rowVersions.Count)
            return;

        _compatibilityRows.TrimTail(newCount, (nodeId, _) => _rowKeyIndex.Remove(nodeId));
        _rowVersions.RemoveRange(newCount, _rowVersions.Count - newCount);
    }

    private Dictionary<string, object>? GetCommittedPropertiesVisibleTo(
        Transaction.Transaction tx,
        int rowIndex,
        object id)
    {
        if (_columnStores.Count > 0)
        {
            if (TryGetLatestCommittedSnapshotCommitTs(rowIndex, out var latestCommitTs) &&
                latestCommitTs != VersionInfo.AlwaysInsertedVersion &&
                latestCommitTs > tx.StartTS)
            {
                return _propertyStore.BuildVisibleProperties(tx.StartTS, rowIndex);
            }

            return _propertyStore.BuildVisibleProperties(tx, rowIndex);
        }

        return Data.TryGetValue(id, out var props) ? props : null;
    }

    private void CommitColumnSnapshot(int rowIndex, Dictionary<string, object> properties, ulong commitTs)
        => _propertyStore.CommitSnapshot(rowIndex, properties, commitTs);

    private bool TryGetLatestCommittedSnapshotCommitTs(int rowIndex, out ulong commitTs)
        => _propertyStore.TryGetLatestCommittedSnapshotCommitTs(rowIndex, out commitTs);

    private Dictionary<string, object> BuildVisibleProperties(Transaction.Transaction tx, int rowIndex)
        => _propertyStore.BuildVisibleProperties(tx, rowIndex);

    private Dictionary<string, object> BuildVisibleProperties(ulong visibleCommitVersion, int rowIndex)
        => _propertyStore.BuildVisibleProperties(visibleCommitVersion, rowIndex);

    private Column GetOrCreateColumnStore(string name)
    {
        if (_columnStores.TryGetValue(name, out var existing))
            return existing;

        var columnStore = _columnFactory?.CreateColumn(name, Math.Max(1024, _rowKeyIndex.Count))
            ?? new Column(name, Math.Max(1024, _rowKeyIndex.Count));
        for (var i = 0; i < _rowKeyIndex.Count; i++)
            columnStore.Append(null);
        _columnStores[name] = columnStore;
        return columnStore;
    }

    private bool HasRowColumnVersion(int rowIndex, ulong version)
        => _propertyStore.HasRowColumnVersion(rowIndex, version);

    internal bool AddProperty(string propertyName, object? defaultValue = null)
    {
        SyncFromCompatibilityViewIfNeeded();
        if (_columnStores.ContainsKey(propertyName))
            return false;

        var columnStore = _columnFactory?.CreateColumn(propertyName, Math.Max(1024, _rowKeyIndex.Count))
            ?? new Column(propertyName, Math.Max(1024, _rowKeyIndex.Count));
        var compatibilityColumn = new List<object?>(_rowKeyIndex.Count);
        for (var i = 0; i < _rowKeyIndex.Count; i++)
        {
            columnStore.Append(defaultValue);
            compatibilityColumn.Add(defaultValue);
        }

        _columnStores[propertyName] = columnStore;
        _columns[propertyName] = compatibilityColumn;
        if (defaultValue is not null)
        {
            foreach (var row in Data.Values)
                row[propertyName] = defaultValue;
        }
        return true;
    }

    internal bool DropProperty(string propertyName)
    {
        SyncFromCompatibilityViewIfNeeded();
        var removed = _columnStores.Remove(propertyName);
        removed = _columns.Remove(propertyName) || removed;
        if (!removed)
            return false;

        foreach (var row in Data.Values)
            row.Remove(propertyName);

        return true;
    }

    private bool TryFindVisibleRowIndex(Transaction.Transaction tx, object id, out int rowIndex)
        => _rowKeyIndex.TryFindVisibleIndex(id, i => IsRowVisibleTo(tx, i), out rowIndex);

    private bool IsRowVisibleTo(Transaction.Transaction tx, int rowIndex)
        => _rowVersions.IsVisible(tx, rowIndex);

    private void RegisterInsertUndo(Transaction.Transaction tx, int rowIndex, object id)
    {
        tx.TrackVersionedRowAction(Storage.UndoRecordType.INSERT_INFO, UndoReplayHandler, rowIndex);
    }

    private void RegisterRowUpdateUndo(
        Transaction.Transaction tx,
        int rowIndex,
        object id,
        string? propertyName = null)
    {
        tx.TrackVersionedRowAction(Storage.UndoRecordType.UPDATE_INFO, UndoReplayHandler, rowIndex);
    }

    void IRowUndoReplayHost.ReplaceCommittedRow(int rowIndex, Dictionary<string, object> properties)
        => _compatibilityRows.ReplaceRow(rowIndex, properties);

    void IRowUndoReplayHost.TrimRolledBackTail()
        => TrimRolledBackTail();

    void IRowUndoReplayHost.PersistCommittedUpdate(int rowIndex, Dictionary<string, object> properties)
    {
        if (_graphLog is null || string.IsNullOrEmpty(_tableName) || rowIndex < 0 || rowIndex >= _rowKeyIndex.Count)
            return;

        _graphLog.AppendNode(_tableName, _rowKeyIndex[rowIndex], properties);
    }

    void IRowUndoReplayHost.PersistCommittedDelete(int rowIndex)
    {
        if (_graphLog is null || string.IsNullOrEmpty(_tableName) || rowIndex < 0 || rowIndex >= _rowKeyIndex.Count)
            return;

        _graphLog.AppendNodeDelete(_tableName, _rowKeyIndex[rowIndex]);
    }
}

internal class RelTableData : IVersionedTransactionParticipant, IRowUndoReplayHost
{
    internal readonly record struct AdjacencyEntry(int RowIndex);

    private readonly List<EdgeKey> _edgeKeys = new();
    private readonly Dictionary<EdgeKey, int> _rowIndexByKey = new();
    private readonly Dictionary<string, List<object?>> _columns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Column> _columnStores = new(StringComparer.OrdinalIgnoreCase);
    private readonly RowVersionStore _rowVersions = new();
    private readonly CompatibilityColumnMirror _columnMirror;
    private readonly VersionedPropertyStore _propertyStore;
    private readonly CompatibilityKeyedRowStore<EdgeKey> _compatibilityRows;
    private readonly RowKeyIndex<EdgeKey> _rowKeyIndex;
    private RowUndoReplayHandler? _undoReplayHandler;
    private readonly Dictionary<object, List<AdjacencyEntry>> _outAdj = new();
    private readonly Dictionary<object, List<AdjacencyEntry>> _inAdj = new();
    private readonly RelAdjacencyIndex _adjacencyIndex;
    private string? _tableName;
    private GraphLogWriter? _graphLog;
    private WAL? _wal;
    private uint _tableId;
    private ColumnFactory? _columnFactory;

    // Compatibility view for existing call sites; do not mutate directly in new code.
    public Dictionary<EdgeKey, Dictionary<string, object>> Data { get; } = new();
    private RowUndoReplayHandler UndoReplayHandler => _undoReplayHandler ??= new RowUndoReplayHandler(_rowVersions, _columnStores, this);
    public string FromTableName { get; private set; } = string.Empty;
    public string ToTableName { get; private set; } = string.Empty;

    public RelTableData()
    {
        _columnMirror = new CompatibilityColumnMirror(_columns, _columnStores);
        _propertyStore = new VersionedPropertyStore(_columnStores, () => _edgeKeys.Count);
        _compatibilityRows = new CompatibilityKeyedRowStore<EdgeKey>(_edgeKeys, Data, _columnMirror);
        _rowKeyIndex = new RowKeyIndex<EdgeKey>(_edgeKeys, _rowIndexByKey);
        _adjacencyIndex = new RelAdjacencyIndex(_outAdj, _inAdj);
    }

    public RelTableData(string fromTableName, string toTableName)
    {
        _columnMirror = new CompatibilityColumnMirror(_columns, _columnStores);
        _propertyStore = new VersionedPropertyStore(_columnStores, () => _edgeKeys.Count);
        _compatibilityRows = new CompatibilityKeyedRowStore<EdgeKey>(_edgeKeys, Data, _columnMirror);
        _rowKeyIndex = new RowKeyIndex<EdgeKey>(_edgeKeys, _rowIndexByKey);
        _adjacencyIndex = new RelAdjacencyIndex(_outAdj, _inAdj);
        SetEndpointTables(fromTableName, toTableName);
    }

    internal void BindPersistenceSurface(string tableName, GraphLogWriter graphLog, WAL? wal = null, uint tableId = 0, ColumnFactory? columnFactory = null)
    {
        _tableName = tableName;
        _graphLog = graphLog;
        _wal = wal;
        _tableId = tableId;
        _columnFactory = columnFactory;
        _propertyStore.SetColumnFactory(columnFactory);
        _columnMirror.SetColumnFactory(columnFactory);
    }

    internal void RenameProperty(string propertyName, string newPropertyName)
    {
        SyncFromCompatibilityViewIfNeeded();
        if (string.Equals(propertyName, newPropertyName, StringComparison.OrdinalIgnoreCase))
            return;
        if (!_columnStores.TryGetValue(propertyName, out var columnStore))
            throw new KeyNotFoundException($"Property {propertyName} not found.");
        if (_columnStores.ContainsKey(newPropertyName))
            throw new InvalidOperationException($"Property {newPropertyName} already exists.");

        _columnStores.Remove(propertyName);
        _columnStores[newPropertyName] = columnStore;

        if (_columns.TryGetValue(propertyName, out var compatibilityColumn))
        {
            _columns.Remove(propertyName);
            _columns[newPropertyName] = compatibilityColumn;
        }

        foreach (var row in Data.Values)
        {
            if (!row.TryGetValue(propertyName, out var value))
                continue;
            row.Remove(propertyName);
            row[newPropertyName] = value;
        }
    }

    public int Count
    {
        get
        {
            SyncFromCompatibilityViewIfNeeded();
            return _edgeKeys.Count;
        }
    }
    public IReadOnlyDictionary<string, List<object?>> Columns => _columns;

    public void SetEndpointTables(string? fromTableName, string? toTableName)
    {
        var normalizedFrom = fromTableName?.Trim() ?? string.Empty;
        var normalizedTo = toTableName?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(normalizedFrom) || string.IsNullOrEmpty(normalizedTo))
            return;

        FromTableName = normalizedFrom;
        ToTableName = normalizedTo;
    }

    public void ClearEndpointTables()
    {
        FromTableName = string.Empty;
        ToTableName = string.Empty;
    }

    public bool TryGetEndpointTables(out string fromTableName, out string toTableName)
    {
        fromTableName = FromTableName;
        toTableName = ToTableName;
        return !string.IsNullOrEmpty(fromTableName) && !string.IsNullOrEmpty(toTableName);
    }

    public void Upsert(EdgeKey key, Dictionary<string, object> properties)
    {
        UpsertInternal(key, properties, VersionInfo.AlwaysInsertedVersion);
    }

    internal void NormalizeCommittedValues(BogDb.Core.Catalog.TableCatalogEntry? entry)
    {
        SyncFromCompatibilityViewIfNeeded();
        for (var rowIndex = 0; rowIndex < _rowKeyIndex.Count; rowIndex++)
        {
            if (!_rowVersions.IsCommittedVisible(rowIndex))
                continue;

            var props = BuildVisibleProperties(ulong.MaxValue, rowIndex);
            if (props is null)
                continue;

            var normalized = BogDb.Core.Catalog.PropertyValueCoercion.CoerceProperties(entry, props);
            if (!HaveEquivalentProperties(props, normalized))
            {
                _compatibilityRows.ReplaceRow(rowIndex, normalized);
                CommitColumnSnapshot(rowIndex, normalized, VersionInfo.AlwaysInsertedVersion);
            }
        }
    }

    public int Insert(EdgeKey key, Dictionary<string, object> properties)
    {
        SyncFromCompatibilityViewIfNeeded();
        return AppendCommittedRow(key, properties);
    }

    public void Upsert(Transaction.Transaction tx, EdgeKey key, Dictionary<string, object> properties)
    {
        SyncFromCompatibilityViewIfNeeded();
        if (_rowKeyIndex.TryGetIndex(key, out var latestRowIndexForKey))
        {
            var latestRowVersion = _rowVersions.Get(latestRowIndexForKey);
            if (latestRowVersion.DeleteVersion == tx.ID)
            {
                var appendedRowIndex = AppendNewRowVersion(key, properties, tx.ID);
                RegisterInsertUndo(tx, appendedRowIndex, key);
                LogRelInsertionToWal(key, properties);
                return;
            }
        }

        if (TryFindVisibleRowIndex(tx, key, out var rowIndex))
        {
            var rowVersion = _rowVersions.Get(rowIndex);
            if (HasDeleteConflict(tx, rowVersion))
                throw new InvalidOperationException($"Write-write conflict updating row {rowIndex}.");

            if (rowVersion.DeleteVersion == tx.ID)
            {
                _rowVersions.Set(rowIndex, rowVersion with { DeleteVersion = VersionInfo.InvalidVersion });
            }

            ThrowIfColumnWriteConflict(rowIndex, tx, "updating");
            RegisterRowUpdateUndo(tx, rowIndex, key);
            _propertyStore.ClearRowProperties(tx, rowIndex);
            _propertyStore.ApplyRowProperties(tx, rowIndex, properties);
            LogRelInsertionToWal(key, properties);
            return;
        }

        if (_rowKeyIndex.TryGetIndex(key, out rowIndex))
        {
            var rowVersion = _rowVersions.Get(rowIndex);
            if (rowVersion.DeleteVersion != VersionInfo.InvalidVersion &&
                rowVersion.DeleteVersion != tx.ID &&
                VersionInfo.IsVersionVisible(tx, rowVersion.DeleteVersion))
            {
                rowIndex = AppendNewRowVersion(key, properties, tx.ID);
                RegisterInsertUndo(tx, rowIndex, key);
                LogRelInsertionToWal(key, properties);
                return;
            }

            if (TryGetLatestCommittedSnapshotCommitTs(rowIndex, out var latestCommitTs) &&
                latestCommitTs != VersionInfo.AlwaysInsertedVersion &&
                latestCommitTs > tx.StartTS)
            {
                throw new InvalidOperationException($"Write-write conflict updating row {rowIndex}.");
            }
        }

        rowIndex = UpsertInternal(key, properties, tx.ID);
        RegisterInsertUndo(tx, rowIndex, key);
        LogRelInsertionToWal(key, properties);
    }

    public int Insert(Transaction.Transaction tx, EdgeKey key, Dictionary<string, object> properties)
    {
        SyncFromCompatibilityViewIfNeeded();
        var rowIndex = AppendNewRowVersion(key, properties, tx.ID);
        RegisterInsertUndo(tx, rowIndex, key);
        LogRelInsertionToWal(key, properties);
        return rowIndex;
    }

    private int UpsertInternal(EdgeKey key, Dictionary<string, object> properties, ulong insertVersion)
    {
        if (_rowKeyIndex.TryGetIndex(key, out var rowIndex))
        {
            _compatibilityRows.ReplaceRow(rowIndex, properties);
            if (insertVersion == VersionInfo.AlwaysInsertedVersion)
                CommitColumnSnapshot(rowIndex, properties, VersionInfo.AlwaysInsertedVersion);
            var rowVersion = _rowVersions.Get(rowIndex);
            if (rowVersion.InsertVersion == VersionInfo.InvalidVersion)
                rowVersion = rowVersion with { InsertVersion = insertVersion };
            if (rowVersion.DeleteVersion == insertVersion)
                rowVersion = rowVersion with { DeleteVersion = VersionInfo.InvalidVersion };
            _rowVersions.Set(rowIndex, rowVersion);
            return rowIndex;
        }

        rowIndex = _rowKeyIndex.Count;
        _rowKeyIndex.Add(key);
        Data[key] = properties;
        _rowVersions.Add(insertVersion);
        AppendColumnRow(properties);
        _adjacencyIndex.Add(key, rowIndex);
        if (insertVersion == VersionInfo.AlwaysInsertedVersion)
            CommitColumnSnapshot(rowIndex, properties, VersionInfo.AlwaysInsertedVersion);
        return rowIndex;
    }

    private int AppendCommittedRow(EdgeKey key, Dictionary<string, object> properties)
    {
        var rowIndex = _rowKeyIndex.Count;
        _rowKeyIndex.Add(key);
        _rowVersions.Add(VersionInfo.AlwaysInsertedVersion);
        AppendColumnRow(properties);
        _adjacencyIndex.Add(key, rowIndex);
        CommitColumnSnapshot(rowIndex, properties, VersionInfo.AlwaysInsertedVersion);
        RefreshCommittedCompatibilityView();
        return rowIndex;
    }

    private int AppendNewRowVersion(EdgeKey key, Dictionary<string, object> properties, ulong insertVersion)
    {
        var rowIndex = _rowKeyIndex.Count;
        _rowKeyIndex.Add(key);
        _rowVersions.Add(insertVersion);
        AppendColumnRow(properties);
        _adjacencyIndex.Add(key, rowIndex);
        return rowIndex;
    }

    public bool Remove(EdgeKey key)
    {
        if (!_rowKeyIndex.TryGetIndex(key, out var rowIndex))
            return false;
        var (lastIndex, lastKey, moved) = _rowKeyIndex.RemoveSwap(key, rowIndex);

        _adjacencyIndex.Remove(key, rowIndex);

        if (moved)
        {
            _adjacencyIndex.Replace(lastKey, lastIndex, rowIndex);
        }
        Data.Remove(key);
        _rowVersions.Copy(rowIndex, lastIndex);
        _rowVersions.RemoveLast();

        foreach (var col in _columns.Values)
        {
            col[rowIndex] = col[lastIndex];
            col.RemoveAt(lastIndex);
        }
        foreach (var columnStore in _columnStores.Values)
        {
            if (rowIndex != lastIndex)
                columnStore.MoveRow(lastIndex, rowIndex);
            columnStore.Truncate(lastIndex);
        }

        return true;
    }

    public bool Remove(Transaction.Transaction tx, EdgeKey key)
    {
        SyncFromCompatibilityViewIfNeeded();
        if (!TryFindVisibleRowIndex(tx, key, out var rowIndex))
            return false;
        return Remove(tx, rowIndex);
    }

    public bool Remove(Transaction.Transaction tx, int rowIndex)
    {
        SyncFromCompatibilityViewIfNeeded();
        if (rowIndex < 0 || rowIndex >= _rowKeyIndex.Count || !_rowVersions.IsVisible(tx, rowIndex))
            return false;

        var rowVersion = _rowVersions.Get(rowIndex);
        if (rowVersion.DeleteVersion == tx.ID)
            return true;
        if (rowVersion.DeleteVersion != VersionInfo.InvalidVersion && rowVersion.DeleteVersion > tx.StartTS)
            throw new InvalidOperationException($"Write-write conflict deleting row {rowIndex}.");
        ThrowIfColumnWriteConflict(rowIndex, tx, "deleting");
        tx.TrackVersionedRowAction(Storage.UndoRecordType.DELETE_INFO, UndoReplayHandler, rowIndex);
        _rowVersions.Set(rowIndex, rowVersion with { DeleteVersion = tx.ID });
        var key = _rowKeyIndex[rowIndex];
        _wal?.LogRelDeletion(_tableId, key.From.GetHashCode(), key.To.GetHashCode(), rowIndex);
        return true;
    }

    /// <summary>
    /// WAL recovery: directly removes the relationship at the given row index,
    /// bypassing transaction checks and WAL logging.
    /// </summary>
    internal void RemoveByRowIndex(long rowOffset)
    {
        SyncFromCompatibilityViewIfNeeded();
        var rowIndex = (int)rowOffset;
        if (rowIndex < 0 || rowIndex >= _edgeKeys.Count)
            return;

        var key = _edgeKeys[rowIndex];
        _rowVersions.Set(rowIndex, _rowVersions.Get(rowIndex) with
        {
            DeleteVersion = VersionInfo.InvalidVersion - 1
        });
        Data.Remove(key);
    }

    /// <summary>
    /// WAL recovery: directly updates a property value at the given row offset,
    /// bypassing transaction checks and WAL logging.
    /// </summary>
    internal void SetPropertyByRowIndex(long rowOffset, string propertyName, object? value)
    {
        SyncFromCompatibilityViewIfNeeded();
        var rowIndex = (int)rowOffset;
        if (rowIndex < 0 || rowIndex >= _edgeKeys.Count)
            return;

        var columnStore = GetOrCreateColumnStore(propertyName);
        columnStore.DirectSet(rowIndex, value);

        // Also update the compatibility Data view
        var key = _edgeKeys[rowIndex];
        if (Data.TryGetValue(key, out var props))
            props[propertyName] = value!;
    }

    public bool TryGetProperties(EdgeKey key, out Dictionary<string, object>? props)
    {
        SyncFromCompatibilityViewIfNeeded();
        if (!_rowKeyIndex.TryGetIndex(key, out var rowIndex))
        {
            props = null;
            return false;
        }

        if (!_rowVersions.IsCommittedVisible(rowIndex))
        {
            props = null;
            return false;
        }

        return Data.TryGetValue(key, out props);
    }

    public bool TryGetProperties(
        Transaction.Transaction tx,
        EdgeKey key,
        out Dictionary<string, object>? props)
    {
        SyncFromCompatibilityViewIfNeeded();
        if (!TryFindVisibleRowIndex(tx, key, out var rowIndex))
        {
            props = null;
            return false;
        }

        props = GetVisibleProperties(tx, rowIndex, key);
        return props is not null;
    }

    public bool TryFindVisibleRow(
        Transaction.Transaction tx,
        EdgeKey key,
        Func<Dictionary<string, object>, bool> predicate,
        out int rowIndex,
        out Dictionary<string, object>? props)
    {
        SyncFromCompatibilityViewIfNeeded();
        for (var i = _rowKeyIndex.Count - 1; i >= 0; i--)
        {
            if (!EqualityComparer<EdgeKey>.Default.Equals(_rowKeyIndex[i], key))
                continue;
            if (!_rowVersions.IsVisible(tx, i))
                continue;

            var visible = GetVisibleProperties(tx, i, key);
            if (visible is null || !predicate(visible))
                continue;

            rowIndex = i;
            props = visible;
            return true;
        }

        rowIndex = -1;
        props = null;
        return false;
    }

    public bool SetProperty(Transaction.Transaction tx, EdgeKey key, string propertyName, object? value)
    {
        SyncFromCompatibilityViewIfNeeded();
        if (!TryFindVisibleRowIndex(tx, key, out var rowIndex))
            return false;
        return SetProperty(tx, rowIndex, propertyName, value);
    }

    public bool SetProperty(Transaction.Transaction tx, int rowIndex, string propertyName, object? value)
    {
        SyncFromCompatibilityViewIfNeeded();
        if (rowIndex < 0 || rowIndex >= _rowKeyIndex.Count || !_rowVersions.IsVisible(tx, rowIndex))
            return false;
        var rowVersion = _rowVersions.Get(rowIndex);
        if (HasDeleteConflict(tx, rowVersion))
            throw new InvalidOperationException($"Write-write conflict updating row {rowIndex}.");

        ThrowIfColumnWriteConflict(rowIndex, tx, "updating");
        RegisterRowUpdateUndo(tx, rowIndex, _rowKeyIndex[rowIndex], propertyName);
        var columnStore = GetOrCreateColumnStore(propertyName);
        columnStore.Update(tx, rowIndex, value);
        var key = _rowKeyIndex[rowIndex];
        _wal?.LogRelUpdate(_tableId, GetColumnOrdinal(propertyName),
            key.From.GetHashCode(), key.To.GetHashCode(), rowIndex, value);
        return true;
    }

    private uint GetColumnOrdinal(string propertyName)
    {
        uint ordinal = 0;
        foreach (var colName in _columnStores.Keys)
        {
            if (string.Equals(colName, propertyName, StringComparison.OrdinalIgnoreCase))
                return ordinal;
            ordinal++;
        }
        return ordinal;
    }

    private void LogRelInsertionToWal(EdgeKey key, Dictionary<string, object> properties)
    {
        if (_wal == null) return;
        var colNames = properties.Keys.ToArray();
        var row = new List<object?>(properties.Values.Select(v => (object?)v));
        _wal.LogTableInsertion(_tableId, 1 /* REL */, colNames, new List<List<object?>> { row });
    }

    public IEnumerable<KeyValuePair<EdgeKey, Dictionary<string, object>>> EnumerateRows()
    {
        SyncFromCompatibilityViewIfNeeded();
        for (var i = 0; i < _rowKeyIndex.Count; i++)
        {
            if (!_rowVersions.IsCommittedVisible(i))
                continue;
            var key = _rowKeyIndex[i];
            var props = BuildVisibleProperties(ulong.MaxValue, i);
            if (props is not null)
                yield return new KeyValuePair<EdgeKey, Dictionary<string, object>>(key, props);
        }
    }

    public IEnumerable<KeyValuePair<EdgeKey, Dictionary<string, object>>> EnumerateRows(Transaction.Transaction tx)
    {
        SyncFromCompatibilityViewIfNeeded();
        for (var i = 0; i < _rowKeyIndex.Count; i++)
        {
            if (!_rowVersions.IsVisible(tx, i))
                continue;
            var key = _rowKeyIndex[i];
            var props = GetVisibleProperties(tx, i, key);
            if (props is not null)
                yield return new KeyValuePair<EdgeKey, Dictionary<string, object>>(key, props);
        }
    }

    public IEnumerable<KeyValuePair<RelRowRef, Dictionary<string, object>>> EnumerateRowsWithRefs(
        string tableName,
        Transaction.Transaction tx)
    {
        SyncFromCompatibilityViewIfNeeded();
        for (var i = 0; i < _rowKeyIndex.Count; i++)
        {
            if (!_rowVersions.IsVisible(tx, i))
                continue;
            var key = _rowKeyIndex[i];
            var props = GetVisibleProperties(tx, i, key);
            if (props is not null)
                yield return new KeyValuePair<RelRowRef, Dictionary<string, object>>(new RelRowRef(tableName, i, key), props);
        }
    }

    public IReadOnlyList<AdjacencyEntry> GetOutgoingEdgeRows(object nodeId)
    {
        SyncFromCompatibilityViewIfNeeded();
        return _adjacencyIndex.GetOutgoing(nodeId);
    }

    public IReadOnlyList<AdjacencyEntry> GetIncomingEdgeRows(object nodeId)
    {
        SyncFromCompatibilityViewIfNeeded();
        return _adjacencyIndex.GetIncoming(nodeId);
    }

    public bool TryGetRowByIndex(int rowIndex, out EdgeKey key, out Dictionary<string, object>? props)
    {
        SyncFromCompatibilityViewIfNeeded();
        if (rowIndex < 0 || rowIndex >= _rowKeyIndex.Count)
        {
            key = default;
            props = null;
            return false;
        }

        if (!_rowVersions.IsCommittedVisible(rowIndex))
        {
            key = default;
            props = null;
            return false;
        }

        key = _rowKeyIndex[rowIndex];
        props = BuildVisibleProperties(ulong.MaxValue, rowIndex);
        return props is not null;
    }

    public bool TryGetRowByIndex(
        Transaction.Transaction tx,
        int rowIndex,
        out EdgeKey key,
        out Dictionary<string, object>? props)
    {
        SyncFromCompatibilityViewIfNeeded();
        if (rowIndex < 0 || rowIndex >= _rowKeyIndex.Count)
        {
            key = default;
            props = null;
            return false;
        }

        if (!_rowVersions.IsVisible(tx, rowIndex))
        {
            key = default;
            props = null;
            return false;
        }

        key = _rowKeyIndex[rowIndex];
        props = GetVisibleProperties(tx, rowIndex, key);
        return props is not null;
    }

    public bool ReplaceRow(Transaction.Transaction tx, int rowIndex, Dictionary<string, object> properties)
    {
        SyncFromCompatibilityViewIfNeeded();
        if (rowIndex < 0 || rowIndex >= _rowKeyIndex.Count || !_rowVersions.IsVisible(tx, rowIndex))
            return false;

        var rowVersion = _rowVersions.Get(rowIndex);
        if (HasDeleteConflict(tx, rowVersion))
            throw new InvalidOperationException($"Write-write conflict updating row {rowIndex}.");

        ThrowIfColumnWriteConflict(rowIndex, tx, "updating");
        RegisterRowUpdateUndo(tx, rowIndex, _rowKeyIndex[rowIndex]);
        _propertyStore.ClearRowProperties(tx, rowIndex);
        _propertyStore.ApplyRowProperties(tx, rowIndex, properties);
        return true;
    }

    public IEnumerable<KeyValuePair<EdgeKey, Dictionary<string, object>>> EnumerateOutgoingRows(
        object nodeId,
        Transaction.Transaction? tx = null)
    {
        foreach (var entry in GetOutgoingEdgeRows(nodeId))
        {
            if (tx is null)
            {
                if (TryGetRowByIndex(entry.RowIndex, out var key, out var props) && props is not null)
                    yield return new KeyValuePair<EdgeKey, Dictionary<string, object>>(key, props);
                continue;
            }

            if (TryGetRowByIndex(tx, entry.RowIndex, out var keyTx, out var propsTx) && propsTx is not null)
                yield return new KeyValuePair<EdgeKey, Dictionary<string, object>>(keyTx, propsTx);
        }
    }

    public IEnumerable<KeyValuePair<RelRowRef, Dictionary<string, object>>> EnumerateOutgoingRowsWithRefs(
        string tableName,
        object nodeId,
        Transaction.Transaction tx)
    {
        foreach (var entry in GetOutgoingEdgeRows(nodeId))
        {
            if (TryGetRowByIndex(tx, entry.RowIndex, out var key, out var props) && props is not null)
                yield return new KeyValuePair<RelRowRef, Dictionary<string, object>>(new RelRowRef(tableName, entry.RowIndex, key), props);
        }
    }

    public IEnumerable<KeyValuePair<EdgeKey, Dictionary<string, object>>> EnumerateIncomingRows(
        object nodeId,
        Transaction.Transaction? tx = null)
    {
        foreach (var entry in GetIncomingEdgeRows(nodeId))
        {
            if (tx is null)
            {
                if (TryGetRowByIndex(entry.RowIndex, out var key, out var props) && props is not null)
                    yield return new KeyValuePair<EdgeKey, Dictionary<string, object>>(key, props);
                continue;
            }

            if (TryGetRowByIndex(tx, entry.RowIndex, out var keyTx, out var propsTx) && propsTx is not null)
                yield return new KeyValuePair<EdgeKey, Dictionary<string, object>>(keyTx, propsTx);
        }
    }

    public IEnumerable<KeyValuePair<RelRowRef, Dictionary<string, object>>> EnumerateIncomingRowsWithRefs(
        string tableName,
        object nodeId,
        Transaction.Transaction tx)
    {
        foreach (var entry in GetIncomingEdgeRows(nodeId))
        {
            if (TryGetRowByIndex(tx, entry.RowIndex, out var key, out var props) && props is not null)
                yield return new KeyValuePair<RelRowRef, Dictionary<string, object>>(new RelRowRef(tableName, entry.RowIndex, key), props);
        }
    }

    private void AppendColumnRow(Dictionary<string, object> properties)
        => _columnMirror.AppendRow(_edgeKeys.Count, properties);

    private void OverwriteColumnRow(int rowIndex, Dictionary<string, object> properties)
        => _columnMirror.OverwriteRow(rowIndex, _edgeKeys.Count, properties);

    private void SyncFromCompatibilityViewIfNeeded()
    {
        if (Data.Count <= _rowKeyIndex.Count)
        {
            foreach (var key in Data.Keys)
            {
                if (!_rowKeyIndex.ContainsKey(key))
                {
                    RebuildFromCompatibilityView();
                    return;
                }
            }
            return;
        }

        RebuildFromCompatibilityView();
    }

    private void RebuildFromCompatibilityView()
    {
        _rowKeyIndex.Clear();
        _columns.Clear();
        _columnStores.Clear();
        _rowVersions.Clear();
        _adjacencyIndex.Clear();

        foreach (var (key, props) in Data)
        {
            var rowIndex = _rowKeyIndex.Count;
            _rowKeyIndex.Add(key);
            _adjacencyIndex.Add(key, rowIndex);
            _rowVersions.Add(VersionInfo.AlwaysInsertedVersion);

            foreach (var col in _columns.Values)
                col.Add(null);

            foreach (var (propName, value) in props)
            {
                if (!_columns.TryGetValue(propName, out var col))
                {
                    col = new List<object?>(_rowKeyIndex.Count);
                    for (var i = 0; i < _rowKeyIndex.Count; i++)
                        col.Add(null);
                    _columns[propName] = col;
                }

                col[rowIndex] = value;
            }
            CommitColumnSnapshot(rowIndex, props, VersionInfo.AlwaysInsertedVersion);
        }
    }

    private static bool HaveEquivalentProperties(
        Dictionary<string, object> left,
        Dictionary<string, object> right)
    {
        if (left.Count != right.Count)
            return false;

        foreach (var (key, leftValue) in left)
        {
            if (!right.TryGetValue(key, out var rightValue) || !Equals(leftValue, rightValue))
                return false;
        }

        return true;
    }

    private void RefreshCommittedCompatibilityView()
    {
        Data.Clear();
        for (var rowIndex = 0; rowIndex < _rowKeyIndex.Count; rowIndex++)
        {
            if (!_rowVersions.IsCommittedVisible(rowIndex))
                continue;

            var key = _rowKeyIndex[rowIndex];
            var props = _propertyStore.BuildVisibleProperties(ulong.MaxValue, rowIndex);
            Data[key] = props;
        }
    }

    public void CommitVersionedChanges(Transaction.Transaction tx, ulong commitTS)
    {
        var insertedRows = new HashSet<int>();
        var rowsWithColumnUpdates = new HashSet<int>();

        for (var i = 0; i < _rowVersions.Count; i++)
        {
            if (_propertyStore.HasRowColumnVersion(i, tx.ID))
                rowsWithColumnUpdates.Add(i);
        }
        _rowVersions.Commit(tx, commitTS, insertedRows);

        foreach (var columnStore in _columnStores.Values)
            columnStore.CommitUpdates(tx, commitTS);

        var committedReader = new Transaction.Transaction(
            TransactionType.READ_ONLY,
            id: 1,
            startTS: commitTS);

        foreach (var rowIndex in rowsWithColumnUpdates)
        {
            var edgeKey = _rowKeyIndex[rowIndex];
            var props = _propertyStore.BuildVisibleProperties(committedReader, rowIndex);
            _compatibilityRows.ReplaceRow(rowIndex, props);
        }

        foreach (var rowIndex in insertedRows)
        {
            if (rowsWithColumnUpdates.Contains(rowIndex))
                continue;
            var props = _propertyStore.BuildVisibleProperties(committedReader, rowIndex);
            _propertyStore.CommitSnapshot(rowIndex, props, commitTS);
        }

        RefreshCommittedCompatibilityView();
    }

    public void CommitVersions(Transaction.Transaction tx, ulong commitTS)
        => CommitVersionedChanges(tx, commitTS);

    public void RollbackVersionedChanges(Transaction.Transaction tx)
    {
        _rowVersions.Rollback(tx);

        foreach (var columnStore in _columnStores.Values)
            columnStore.RollbackUpdates(tx);
        TrimRolledBackTail();
        RefreshCommittedCompatibilityView();
    }

    public void RollbackVersions(Transaction.Transaction tx)
        => RollbackVersionedChanges(tx);

    private Dictionary<string, object>? GetVisibleProperties(Transaction.Transaction tx, int rowIndex, EdgeKey key)
        => GetCommittedPropertiesVisibleTo(tx, rowIndex, key);

    private bool HasDeleteConflict(Transaction.Transaction tx, RowVersionState rowVersion)
        => rowVersion.DeleteVersion != VersionInfo.InvalidVersion &&
           rowVersion.DeleteVersion != tx.ID &&
           rowVersion.DeleteVersion > tx.StartTS;

    private void ThrowIfColumnWriteConflict(int rowIndex, Transaction.Transaction tx, string operation)
        => _propertyStore.ThrowIfWriteConflict(rowIndex, tx, operation);

    private void TrimRolledBackTail()
    {
        var newCount = _rowVersions.GetTrimmedRowCount();

        if (newCount == _rowVersions.Count)
            return;

        _compatibilityRows.TrimTail(newCount, (key, i) =>
        {
            _adjacencyIndex.Remove(key, i);
            _rowKeyIndex.Remove(key);
        });
        _rowVersions.RemoveRange(newCount, _rowVersions.Count - newCount);
    }

    private Dictionary<string, object>? GetCommittedPropertiesVisibleTo(
        Transaction.Transaction tx,
        int rowIndex,
        EdgeKey key)
    {
        if (_columnStores.Count > 0)
        {
            if (TryGetLatestCommittedSnapshotCommitTs(rowIndex, out var latestCommitTs) &&
                latestCommitTs != VersionInfo.AlwaysInsertedVersion &&
                latestCommitTs > tx.StartTS)
            {
                return _propertyStore.BuildVisibleProperties(tx.StartTS, rowIndex);
            }

            return _propertyStore.BuildVisibleProperties(tx, rowIndex);
        }

        return Data.TryGetValue(key, out var props) ? props : null;
    }

    private void CommitColumnSnapshot(int rowIndex, Dictionary<string, object> properties, ulong commitTs)
        => _propertyStore.CommitSnapshot(rowIndex, properties, commitTs);

    private bool TryGetLatestCommittedSnapshotCommitTs(int rowIndex, out ulong commitTs)
        => _propertyStore.TryGetLatestCommittedSnapshotCommitTs(rowIndex, out commitTs);

    private Dictionary<string, object> BuildVisibleProperties(Transaction.Transaction tx, int rowIndex)
        => _propertyStore.BuildVisibleProperties(tx, rowIndex);

    private Dictionary<string, object> BuildVisibleProperties(ulong visibleCommitVersion, int rowIndex)
        => _propertyStore.BuildVisibleProperties(visibleCommitVersion, rowIndex);

    private Column GetOrCreateColumnStore(string name)
    {
        if (_columnStores.TryGetValue(name, out var existing))
            return existing;

        var columnStore = _columnFactory?.CreateColumn(name, Math.Max(1024, _rowKeyIndex.Count))
            ?? new Column(name, Math.Max(1024, _rowKeyIndex.Count));
        for (var i = 0; i < _rowKeyIndex.Count; i++)
            columnStore.Append(null);
        _columnStores[name] = columnStore;
        return columnStore;
    }

    private bool HasRowColumnVersion(int rowIndex, ulong version)
        => _propertyStore.HasRowColumnVersion(rowIndex, version);

    internal bool AddProperty(string propertyName, object? defaultValue = null)
    {
        SyncFromCompatibilityViewIfNeeded();
        if (_columnStores.ContainsKey(propertyName))
            return false;

        var columnStore = _columnFactory?.CreateColumn(propertyName, Math.Max(1024, _rowKeyIndex.Count))
            ?? new Column(propertyName, Math.Max(1024, _rowKeyIndex.Count));
        var compatibilityColumn = new List<object?>(_rowKeyIndex.Count);
        for (var i = 0; i < _rowKeyIndex.Count; i++)
        {
            columnStore.Append(defaultValue);
            compatibilityColumn.Add(defaultValue);
        }

        _columnStores[propertyName] = columnStore;
        _columns[propertyName] = compatibilityColumn;
        if (defaultValue is not null)
        {
            foreach (var row in Data.Values)
                row[propertyName] = defaultValue;
        }
        return true;
    }

    internal bool DropProperty(string propertyName)
    {
        SyncFromCompatibilityViewIfNeeded();
        var removed = _columnStores.Remove(propertyName);
        removed = _columns.Remove(propertyName) || removed;
        if (!removed)
            return false;

        foreach (var row in Data.Values)
            row.Remove(propertyName);

        return true;
    }

    private bool TryFindVisibleRowIndex(Transaction.Transaction tx, EdgeKey key, out int rowIndex)
        => _rowKeyIndex.TryFindVisibleIndex(key, i => IsRowVisibleTo(tx, i), out rowIndex);

    private bool IsRowVisibleTo(Transaction.Transaction tx, int rowIndex)
        => _rowVersions.IsVisible(tx, rowIndex);

    private void RegisterInsertUndo(Transaction.Transaction tx, int rowIndex, EdgeKey key)
    {
        tx.TrackVersionedRowAction(Storage.UndoRecordType.INSERT_INFO, UndoReplayHandler, rowIndex);
    }

    private void RegisterRowUpdateUndo(
        Transaction.Transaction tx,
        int rowIndex,
        EdgeKey key,
        string? propertyName = null)
    {
        tx.TrackVersionedRowAction(Storage.UndoRecordType.UPDATE_INFO, UndoReplayHandler, rowIndex);
    }

    void IRowUndoReplayHost.ReplaceCommittedRow(int rowIndex, Dictionary<string, object> properties)
        => _compatibilityRows.ReplaceRow(rowIndex, properties);

    void IRowUndoReplayHost.TrimRolledBackTail()
        => TrimRolledBackTail();

    void IRowUndoReplayHost.PersistCommittedUpdate(int rowIndex, Dictionary<string, object> properties)
    {
        if (_graphLog is null || string.IsNullOrEmpty(_tableName) || rowIndex < 0 || rowIndex >= _rowKeyIndex.Count)
            return;

        var key = _rowKeyIndex[rowIndex];
        _graphLog.AppendRel(_tableName, key.From, key.To, properties);
    }

    void IRowUndoReplayHost.PersistCommittedDelete(int rowIndex)
    {
        if (_graphLog is null || string.IsNullOrEmpty(_tableName) || rowIndex < 0 || rowIndex >= _rowKeyIndex.Count)
            return;

        var key = _rowKeyIndex[rowIndex];
        _graphLog.AppendRelDelete(_tableName, key.From, key.To);
    }

}

internal record EdgeKey(object From, object To);
internal readonly record struct RelRowRef(string TableName, int RowIndex, EdgeKey EdgeKey);
public sealed record NodeRow(object Id, Dictionary<string, object> Properties, long Offset);

/// <summary>
/// Core database object. Owns the catalog, buffer manager, transaction manager,
/// storage layer, graph log/store, and now node property indexes.
/// </summary>
public class BogDatabase : IDisposable
{
    private readonly DatabaseMetricsRegistry _metricsRegistry = new();

    internal DatabaseMetricsRegistry MetricsRegistry => _metricsRegistry;

    public BogDatabaseMetricsSnapshot GetMetricsSnapshot()
        => _metricsRegistry.Snapshot();

    public void ResetMetrics()
        => _metricsRegistry.Reset();

    public BogDatabaseOptions Options { get; }
    public CatalogModel Catalog { get; }
    public Storage.BufferManager.BufferManager BufferManager { get; }
    public TransactionManager TransactionManager { get; }
    public Storage.StorageManager StorageManager { get; }
    internal Storage.GraphLogWriter GraphLog { get; }
    internal Storage.GraphStore GraphStore { get; }
    internal bool IsInMemory => _isInMemory;
    public bool IsReadOnly { get; }
    public string DatabasePath { get; }
    /// <summary>Registry of table functions registered by loaded extensions.</summary>
    public Extension.FunctionRegistry FunctionRegistry { get; }
    /// <summary>Registry of standalone table functions registered by loaded extensions.</summary>
    public Extension.StandaloneTableFunctionRegistry StandaloneTableFunctionRegistry { get; }
    /// <summary>Registry of scalar functions registered by loaded extensions.</summary>
    public Extension.ScalarFunctionRegistry ScalarFunctionRegistry { get; }
    public Extension.ExtensionManager ExtensionManager { get; }

    private readonly bool _isInMemory;
    private readonly ColumnFactory _columnFactory;
    private const string CatalogFileName = "catalog.bin";
    private const string GraphDataFileName = "graph-data.bin";
    private const string IndexDataFileName = "index-data.bin";
    private const string IndexDirectoryName = "indexes";
    private readonly Dictionary<string, VirtualFileSystem> _registeredFileSystems =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _registeredFileSystemOwners =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Extension.IStorageExtension> _registeredStorageExtensions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _registeredStorageExtensionOwners =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object> _registeredExtensionServices =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _registeredExtensionServiceOwners =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Extension.AttachedDatabaseHandle> _attachedDatabases =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Extension.ExtensionOption> _extensionOptions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object?> _extensionOptionValues =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _extensionOptionOwners =
        new(StringComparer.OrdinalIgnoreCase);
    private string? _currentExtensionOwner;

    internal Dictionary<string, NodeTableData> NodeTables { get; }
    internal Dictionary<string, RelTableData> RelTables { get; }

    // ── Hash Index Registry ───────────────────────────────────────────────────
    /// <summary>Per-table property indexes. Keyed by table name.</summary>
    internal Dictionary<string, NodePropertyIndex> NodeIndexes { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, VirtualFileSystem> RegisteredFileSystems => _registeredFileSystems;
    public IReadOnlyDictionary<string, Extension.IStorageExtension> RegisteredStorageExtensions => _registeredStorageExtensions;
    public IReadOnlyDictionary<string, Extension.AttachedDatabaseHandle> AttachedDatabases => _attachedDatabases;
    public IReadOnlyDictionary<string, Extension.ExtensionOption> ExtensionOptions => _extensionOptions;
    public IReadOnlyDictionary<string, object> RegisteredExtensionServices => _registeredExtensionServices;

    public IEnumerable<KeyValuePair<object, Dictionary<string, object>>> EnumerateVisibleNodeRows(
        string tableName,
        Transaction.Transaction? transaction = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        if (!NodeTables.TryGetValue(tableName, out var table))
            return [];

        return transaction is null
            ? table.EnumerateRows()
            : table.EnumerateRows(transaction);
    }

    public IEnumerable<NodeRow> EnumerateNodeRowsWithOffsets(
        string tableName,
        Transaction.Transaction? transaction = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        if (NodeTables.TryGetValue(tableName, out var table))
        {
            for (long offset = 0; offset < table.Count; offset++)
            {
                var found = transaction is null
                    ? table.TryGetByOffset(offset, out var nodeId, out var properties)
                    : table.TryGetByOffset(transaction, offset, out nodeId, out properties);
                if (!found || properties is null)
                    continue;

                yield return new NodeRow(nodeId, NormalizeNodePropertiesForRead(tableName, properties), offset);
            }

            yield break;
        }

        long persistedOffset = 0;
        foreach (var row in GraphStore.EnumerateNodes(tableName))
        {
            yield return new NodeRow(row.Key, NormalizeNodePropertiesForRead(tableName, row.Value), persistedOffset++);
        }
    }

    public bool TryGetNodeOffset(string tableName, object nodeId, out long offset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentNullException.ThrowIfNull(nodeId);

        if (NodeTables.TryGetValue(tableName, out var table))
            return table.TryGetOffset(nodeId, out offset);

        offset = 0;
        foreach (var row in GraphStore.EnumerateNodes(tableName))
        {
            if (StructuralValueComparer.AreEqual(row.Key, nodeId))
                return true;
            offset++;
        }

        offset = -1;
        return false;
    }

    public bool TryGetNodeRowByOffset(
        string tableName,
        long offset,
        Transaction.Transaction? transaction,
        out NodeRow? row)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        row = null;

        if (NodeTables.TryGetValue(tableName, out var table))
        {
            var found = transaction is null
                ? table.TryGetByOffset(offset, out var nodeId, out var properties)
                : table.TryGetByOffset(transaction, offset, out nodeId, out properties);
            if (!found || properties is null)
                return false;

            row = new NodeRow(nodeId, NormalizeNodePropertiesForRead(tableName, properties), offset);
            return true;
        }

        if (!GraphStore.TryGetNodeByOffset(tableName, offset, out var persistedNodeId, out var persistedProperties))
            return false;

        row = new NodeRow(persistedNodeId, NormalizeNodePropertiesForRead(tableName, persistedProperties), offset);
        return true;
    }

    public void RegisterFileSystem(string name, VirtualFileSystem fileSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(fileSystem);
        _registeredFileSystems[name] = fileSystem;
        if (!string.IsNullOrWhiteSpace(_currentExtensionOwner))
            _registeredFileSystemOwners[name] = _currentExtensionOwner;
        else
            _registeredFileSystemOwners.Remove(name);
    }

    public bool TryGetFileSystem(string name, out VirtualFileSystem fileSystem)
        => _registeredFileSystems.TryGetValue(name, out fileSystem!);

    public void RegisterStorageExtension(string name, Extension.IStorageExtension storageExtension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(storageExtension);
        _registeredStorageExtensions[name] = storageExtension;
        if (!string.IsNullOrWhiteSpace(_currentExtensionOwner))
            _registeredStorageExtensionOwners[name] = _currentExtensionOwner;
        else
            _registeredStorageExtensionOwners.Remove(name);
    }

    public bool TryGetStorageExtension(string name, out Extension.IStorageExtension storageExtension)
        => _registeredStorageExtensions.TryGetValue(name, out storageExtension!);

    public bool TryResolveStorageExtension(string dbType, out Extension.IStorageExtension storageExtension)
    {
        if (_registeredStorageExtensions.TryGetValue(dbType, out storageExtension!))
            return true;

        foreach (var extension in _registeredStorageExtensions.Values)
        {
            if (extension.CanHandle(dbType))
            {
                storageExtension = extension;
                return true;
            }
        }

        storageExtension = null!;
        return false;
    }

    public void RegisterExtensionService(string name, object service)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(service);
        _registeredExtensionServices[name] = service;
        if (!string.IsNullOrWhiteSpace(_currentExtensionOwner))
            _registeredExtensionServiceOwners[name] = _currentExtensionOwner;
        else
            _registeredExtensionServiceOwners.Remove(name);
    }

    public bool TryGetExtensionService<T>(string name, out T service) where T : class
    {
        if (_registeredExtensionServices.TryGetValue(name, out var raw) && raw is T typed)
        {
            service = typed;
            return true;
        }

        service = null!;
        return false;
    }

    public void AddAttachedDatabase(Extension.AttachedDatabaseHandle attachedDatabase)
    {
        ArgumentNullException.ThrowIfNull(attachedDatabase);
        if (_attachedDatabases.ContainsKey(attachedDatabase.Alias))
            throw new InvalidOperationException($"Attached database alias '{attachedDatabase.Alias}' already exists.");
        foreach (var table in attachedDatabase.Tables.Values)
        {
            var functionName = GetAttachedTableFunctionName(attachedDatabase.Alias, table.Name);
            if (StandaloneTableFunctionRegistry.Contains(functionName))
            {
                throw new InvalidOperationException(
                    $"Attached table function '{functionName}' already exists.");
            }
        }

        _attachedDatabases[attachedDatabase.Alias] = attachedDatabase;
        foreach (var table in attachedDatabase.Tables.Values)
        {
            StandaloneTableFunctionRegistry.Register(
                new Extension.AttachedTableFunction(this, attachedDatabase.Alias, table));
        }
    }

    public bool TryGetAttachedDatabase(string alias, out Extension.AttachedDatabaseHandle attachedDatabase)
        => _attachedDatabases.TryGetValue(alias, out attachedDatabase!);

    public static string GetAttachedTableFunctionName(string alias, string tableName)
        => $"{alias}.{tableName}";

    public bool RemoveAttachedDatabase(string alias)
    {
        if (!_attachedDatabases.TryGetValue(alias, out var attachedDatabase))
            return false;

        foreach (var table in attachedDatabase.Tables.Values)
            StandaloneTableFunctionRegistry.Unregister(GetAttachedTableFunctionName(alias, table.Name));

        return _attachedDatabases.Remove(alias);
    }

    public void AddExtensionOption(string name, LogicalTypeID type, object? defaultValue, bool isConfidential = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var option = new Extension.ExtensionOption(name, type, defaultValue, isConfidential);
        _extensionOptions[name] = option;
        _extensionOptionValues[name] = defaultValue;
        if (!string.IsNullOrWhiteSpace(_currentExtensionOwner))
            _extensionOptionOwners[name] = _currentExtensionOwner;
        else
            _extensionOptionOwners.Remove(name);
    }

    public bool TryGetExtensionOption(string name, out Extension.ExtensionOption option)
        => _extensionOptions.TryGetValue(name, out option!);

    public object? GetExtensionOptionValue(string name)
    {
        if (!_extensionOptionValues.TryGetValue(name, out var value))
            throw new KeyNotFoundException($"Extension option {name} not found.");
        return value;
    }

    public void SetExtensionOption(string name, object? value)
    {
        if (!_extensionOptions.ContainsKey(name))
            throw new KeyNotFoundException($"Extension option {name} not found.");
        _extensionOptionValues[name] = value;
    }

    public IEnumerable<KeyValuePair<object, Dictionary<string, object>>> EnumerateNodeRows(string tableName)
    {
        var orderedIds = new List<object>();
        var rowsById = new Dictionary<object, Dictionary<string, object>>();

        if (!_isInMemory)
        {
            foreach (var row in GraphStore.EnumerateNodes(tableName))
            {
                orderedIds.Add(row.Key);
                rowsById[row.Key] = NormalizeNodePropertiesForRead(tableName, row.Value);
            }
        }

        if (NodeTables.TryGetValue(tableName, out var table))
        {
            foreach (var row in table.EnumerateRows())
            {
                if (!rowsById.ContainsKey(row.Key))
                    orderedIds.Add(row.Key);
                rowsById[row.Key] = NormalizeNodePropertiesForRead(tableName, row.Value);
            }
        }

        foreach (var id in orderedIds)
        {
            if (rowsById.TryGetValue(id, out var row))
            {
                _metricsRegistry.AddNodeReads(1);
                yield return new KeyValuePair<object, Dictionary<string, object>>(id, row);
            }
        }
    }

    /// <summary>
    /// Returns an amortized O(1), best-effort fingerprint of node-write activity for
    /// <paramref name="tableName"/>, reflecting the rows currently staged in the in-memory
    /// (uncheckpointed) overlay. Derived indexes such as the vector HNSW graph capture this at
    /// build time and re-check it at query time to detect that live inserts may have made them
    /// stale, so they can fall back to an exact scan for correctness. This is a staleness *hint*,
    /// not an authoritative row count: it does not by itself observe in-place property updates,
    /// and a checkpoint that flushes the overlay may shift it. Rebuild an index to refresh it fully.
    /// </summary>
    public long GetNodeWriteFingerprint(string tableName)
        => NodeTables.TryGetValue(tableName, out var table) ? table.Count : 0L;

    internal Dictionary<string, object> NormalizeNodePropertiesForRead(
        string tableName,
        Dictionary<string, object> properties)
    {
        var entry = Catalog.GetTableCatalogEntry(null, tableName, useInternal: false);
        return BogDb.Core.Catalog.PropertyValueCoercion.CoerceProperties(entry, properties);
    }

    internal Dictionary<string, object> NormalizeRelPropertiesForRead(
        string tableName,
        Dictionary<string, object> properties)
    {
        var entry = Catalog.GetTableCatalogEntry(null, tableName, useInternal: false);
        return BogDb.Core.Catalog.PropertyValueCoercion.CoerceProperties(entry, properties);
    }

    internal IDisposable EnterExtensionRegistrationScope(string owner)
    {
        var previousOwner = _currentExtensionOwner;
        _currentExtensionOwner = owner;
        return new ExtensionRegistrationScope(this, previousOwner);
    }

    internal void RemoveExtensionRegistrations(string owner)
    {
        FunctionRegistry.UnregisterOwnedBy(owner);
        StandaloneTableFunctionRegistry.UnregisterOwnedBy(owner);
        ScalarFunctionRegistry.UnregisterOwnedBy(owner);

        foreach (var entry in _registeredFileSystemOwners.Where(kvp =>
                     string.Equals(kvp.Value, owner, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _registeredFileSystems.Remove(entry.Key);
            _registeredFileSystemOwners.Remove(entry.Key);
        }

        foreach (var entry in _registeredStorageExtensionOwners.Where(kvp =>
                     string.Equals(kvp.Value, owner, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _registeredStorageExtensions.Remove(entry.Key);
            _registeredStorageExtensionOwners.Remove(entry.Key);
        }

        foreach (var entry in _registeredExtensionServiceOwners.Where(kvp =>
                     string.Equals(kvp.Value, owner, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _registeredExtensionServices.Remove(entry.Key);
            _registeredExtensionServiceOwners.Remove(entry.Key);
        }

        foreach (var entry in _attachedDatabases.Where(kvp =>
                     string.Equals(kvp.Value.ExtensionName, owner, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            RemoveAttachedDatabase(entry.Key);
        }

        foreach (var entry in _extensionOptionOwners.Where(kvp =>
                     string.Equals(kvp.Value, owner, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _extensionOptions.Remove(entry.Key);
            _extensionOptionValues.Remove(entry.Key);
            _extensionOptionOwners.Remove(entry.Key);
        }
    }

    /// <summary>Get (or create) the NodePropertyIndex for a given table.</summary>
    internal NodePropertyIndex GetNodePropertyIndex(string tableName)
    {
        if (!NodeIndexes.TryGetValue(tableName, out var idx))
        {
            idx = new NodePropertyIndex();
            NodeIndexes[tableName] = idx;
        }
        return idx;
    }

    /// <summary>
    /// Look up a key in the named table's property index.
    /// Returns true and sets <paramref name="nodeOffset"/> if found.
    /// </summary>
    internal bool TryIndexLookup(string tableName, string propertyName, object key, out long nodeOffset)
    {
        _metricsRegistry.AddIndexLookups(1);
        nodeOffset = -1;
        if (!NodeIndexes.TryGetValue(tableName, out var idx) ||
            !idx.TryLookupAll(propertyName, key, out var nodeOffsets))
        {
            return false;
        }

        if (NodeTables.TryGetValue(tableName, out var table))
        {
            for (var i = nodeOffsets.Count - 1; i >= 0; i--)
            {
                if (!table.TryGetByOffset(nodeOffsets[i], out _, out var props) || props is null)
                    continue;

                var normalizedProps = NormalizeNodePropertiesForRead(tableName, props);
                if (!normalizedProps.TryGetValue(propertyName, out var propertyValue) ||
                    !StructuralValueComparer.AreEqual(propertyValue, key))
                {
                    continue;
                }

                nodeOffset = nodeOffsets[i];
                return true;
            }
        }
        else
        {
            for (var i = nodeOffsets.Count - 1; i >= 0; i--)
            {
                if (!GraphStore.TryGetNodeByOffset(tableName, nodeOffsets[i], out _, out var props) || props is null)
                    continue;

                var normalizedProps = NormalizeNodePropertiesForRead(tableName, props);
                if (!normalizedProps.TryGetValue(propertyName, out var propertyValue) ||
                    !StructuralValueComparer.AreEqual(propertyValue, key))
                {
                    continue;
                }

                nodeOffset = nodeOffsets[i];
                return true;
            }
        }

        return false;
    }

    private readonly List<BogDb.Core.Extension.INodeMutationListener> _nodeMutationListeners = new();

    /// <summary>
    /// Registers a listener notified of node upserts/deletes so extensions can incrementally maintain
    /// derived indexes (vector, FTS). Notifications fire inside write transactions, before commit; see
    /// <see cref="BogDb.Core.Extension.INodeMutationListener"/> for the commit-deferral contract.
    /// </summary>
    public void RegisterNodeMutationListener(BogDb.Core.Extension.INodeMutationListener listener)
    {
        if (listener != null && !_nodeMutationListeners.Contains(listener))
            _nodeMutationListeners.Add(listener);
    }

    internal void NotifyNodeMutation(
        Transaction.Transaction tx,
        string tableName,
        object id,
        BogDb.Core.Extension.NodeMutationKind kind,
        IReadOnlyDictionary<string, object>? properties)
    {
        // Only fire for live write transactions with registered listeners — recovery/checkpoint/dummy
        // transactions are excluded (indexes are rebuilt on open), and the common no-listener case is free.
        if (_nodeMutationListeners.Count == 0 || tx == null || !tx.IsWriteTransaction())
            return;

        foreach (var listener in _nodeMutationListeners)
            listener.OnNodeMutation(tx, tableName, id, kind, properties);
    }

    /// <summary>
    /// Update all registered property indexes for a node that was just written.
    /// Called by PhysicalInsert and BogConnection insert paths after writing to NodeTables.
    /// Only indexed properties are touched — mirrors UpdateIndexesForNode in BogConnection.
    /// </summary>
    internal void UpdateNodeIndexes(string tableName, Transaction.Transaction tx, object id, Dictionary<string, object> props, NodeTableData table)
    {
        NotifyNodeMutation(tx, tableName, id, BogDb.Core.Extension.NodeMutationKind.Upsert, props);

        if (!NodeIndexes.TryGetValue(tableName, out var nodeIdx) || !nodeIdx.HasAnyIndex)
            return;

        if (!table.TryGetOffset(id, out var offset))
            return;

        var normalizedProps = NormalizeNodePropertiesForRead(tableName, props);

        foreach (var propertyName in nodeIdx.IndexedProperties)
        {
            if (normalizedProps.TryGetValue(propertyName, out var value) && value is not null)
                nodeIdx.Put(propertyName, value, offset);
        }
    }

    internal void RefreshNodeIndexesForVisibleRow(
        string tableName,
        Transaction.Transaction tx,
        object id,
        NodeTableData table)
    {
        // Refresh if there is a core property index OR an extension mutation listener to notify — an
        // update on a table with only a vector/FTS index must still reach UpdateNodeIndexes (which fires
        // the Upsert notification before its own core-index guard).
        var hasCoreIndex = NodeIndexes.TryGetValue(tableName, out var nodeIdx) && nodeIdx.HasAnyIndex;
        if (!hasCoreIndex && _nodeMutationListeners.Count == 0)
            return;

        if (!table.TryGetProperties(tx, id, out var props) || props is null)
            return;

        UpdateNodeIndexes(tableName, tx, id, props, table);
    }

    /// <summary>
    /// Remove the node's current indexed property values from all property indexes
    /// for the given table. Must be called BEFORE the row is deleted from storage.
    /// </summary>
    internal void RemoveNodeFromIndexes(
        string tableName,
        Transaction.Transaction tx,
        object id,
        NodeTableData table)
    {
        NotifyNodeMutation(tx, tableName, id, BogDb.Core.Extension.NodeMutationKind.Delete, null);

        if (!NodeIndexes.TryGetValue(tableName, out var nodeIdx) || !nodeIdx.HasAnyIndex)
            return;

        if (!table.TryGetProperties(tx, id, out var props) || props is null)
            return;

        if (!table.TryGetOffset(id, out var offset))
            return;

        var normalizedProps = NormalizeNodePropertiesForRead(tableName, props);

        foreach (var propertyName in nodeIdx.IndexedProperties)
        {
            if (normalizedProps.TryGetValue(propertyName, out var value) && value is not null)
                nodeIdx.Remove(propertyName, value, offset);
        }
    }

    internal void DropTable(Transaction.Transaction transaction, string tableName)
    {
        var entry = Catalog.GetTableCatalogEntry(transaction, tableName)
            ?? throw new KeyNotFoundException($"Table {tableName} not found.");

        if (entry is BogDb.Core.Catalog.NodeTableCatalogEntry)
        {
            foreach (var relEntry in Catalog.GetRelTableEntries())
            {
                if (relEntry is not BogDb.Core.Catalog.RelGroupCatalogEntry relGroupEntry)
                    continue;
                if (relGroupEntry.ReferencesNodeTable(tableName))
                {
                    throw new InvalidOperationException(
                        $"Cannot drop node table '{tableName}' while relationship table '{relEntry.Name}' still references it.");
                }
            }

            NodeTables.Remove(tableName);
            NodeIndexes.Remove(tableName);
        }
        else if (entry is BogDb.Core.Catalog.RelGroupCatalogEntry)
        {
            RelTables.Remove(tableName);
        }

        StorageManager.GetWAL().LogDropTableRecord(checked((uint)entry.TableID));
        Catalog.DropIndexEntriesForTable(transaction, tableName);
        Catalog.DropTableEntry(transaction, tableName);
    }

    internal void AlterTableAddProperty(
        Transaction.Transaction transaction,
        string tableName,
        string propertyName,
        LogicalTypeID type,
        string? declaredType = null,
        object? defaultValue = null,
        string defaultExpressionName = "")
    {
        var entry = Catalog.GetTableCatalogEntry(transaction, tableName)
            ?? throw new KeyNotFoundException($"Table {tableName} not found.");

        if (entry.ContainsProperty(propertyName))
            return;

        entry.AddProperty(new BogDb.Core.Catalog.PropertyDefinition(
            new BogDb.Core.Catalog.ColumnDefinition(propertyName, type, declaredType),
            defaultExpressionName));
        Catalog.IncrementVersion();

        if (entry is BogDb.Core.Catalog.NodeTableCatalogEntry)
            NodeTables[tableName].AddProperty(propertyName, defaultValue);
        else if (entry is BogDb.Core.Catalog.RelGroupCatalogEntry)
            RelTables[tableName].AddProperty(propertyName, defaultValue);
    }

    internal void AlterTableDropProperty(Transaction.Transaction transaction, string tableName, string propertyName)
    {
        var entry = Catalog.GetTableCatalogEntry(transaction, tableName)
            ?? throw new KeyNotFoundException($"Table {tableName} not found.");

        if (!entry.ContainsProperty(propertyName))
            return;

        if (Catalog.ContainsIndexEntry(tableName, propertyName))
        {
            Catalog.DropIndexEntry(transaction, tableName, propertyName);
            if (NodeIndexes.TryGetValue(tableName, out var index))
                index.DropIndex(propertyName);
        }

        entry.DropProperty(propertyName);
        Catalog.IncrementVersion();

        if (entry is BogDb.Core.Catalog.NodeTableCatalogEntry)
            NodeTables[tableName].DropProperty(propertyName);
        else if (entry is BogDb.Core.Catalog.RelGroupCatalogEntry)
            RelTables[tableName].DropProperty(propertyName);
    }

    internal void AlterTableRename(Transaction.Transaction transaction, string tableName, string newTableName)
    {
        var entry = Catalog.GetTableCatalogEntry(transaction, tableName)
            ?? throw new KeyNotFoundException($"Table {tableName} not found.");
        if (Catalog.ContainsTable(transaction, newTableName))
            throw new InvalidOperationException($"Table {newTableName} already exists.");

        Catalog.RenameTableEntry(transaction, tableName, newTableName);
        Catalog.RenameIndexEntriesForTable(transaction, tableName, newTableName);

        if (entry is BogDb.Core.Catalog.NodeTableCatalogEntry)
        {
            var table = NodeTables[tableName];
            NodeTables.Remove(tableName);
            NodeTables[newTableName] = table;
            BindTablePersistenceSurface(newTableName, table);

            if (NodeIndexes.TryGetValue(tableName, out var index))
            {
                NodeIndexes.Remove(tableName);
                NodeIndexes[newTableName] = index;
            }

            foreach (var relEntryBase in Catalog.GetRelTableEntries())
            {
                var relEntry = (BogDb.Core.Catalog.RelGroupCatalogEntry)relEntryBase;
                relEntry.RenameEndpointTable(tableName, newTableName);
                if (RelTables.TryGetValue(relEntry.Name, out var relTable))
                    relTable.SetEndpointTables(relEntry.SrcTableName, relEntry.DstTableName);
            }
        }
        else if (entry is BogDb.Core.Catalog.RelGroupCatalogEntry)
        {
            var table = RelTables[tableName];
            RelTables.Remove(tableName);
            RelTables[newTableName] = table;
            BindTablePersistenceSurface(newTableName, table);
        }
    }

    internal void AlterTableRenameProperty(Transaction.Transaction transaction, string tableName, string propertyName, string newPropertyName)
    {
        var entry = Catalog.GetTableCatalogEntry(transaction, tableName)
            ?? throw new KeyNotFoundException($"Table {tableName} not found.");

        if (!entry.ContainsProperty(propertyName))
            throw new KeyNotFoundException($"Property {propertyName} not found.");
        if (entry.ContainsProperty(newPropertyName))
            throw new InvalidOperationException($"Property {newPropertyName} already exists.");

        var hadIndex = Catalog.ContainsIndexEntry(tableName, propertyName);
        if (hadIndex)
            Catalog.RenameIndexEntry(transaction, tableName, propertyName, tableName, newPropertyName);

        entry.RenameProperty(propertyName, newPropertyName);
        Catalog.IncrementVersion();

        if (entry is BogDb.Core.Catalog.NodeTableCatalogEntry)
        {
            NodeTables[tableName].RenameProperty(propertyName, newPropertyName);
            if (NodeIndexes.TryGetValue(tableName, out var index))
                index.RenameIndex(propertyName, newPropertyName);
        }
        else if (entry is BogDb.Core.Catalog.RelGroupCatalogEntry)
        {
            RelTables[tableName].RenameProperty(propertyName, newPropertyName);
        }
    }

    internal void AlterTableConnectionChange(
        Transaction.Transaction transaction,
        string tableName,
        bool isAdd,
        bool ignoreIfPresentOrMissing,
        string fromTableName,
        string toTableName)
    {
        var entry = Catalog.GetTableCatalogEntry(transaction, tableName)
            ?? throw new KeyNotFoundException($"Table {tableName} not found.");
        if (entry is not BogDb.Core.Catalog.RelGroupCatalogEntry relEntry)
            throw new InvalidOperationException($"ALTER TABLE ... {(isAdd ? "ADD" : "DROP")} FROM/TO is only supported for relationship tables.");

        EnsureNodeTableExists(transaction, fromTableName);
        EnsureNodeTableExists(transaction, toTableName);

        var hasConnection = relEntry.ContainsConnection(fromTableName, toTableName);
        if (isAdd)
        {
            if (hasConnection)
            {
                if (ignoreIfPresentOrMissing)
                    return;
                throw new InvalidOperationException($"{fromTableName}->{toTableName} already exists in {tableName} table.");
            }

            relEntry.AddConnection(fromTableName, toTableName);
        }
        else
        {
            if (!hasConnection)
            {
                if (ignoreIfPresentOrMissing)
                    return;
                throw new InvalidOperationException($"{fromTableName}->{toTableName} does not exist in {tableName} table.");
            }

            if (RelTables.TryGetValue(tableName, out var relTable))
            {
                foreach (var edge in new List<EdgeKey>(GetMatchingConnectionEdges(relTable, fromTableName, toTableName)))
                    relTable.Remove(transaction, edge);
            }

            relEntry.DropConnection(fromTableName, toTableName);
        }

        Catalog.IncrementVersion();

        if (RelTables.TryGetValue(tableName, out var table))
            SyncRelTableEndpoints(table, relEntry);
    }

    /// <summary>
    /// Create an index on <paramref name="propertyName"/> for <paramref name="tableName"/>
    /// and (re)build it from the current node data (in-memory or GraphStore).
    /// For file-backed databases, uses a DiskBackedNodeIndex with a per-property .kzix file.
    /// </summary>
    public void CreateIndex(string tableName, string propertyName)
    {
        var propIdx = GetNodePropertyIndex(tableName);

        if (!_isInMemory)
        {
            var indexPath = GetIndexFilePath(tableName, propertyName);
            propIdx.CreateDiskBackedIndex(propertyName, indexPath);
        }
        else
        {
            propIdx.CreateIndex(propertyName);
        }

        LogicalTypeID? propertyType = null;
        if (Catalog.GetTableCatalogEntry(null, tableName) is BogDb.Core.Catalog.TableCatalogEntry tableEntry &&
            tableEntry.ContainsProperty(propertyName))
        {
            propertyType = tableEntry.GetProperty(propertyName).Type;
        }

        Catalog.CreateIndexEntry(tableName, propertyName, propertyType: propertyType);
        propIdx.Rebuild(propertyName, EnumerateNodeRows(tableName));

        if (!_isInMemory)
            PersistState();
    }

    /// <summary>
    /// Returns the path for a per-property disk-backed index file.
    /// Layout: {DatabasePath}/indexes/{tableName}.{propertyName}.kzix
    /// </summary>
    private string GetIndexFilePath(string tableName, string propertyName)
    {
        var indexDir = Path.Combine(DatabasePath, IndexDirectoryName);
        return Path.Combine(indexDir, $"{tableName}.{propertyName}.kzix");
    }

    private void EnsureNodeTableExists(Transaction.Transaction transaction, string tableName)
    {
        var entry = Catalog.GetTableCatalogEntry(transaction, tableName)
            ?? throw new InvalidOperationException($"Table {tableName} does not exist.");
        if (entry is not BogDb.Core.Catalog.NodeTableCatalogEntry)
            throw new InvalidOperationException($"Table {tableName} is not a node table.");
    }

    private List<EdgeKey> GetMatchingConnectionEdges(RelTableData relTable, string fromTableName, string toTableName)
    {
        var matches = new List<EdgeKey>();
        if (!NodeTables.TryGetValue(fromTableName, out var fromTable) ||
            !NodeTables.TryGetValue(toTableName, out var toTable))
        {
            return matches;
        }

        foreach (var (edgeKey, _) in relTable.EnumerateRows())
        {
            if (!fromTable.TryGetOffset(edgeKey.From, out _))
                continue;
            if (!toTable.TryGetOffset(edgeKey.To, out _))
                continue;
            matches.Add(edgeKey);
        }

        return matches;
    }

    private static void SyncRelTableEndpoints(RelTableData relTable, BogDb.Core.Catalog.RelGroupCatalogEntry relEntry)
    {
        var connections = relEntry.GetConnections();
        if (connections.Count == 0)
        {
            relTable.ClearEndpointTables();
            return;
        }

        relTable.SetEndpointTables(connections[0].SrcTableName, connections[0].DstTableName);
    }

    private BogDatabase(
        string path,
        BogDatabaseOptions? options = null,
        CatalogModel? catalog = null,
        Dictionary<string, NodeTableData>? nodeTables = null,
        Dictionary<string, RelTableData>? relTables = null,
        Storage.DatabaseLockManager? preAcquiredLockManager = null)
    {
        DatabasePath = path;
        Options = (options ?? new BogDatabaseOptions()).Clone();
        _isInMemory = string.Equals(path, ":memory:", StringComparison.OrdinalIgnoreCase);
        IsReadOnly = Options.ReadOnly;
        Catalog = catalog ?? new CatalogModel();
        NodeTables = nodeTables ?? new Dictionary<string, NodeTableData>();
        RelTables = relTables ?? new Dictionary<string, RelTableData>();
        FunctionRegistry = new Extension.FunctionRegistry(() => _currentExtensionOwner);
        StandaloneTableFunctionRegistry = new Extension.StandaloneTableFunctionRegistry(() => _currentExtensionOwner);
        ScalarFunctionRegistry = new Extension.ScalarFunctionRegistry(() => _currentExtensionOwner);
        ExtensionManager = new Extension.ExtensionManager(this);
        StorageManager = new Storage.StorageManager(path, IsReadOnly, preAcquiredLockManager);
        BufferManager = new Storage.BufferManager.BufferManager(
            Options.BufferPoolSizeBytes,
            Options.MaxMappedDatabaseSizeBytes,
            StorageManager.GetWAL());
        TransactionManager = new TransactionManager(StorageManager);
        GraphLog = new Storage.GraphLogWriter(path, StorageManager.GetWAL(), _isInMemory, IsReadOnly);
        GraphStore = new Storage.GraphStore(path, _isInMemory);
        _columnFactory = new Storage.Table.ColumnFactory(BufferManager, path, _isInMemory);
        BindTablePersistenceSurfaces();
        RegisterCatalogTableFunctions();
    }

    /// <summary>
    /// Registers built-in catalog introspection table functions (C++ parity).
    /// </summary>
    private void RegisterCatalogTableFunctions()
    {
        StandaloneTableFunctionRegistry.Register(new Function.ShowTablesTableFunction());
        StandaloneTableFunctionRegistry.Register(new Function.TableInfoTableFunction());
        StandaloneTableFunctionRegistry.Register(new Function.CreateIndexTableFunction(this));
        StandaloneTableFunctionRegistry.Register(new Function.ShowFunctionsTableFunction());
        StandaloneTableFunctionRegistry.Register(new Function.ShowIndexesTableFunction());
        StandaloneTableFunctionRegistry.Register(new Function.ShowSequencesTableFunction());
        StandaloneTableFunctionRegistry.Register(new Function.ShowMacrosTableFunction());
        StandaloneTableFunctionRegistry.Register(new Function.ShowAttachedDatabasesTableFunction());
        StandaloneTableFunctionRegistry.Register(new Function.ShowLoadedExtensionsTableFunction());
        StandaloneTableFunctionRegistry.Register(new Function.ClearWarningsTableFunction());
        StandaloneTableFunctionRegistry.Register(new Function.ShowWarningsTableFunction());
    }

    /// <summary>
    /// Opens or creates a BogDbDB instance at the specified path.
    /// </summary>
    public static BogDatabase Open(string path, BogDatabaseOptions? options = null)
    {
        var effectiveOptions = (options ?? new BogDatabaseOptions()).Clone();

        if (string.Equals(path, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return new BogDatabase(path, effectiveOptions);
        }

        if (effectiveOptions.ReadOnly)
        {
            if (!Directory.Exists(path))
                throw new DirectoryNotFoundException($"Database directory '{path}' does not exist.");

            var catalog = LoadCatalog(path) ?? new CatalogModel();
            var (nodeTables, relTables) = LoadGraphData(path);

            if (effectiveOptions.ReadCommittedRecoveryState)
            {
                var walReplayer = new WALReplayer(path);
                var replayedWalRecords = walReplayer.ReadCommittedRecordsWithoutTruncation();
                ApplyRecoveredCatalogRecords(catalog, nodeTables, relTables, replayedWalRecords);

                var maxCommittedGraphLogOffset = GetMaxCommittedGraphLogOffset(replayedWalRecords);
                if (maxCommittedGraphLogOffset > 0)
                {
                    Storage.GraphLogReader.ApplyLog(path, nodeTables, relTables, maxCommittedGraphLogOffset);
                }
            }

            BackfillRelTableMetadata(catalog, relTables);
            var database = new BogDatabase(path, effectiveOptions, catalog, nodeTables, relTables);
            database.NormalizeLoadedValuesFromCatalog();
            if (!database.TryLoadNodeIndexesFromSnapshot())
                database.RebuildNodeIndexesFromCatalog();
            return database;
        }

        Directory.CreateDirectory(path);
        var lockManager = Storage.DatabaseLockManager.Acquire(path);

        try
        {
            // Ensure WAL is replayed/truncated before loading persisted metadata.
            var walReplayer = new WALReplayer(path);
            var replayedWalRecords = walReplayer.Replay();
            var shouldApplyGraphLog = ShouldApplyGraphLogFromRecords(path, replayedWalRecords);
            SanitizeGraphLogFromRecords(path, shouldApplyGraphLog, replayedWalRecords);
            var catalog = LoadCatalog(path) ?? new CatalogModel();
            var (nodeTables, relTables) = LoadGraphData(path);

            ApplyRecoveredCatalogRecords(catalog, nodeTables, relTables, replayedWalRecords);

            if (shouldApplyGraphLog)
            {
                Storage.GraphLogReader.ApplyLog(path, nodeTables, relTables);
            }
            BackfillRelTableMetadata(catalog, relTables);
            var database = new BogDatabase(path, effectiveOptions, catalog, nodeTables, relTables, lockManager);
            lockManager = null;
            database.NormalizeLoadedValuesFromCatalog();
            if (!database.TryLoadNodeIndexesFromSnapshot())
                database.RebuildNodeIndexesFromCatalog();
            return database;
        }
        catch
        {
            lockManager?.Dispose();
            throw;
        }
    }

    private static void ApplyRecoveredCatalogRecords(
        CatalogModel catalog,
        Dictionary<string, NodeTableData> nodeTables,
        Dictionary<string, RelTableData> relTables,
        IReadOnlyList<WALRecordBase> replayedWalRecords)
    {
        foreach (var record in replayedWalRecords)
        {
            if (record is Transaction.CreateCatalogEntryWALRecord createRecord && createRecord.SerializedEntry.Length > 0)
            {
                using var ms = new System.IO.MemoryStream(createRecord.SerializedEntry);
                using var br = new System.IO.BinaryReader(ms);
                var entry = BogDb.Core.Catalog.CatalogEntry.Deserialize(br);
                if (!catalog.ContainsTable(null, entry.Name))
                {
                    if (entry is not BogDb.Core.Catalog.TableCatalogEntry tableEntry)
                        continue;

                    catalog.CreateTableEntry(Transaction.Transaction.DUMMY_TRANSACTION, tableEntry);
                    if (entry is BogDb.Core.Catalog.NodeTableCatalogEntry)
                        nodeTables[entry.Name] = new NodeTableData();
                    else if (entry is BogDb.Core.Catalog.RelGroupCatalogEntry relGroup)
                        relTables[entry.Name] = new RelTableData(relGroup.SrcTableName, relGroup.DstTableName);
                }
            }
            else if (record is Transaction.DropCatalogEntryWALRecord dropRecord)
            {
                ApplyDroppedTableRecord(catalog, nodeTables, relTables, dropRecord.EntryId);
            }
        }
    }

    private static long GetMaxCommittedGraphLogOffset(IReadOnlyList<WALRecordBase> replayedWalRecords)
    {
        long maxCommittedOffset = -1;
        foreach (var record in replayedWalRecords)
        {
            if (record is Transaction.CommitWALRecord commitRecord &&
                commitRecord.GraphLogCommittedOffset > maxCommittedOffset)
            {
                maxCommittedOffset = commitRecord.GraphLogCommittedOffset;
            }
        }

        return maxCommittedOffset;
    }

    /// <summary>Alias for Open in embedded/in-process mode.</summary>
    public static BogDatabase CreateInMemory(BogDatabaseOptions? options = null) => Open(":memory:", options);

    public GraphShardExtractor CreateGraphShardExtractor() => new(this);

    public GraphShard ExtractGraphNeighborhood(
        string seedTable,
        object seedNodeId,
        GraphShardExtractionOptions? options = null,
        Transaction.Transaction? tx = null)
        => CreateGraphShardExtractor().ExtractNeighborhood(seedTable, seedNodeId, options, tx);

    public GraphShard ExtractGraphNeighborhood(
        IEnumerable<GraphNodeSelector> seeds,
        GraphShardExtractionOptions? options = null,
        Transaction.Transaction? tx = null)
        => CreateGraphShardExtractor().ExtractNeighborhood(seeds, options, tx);

    public GraphShard ExtractGraphNodeSet(
        IEnumerable<GraphNodeSelector> nodes,
        GraphShardExtractionOptions? options = null,
        Transaction.Transaction? tx = null)
        => CreateGraphShardExtractor().ExtractNodeSet(nodes, options, tx);

    public string ExportGraphNeighborhoodAsJson(
        string seedTable,
        object seedNodeId,
        GraphShardExtractionOptions? options = null,
        bool writeIndented = false,
        Transaction.Transaction? tx = null)
        => GraphShardJson.Serialize(
            ExtractGraphNeighborhood(seedTable, seedNodeId, options, tx),
            writeIndented);

    public string ExportGraphNeighborhoodAsJson(
        IEnumerable<GraphNodeSelector> seeds,
        GraphShardExtractionOptions? options = null,
        bool writeIndented = false,
        Transaction.Transaction? tx = null)
        => GraphShardJson.Serialize(
            ExtractGraphNeighborhood(seeds, options, tx),
            writeIndented);

    public string ExportGraphNodeSetAsJson(
        IEnumerable<GraphNodeSelector> nodes,
        GraphShardExtractionOptions? options = null,
        bool writeIndented = false,
        Transaction.Transaction? tx = null)
        => GraphShardJson.Serialize(
            ExtractGraphNodeSet(nodes, options, tx),
            writeIndented);

    public void Dispose()
    {
        PersistState();
        ExtensionManager.Dispose();
        GraphLog.Dispose();
        // Dispose column factory before StorageManager — releases references
        // to PageBackedColumn instances so their FileHandles can be flushed.
        _columnFactory?.Dispose();
        // Release all storage file handles (data.kz, data.wal) AFTER flushing,
        // so the next Open() can read/replay them without access errors.
        StorageManager.Dispose();
        Catalog.Dispose();
        BufferManager.Dispose();
    }

    public void Checkpoint()
        => PersistState();

    private sealed class ExtensionRegistrationScope : IDisposable
    {
        private readonly BogDatabase _database;
        private readonly string? _previousOwner;
        private bool _disposed;

        public ExtensionRegistrationScope(BogDatabase database, string? previousOwner)
        {
            _database = database;
            _previousOwner = previousOwner;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _database._currentExtensionOwner = _previousOwner;
            _disposed = true;
        }
    }

    internal void PersistState()
    {
        if (_isInMemory || IsReadOnly) return;

        Directory.CreateDirectory(DatabasePath);

        var catalogPath = Path.Combine(DatabasePath, CatalogFileName);
        using (var stream = new FileStream(catalogPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(stream))
        {
            Catalog.Serialize(writer);
        }

        // Flush all page-backed columns to their .kz files and write the manifest
        _columnFactory?.CheckpointAll();

        var graphDataPath = Path.Combine(DatabasePath, GraphDataFileName);
        ColumnarTableSerializer.WriteSnapshot(graphDataPath, NodeTables, RelTables);
        var indexDataPath = Path.Combine(DatabasePath, IndexDataFileName);
        IndexSnapshotSerializer.Write(indexDataPath, NodeIndexes, NodeTables);

        foreach (var participant in RegisteredExtensionServices.Values.OfType<Extension.IDatabasePersistenceParticipant>())
            participant.Persist(this);

        // Checkpoint any disk-backed property indexes that have dirty data
        foreach (var nodeIdx in NodeIndexes.Values)
            nodeIdx.CheckpointDiskIndexes();

        GraphLog.Clear();
        StorageManager.GetWAL().Clear();
    }

    internal void DiscardPendingRecoveryArtifacts(Transaction.Transaction transaction)
    {
        if (_isInMemory || IsReadOnly)
            return;

        ArgumentNullException.ThrowIfNull(transaction);

        GraphLog.Truncate(transaction.RecoveryGraphLogOffset);
        StorageManager.GetWAL().Truncate(transaction.RecoveryWalOffset);
    }

    private static CatalogModel? LoadCatalog(string databasePath)
    {
        var catalogPath = Path.Combine(databasePath, CatalogFileName);
        if (!File.Exists(catalogPath)) return null;

        using var stream = new FileStream(catalogPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);
        return CatalogModel.Deserialize(reader);
    }

    private static (Dictionary<string, NodeTableData> nodeTables, Dictionary<string, RelTableData> relTables) LoadGraphData(string databasePath)
    {
        var nodeTables = new Dictionary<string, NodeTableData>();
        var relTables  = new Dictionary<string, RelTableData>();
        var graphDataPath = Path.Combine(databasePath, GraphDataFileName);
        if (!File.Exists(graphDataPath))
            return (nodeTables, relTables);

        _ = ColumnarTableSerializer.TryReadSnapshot(graphDataPath, nodeTables, relTables);

        return (nodeTables, relTables);
    }

    private static bool ShouldApplyGraphLogFromRecords(string databasePath, IReadOnlyList<WALRecordBase> replayedWalRecords)
    {
        var graphLogPath = Path.Combine(databasePath, "graph-log.bin");
        if (!File.Exists(graphLogPath))
            return false;

        // Apply graph-log whenever it has content — the log's own commit
        // framing handles distinguishing committed vs uncommitted data.
        var graphLogInfo = new System.IO.FileInfo(graphLogPath);
        if (graphLogInfo.Length > 0)
            return true;

        var graphDataPath = Path.Combine(databasePath, GraphDataFileName);
        if (!File.Exists(graphDataPath))
            return true;

        return false;
    }

    private static void SanitizeGraphLogFromRecords(
        string databasePath,
        bool shouldApplyGraphLog,
        IReadOnlyList<WALRecordBase> replayedWalRecords)
    {
        var graphLogPath = Path.Combine(databasePath, "graph-log.bin");
        if (!File.Exists(graphLogPath))
            return;

        if (!shouldApplyGraphLog)
        {
            using var clearStream = new FileStream(graphLogPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            clearStream.SetLength(0);
            clearStream.Flush(true);
            return;
        }

        // Truncate graph-log to the last committed offset from the WAL commit records.
        // This removes any uncommitted tail that was being built during a crash.
        long maxCommittedOffset = -1;
        foreach (var record in replayedWalRecords)
        {
            if (record is Transaction.CommitWALRecord commitRecord)
            {
                if (commitRecord.GraphLogCommittedOffset > maxCommittedOffset)
                    maxCommittedOffset = commitRecord.GraphLogCommittedOffset;
            }
        }

        // If we found committed records, truncate to the max committed offset.
        // If no commits were found (maxCommittedOffset == -1), the graph-log
        // data is entirely uncommitted — truncate to 0.
        var targetLength = maxCommittedOffset >= 0 ? maxCommittedOffset : 0;
        var currentLength = new System.IO.FileInfo(graphLogPath).Length;
        if (currentLength > targetLength)
        {
            using var fs = new FileStream(graphLogPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            fs.SetLength(targetLength);
            fs.Flush(true);
        }
    }

    /// <summary>
    /// Lookup a table name by its internal table ID for WAL recovery.
    /// </summary>
    public string? GetTableNameById(uint tableId)
    {
        var entry = Catalog.GetTableCatalogEntryByOID(tableId);
        return entry?.Name;
    }

    /// <summary>
    /// Reverse-map a WAL column ordinal back to the property name for WAL recovery.
    /// Returns null if the ordinal exceeds the number of properties.
    /// </summary>
    public string? GetColumnNameByOrdinal(uint tableId, uint columnOrdinal)
    {
        var entry = Catalog.GetTableCatalogEntryByOID(tableId);
        if (entry == null) return null;

        uint idx = 0;
        foreach (var propDef in entry.GetProperties())
        {
            if (idx == columnOrdinal)
                return propDef.Name;
            idx++;
        }
        return null;
    }

    /// <summary>
    /// Replay a CREATE TABLE from a deserialized catalog entry during WAL recovery.
    /// </summary>
    public void ReplayCreateTable(BogDb.Core.Catalog.CatalogEntry entry)
    {
        if (entry is not BogDb.Core.Catalog.TableCatalogEntry tableEntry)
            return;
        if (Catalog.ContainsTable(null, entry.Name))
            return;

        Catalog.CreateTableEntry(Transaction.Transaction.DUMMY_TRANSACTION, tableEntry);
        if (entry is BogDb.Core.Catalog.NodeTableCatalogEntry)
            NodeTables[entry.Name] = new NodeTableData();
        else if (entry is BogDb.Core.Catalog.RelGroupCatalogEntry relGroup)
            RelTables[entry.Name] = new RelTableData(relGroup.SrcTableName, relGroup.DstTableName);
    }

    private static void BackfillRelTableMetadata(
        CatalogModel catalog,
        Dictionary<string, RelTableData> relTables)
    {
        foreach (var (tableName, relTable) in relTables)
        {
            if (relTable.TryGetEndpointTables(out _, out _))
                continue;

            if (catalog.GetTableCatalogEntry(null, tableName) is BogDb.Core.Catalog.RelGroupCatalogEntry relEntry)
                SyncRelTableEndpoints(relTable, relEntry);
        }
    }

    private static void ApplyDroppedTableRecord(
        CatalogModel catalog,
        Dictionary<string, NodeTableData> nodeTables,
        Dictionary<string, RelTableData> relTables,
        uint tableId)
    {
        var entry = catalog.GetTableCatalogEntryByOID(tableId);
        if (entry is null)
            return;

        if (entry is BogDb.Core.Catalog.NodeTableCatalogEntry)
            nodeTables.Remove(entry.Name);
        else if (entry is BogDb.Core.Catalog.RelGroupCatalogEntry)
            relTables.Remove(entry.Name);

        catalog.DropIndexEntriesForTable(Transaction.Transaction.DUMMY_TRANSACTION, entry.Name);
        catalog.DropTableEntry(Transaction.Transaction.DUMMY_TRANSACTION, entry.Name);
    }

    private void RebuildNodeIndexesFromCatalog()
    {
        foreach (var indexEntry in Catalog.GetIndexEntries())
        {
            var tableName = indexEntry.TableName;
            var propertyName = indexEntry.PropertyName;
            var index = GetNodePropertyIndex(tableName);

            if (!_isInMemory)
            {
                var indexPath = GetIndexFilePath(tableName, propertyName);
                index.CreateDiskBackedIndex(propertyName, indexPath);
            }
            else
            {
                index.CreateIndex(propertyName);
            }

            index.Rebuild(propertyName, EnumerateNodeRows(tableName));
        }
    }

    private bool TryLoadNodeIndexesFromSnapshot()
    {
        var indexDataPath = Path.Combine(DatabasePath, IndexDataFileName);
        IndexSnapshotSerializer.IndexSnapshot? snapshot;
        try
        {
            snapshot = IndexSnapshotSerializer.TryRead(indexDataPath);
        }
        catch
        {
            return false;
        }

        if (snapshot is null)
            return false;

        foreach (var indexEntry in Catalog.GetIndexEntries())
        {
            var tableName = indexEntry.TableName;
            var propertyName = indexEntry.PropertyName;
            if (!snapshot.Tables.TryGetValue(tableName, out var tableSnapshot) ||
                !tableSnapshot.Properties.TryGetValue(propertyName, out var propertySnapshot))
            {
                return false;
            }

            var index = GetNodePropertyIndex(tableName);

            if (!_isInMemory)
            {
                // Use DiskBackedNodeIndex as backing store — future writes persist to .kzix
                var indexPath = GetIndexFilePath(tableName, propertyName);
                index.CreateDiskBackedIndex(propertyName, indexPath);
                // CreateDiskBackedIndex loads the .kzix, but the snapshot (recomputed from live rows) is the
                // source of truth — the on-disk index can hold stale offsets after delete/reinsert cycles.
                // Clear the loaded entries first so we neither double-populate nor keep stale ones.
                index.ClearIndex(propertyName);
                foreach (var (key, offsets) in propertySnapshot.Entries)
                {
                    foreach (var nodeOffset in offsets)
                        index.Put(propertyName, key, nodeOffset);
                }
            }
            else
            {
                index.CreateIndex(propertyName);
                index.LoadEntries(propertyName, propertySnapshot.Entries);
            }
        }

        return true;
    }

    private void NormalizeLoadedValuesFromCatalog()
    {
        foreach (var (tableName, table) in NodeTables)
        {
            var entry = Catalog.GetTableCatalogEntry(null, tableName, useInternal: false);
            table.NormalizeCommittedValues(entry);
        }

        foreach (var (tableName, table) in RelTables)
        {
            var entry = Catalog.GetTableCatalogEntry(null, tableName, useInternal: false);
            table.NormalizeCommittedValues(entry);
        }
    }

    internal void BindTablePersistenceSurface(string tableName, NodeTableData table)
    {
        var entry = Catalog.GetTableCatalogEntry(null, tableName, useInternal: false);
        var tableId = (uint)(entry?.OID ?? 0);
        _columnFactory?.BeginTable(tableName);
        table.BindPersistenceSurface(tableName, GraphLog, StorageManager.GetWAL(), tableId, _columnFactory);
    }

    internal void BindTablePersistenceSurface(string tableName, RelTableData table)
    {
        var entry = Catalog.GetTableCatalogEntry(null, tableName, useInternal: false);
        var tableId = (uint)(entry?.OID ?? 0);
        _columnFactory?.BeginTable(tableName);
        table.BindPersistenceSurface(tableName, GraphLog, StorageManager.GetWAL(), tableId, _columnFactory);
    }

    private void BindTablePersistenceSurfaces()
    {
        foreach (var (tableName, table) in NodeTables)
            BindTablePersistenceSurface(tableName, table);

        foreach (var (tableName, table) in RelTables)
            BindTablePersistenceSurface(tableName, table);
    }

    private static void SerializeGraphData(
        BinaryWriter writer,
        Dictionary<string, NodeTableData> nodeTables,
        Dictionary<string, RelTableData> relTables)
    {
        writer.Write(nodeTables.Count);
        foreach (var (tableName, tableData) in nodeTables)
        {
            writer.Write(tableName);
            writer.Write(tableData.Count);
            foreach (var (id, properties) in tableData.EnumerateRows())
            {
                GraphDataSerializer.WriteValue(writer, id);
                GraphDataSerializer.WriteProperties(writer, properties);
            }
        }

        writer.Write(relTables.Count);
        foreach (var (tableName, tableData) in relTables)
        {
            writer.Write(tableName);
            writer.Write(tableData.Count);
            foreach (var (edgeKey, properties) in tableData.EnumerateRows())
            {
                GraphDataSerializer.WriteValue(writer, edgeKey.From);
                GraphDataSerializer.WriteValue(writer, edgeKey.To);
                GraphDataSerializer.WriteProperties(writer, properties);
            }
        }
    }
}
