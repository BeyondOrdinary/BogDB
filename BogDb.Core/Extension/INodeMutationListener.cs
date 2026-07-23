using System.Collections.Generic;

namespace BogDb.Core.Extension;

/// <summary>
/// The kind of node mutation reported to an <see cref="INodeMutationListener"/>.
/// CREATE and SET both surface as <see cref="Upsert"/> — a listener treats an update as remove-then-add.
/// </summary>
public enum NodeMutationKind
{
    Upsert,
    Delete
}

/// <summary>
/// Optional hook for extensions (e.g. vector, FTS) to incrementally maintain derived indexes as node
/// rows change, instead of rebuilding from scratch. Register via
/// <see cref="Main.BogDatabase.RegisterNodeMutationListener"/> from <c>IExtension.Load</c>.
///
/// The callback fires inside a write transaction, BEFORE commit. Implementations must NOT mutate their
/// index eagerly — the transaction may still roll back. Instead they register a commit-deferred action via
/// <see cref="Transaction.Transaction.TrackVersionedAction(Storage.UndoRecordType, System.Action, System.Action)"/>,
/// so the index delta is applied atomically at commit and discarded on rollback. Incremental maintenance is
/// a performance optimization layered on the staleness-fingerprint fallback: if a listener declines to handle
/// a change (or a write path never notifies), the fingerprint diverges and the index is rebuilt on next query,
/// so results stay correct regardless.
/// </summary>
public interface INodeMutationListener
{
    void OnNodeMutation(
        Transaction.Transaction transaction,
        string tableName,
        object nodeId,
        NodeMutationKind kind,
        IReadOnlyDictionary<string, object>? properties);
}
