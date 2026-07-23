using System.Collections.Generic;
using System.Linq;
using BogDb.Core.Binder;
using BogDb.Core.Parser;

namespace BogDb.Core.Processor.Operator;

public sealed class RecursiveExtend : PhysicalOperator
{
    private readonly Main.BogDatabase _database;
    private readonly QueryRel _queryRel;
    private readonly int _lowerBound;
    private readonly int _upperBound;
    private readonly long _earlyStopLimit;
    private ExecutionState? _inputState;
    private IEnumerator<(object dstId, string dstTableName, List<Main.EdgeKey> edges)>? _iterator;
    private long _emittedCount;

    public long EarlyStopLimit => _earlyStopLimit;

    public RecursiveExtend(
        Main.BogDatabase database,
        QueryRel queryRel,
        int lowerBound,
        int upperBound,
        long earlyStopLimit,
        PhysicalOperator child,
        uint id)
        : base(PhysicalOperatorType.RECURSIVE_EXTEND, child, id)
    {
        _database = database;
        _queryRel = queryRel;
        _lowerBound = lowerBound;
        _upperBound = upperBound;
        _earlyStopLimit = earlyStopLimit;
    }

    public override bool GetNextTuple(ExecutionContext context)
    {
        if (_earlyStopLimit >= 0 && _emittedCount >= _earlyStopLimit)
        {
            return false;
        }

        while (true)
        {
            if (_inputState == null)
            {
                if (!Children[0].GetNextTuple(context))
                {
                    return false;
                }
                _inputState = context.CaptureState();
                
                object? boundSrcId = null;
                bool hasSrc = context.CurrentVariableIds != null && 
                              context.CurrentVariableIds.TryGetValue(_queryRel.SrcNode.VariableName, out boundSrcId);
                
                if (!hasSrc || boundSrcId == null)
                {
                    continue; 
                }

                if (!TryResolveNode(boundSrcId, _queryRel.SrcNode.TableNames,
                        context.Transaction, out var actualSrcTableName, out _))
                {
                    _inputState = null;
                    continue;
                }

                _iterator = GetRecursivePaths(boundSrcId, actualSrcTableName, context).GetEnumerator();
            }

            context.RestoreState(_inputState);
            while (_iterator!.MoveNext())
            {
                var tuple = _iterator.Current;
                var dstId = tuple.dstId;
                var dstTableName = tuple.dstTableName;
                var edges = tuple.edges;

                if (IsFilteredBySemiMask(context, dstTableName, dstId))
                {
                    continue;
                }

                if (!TryResolveNode(context.CurrentVariableIds![_queryRel.SrcNode.VariableName], _queryRel.SrcNode.TableNames,
                        context.Transaction, out var actualSrcTableName, out var srcProps) ||
                    !TryResolveNode(dstId, _queryRel.DstNode.TableNames,
                        context.Transaction, out var actualDstTableName, out var dstProps) ||
                    !string.Equals(actualDstTableName, dstTableName, System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                context.CurrentNodeId = dstId;
                context.CurrentNodeProperties = dstProps;
                context.CurrentVariableIds ??= new Dictionary<string, object>();
                context.CurrentVariableProperties ??= new Dictionary<string, Dictionary<string, object>>();

                context.CurrentVariableIds[_queryRel.DstNode.VariableName] = dstId;
                context.CurrentVariableProperties[_queryRel.SrcNode.VariableName] = srcProps;
                context.CurrentVariableProperties[_queryRel.DstNode.VariableName] = dstProps;

                if (!string.IsNullOrEmpty(_queryRel.VariableName))
                {
                    context.CurrentVariableIds[_queryRel.VariableName] = edges; // Store as List<EdgeKey>
                }

                if (!string.IsNullOrEmpty(_queryRel.VariableName) || !string.IsNullOrEmpty(_queryRel.PathVariableName))
                {
                    var pathDict = BuildPathValue(context, edges);
                    context.CurrentScalarBindings ??= new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

                    if (!string.IsNullOrEmpty(_queryRel.VariableName))
                    {
                        context.CurrentScalarBindings[_queryRel.VariableName] = pathDict;
                        context.CurrentVariableProperties[_queryRel.VariableName] = pathDict
                            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
                    }

                    if (!string.IsNullOrEmpty(_queryRel.PathVariableName))
                    {
                        context.CurrentScalarBindings[_queryRel.PathVariableName] = pathDict;
                        context.CurrentVariableProperties[_queryRel.PathVariableName] = pathDict
                            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
                    }
                }

                context.CurrentProjectionRow = null;
                _emittedCount++;
                return true;
            }

            _inputState = null;
        }
    }

    private bool IsFilteredBySemiMask(ExecutionContext context, string dstTableName, object dstId)
    {
        if (context.SemiMasks == null)
            return false;

        var entry = _database.Catalog.GetTableEntry(dstTableName);
        var dstTableId = entry?.TableID ?? 0;

        if (dstTableId == 0)
            return false;

        return context.SemiMasks.TryGetValue(dstTableId, out var set) && !set.Contains(dstId);
    }

    private IEnumerable<(object dstId, string dstTableName, List<Main.EdgeKey> edges)> GetRecursivePaths(
        object startId,
        string startTableName,
        ExecutionContext context)
    {
        var tx = context.Transaction;
        var relTableNames = _queryRel.TableNames.Count > 0
            ? _queryRel.TableNames
            : (IReadOnlyList<string>)new List<string>(_database.RelTables.Keys);

        // DFS Stack: (currentId, currentTableName, pathSoFar, visitedNodes)
        var stack = new Stack<(object id, string tableName, List<Main.EdgeKey> path, HashSet<(string TableName, object NodeId)> visited)>();
        stack.Push((startId, startTableName, new List<Main.EdgeKey>(), new HashSet<(string TableName, object NodeId)> { (startTableName, startId) }));

        while (stack.Count > 0)
        {
            var (currId, currentTableName, path, visited) = stack.Pop();

            if (path.Count >= _lowerBound && path.Count <= _upperBound)
            {
                yield return (currId, currentTableName, path);
            }

            if (path.Count >= _upperBound)
            {
                continue; // Cannot expand further
            }

            foreach (var relTableName in relTableNames)
            {
                if (!_database.RelTables.TryGetValue(relTableName, out var relTable))
                {
                    continue;
                }

                foreach (var step in EnumerateNextSteps(relTableName, relTable, currId, currentTableName, context))
                {
                    var visitKey = (step.NextTableName, step.NextId);
                    if (visited.Contains(visitKey))
                    {
                        continue;
                    }

                    var nextPath = new List<Main.EdgeKey>(path) { step.Edge };
                    var nextVisited = new HashSet<(string TableName, object NodeId)>(visited) { visitKey };
                    stack.Push((step.NextId, step.NextTableName, nextPath, nextVisited));
                }
            }
        }
    }

    private IEnumerable<(object NextId, string NextTableName, Main.EdgeKey Edge)> EnumerateNextSteps(
        string relTableName,
        Main.RelTableData relTable,
        object currentId,
        string currentTableName,
        ExecutionContext context)
    {
        var tx = context.Transaction;
        IEnumerable<KeyValuePair<Main.EdgeKey, Dictionary<string, object>>> edges = _queryRel.Direction switch
        {
            ArrowDirection.RIGHT => relTable.EnumerateOutgoingRows(currentId, tx),
            ArrowDirection.LEFT => relTable.EnumerateIncomingRows(currentId, tx),
            ArrowDirection.BOTH => EnumerateBothDirections(relTable, currentId, tx),
            _ => relTable.EnumerateOutgoingRows(currentId, tx)
        };

        foreach (var kvp in edges)
        {
            var edge = kvp.Key;
            if (!TryResolveEndpoints(edge, currentId, out var nextId))
            {
                continue;
            }

            // Per-hop comprehension filter: prune this edge if it fails the predicate. Because pruning
            // happens before the edge is yielded/expanded, a path through a failing hop never continues.
            if (_queryRel.PerHopPredicate != null &&
                !PassesPerHopPredicate(context, relTableName, kvp.Value, nextId, tx))
            {
                continue;
            }

            foreach (var nextTableName in ResolveNextTableNames(relTableName, currentTableName, nextId, tx))
            {
                yield return (nextId, nextTableName, edge);
            }
        }
    }

    // Evaluates the variable-length per-hop predicate against a candidate edge (and the node it reaches),
    // binding the comprehension's edge variable (rr) and node variable (nn) into the evaluation context.
    private bool PassesPerHopPredicate(
        ExecutionContext context,
        string relTableName,
        Dictionary<string, object> rawEdgeProperties,
        object nextId,
        BogDb.Core.Transaction.Transaction tx)
    {
        var edgeProperties = new Dictionary<string, object>(
            _database.NormalizeRelPropertiesForRead(relTableName, rawEdgeProperties))
        {
            ["_label"] = relTableName,
            ["_type"] = relTableName
        };

        var variables = context.CurrentVariableProperties ??= new Dictionary<string, Dictionary<string, object>>();

        // Scope the comprehension variables (rr, nn) to THIS evaluation only: bind them, evaluate, then
        // restore any prior binding of the same name. Without the restore, a comprehension variable that
        // shadows an outer variable would leak the per-hop value downstream and corrupt that variable's rows.
        var relKey = _queryRel.PerHopRelVariable!;
        var hadRel = variables.TryGetValue(relKey, out var priorRel);
        variables[relKey] = edgeProperties;

        var nodeKey = _queryRel.PerHopNodeVariable;
        var hasNodeKey = !string.IsNullOrEmpty(nodeKey);
        var hadNode = false;
        Dictionary<string, object>? priorNode = null;
        if (hasNodeKey)
        {
            hadNode = variables.TryGetValue(nodeKey!, out priorNode);
            if (TryResolveNode(nextId, _queryRel.DstNode.TableNames, tx, out _, out var nodeProperties))
                variables[nodeKey!] = nodeProperties;
            else
                variables.Remove(nodeKey!); // landing node unresolved → nn.* is null, not a stale prior value
        }

        try
        {
            return ExpressionExecutionHelper.EvaluatePredicate(_queryRel.PerHopPredicate!, context);
        }
        finally
        {
            if (hadRel) variables[relKey] = priorRel!;
            else variables.Remove(relKey);

            if (hasNodeKey)
            {
                if (hadNode) variables[nodeKey!] = priorNode!;
                else variables.Remove(nodeKey!);
            }
        }
    }

    private static IEnumerable<KeyValuePair<Main.EdgeKey, Dictionary<string, object>>> EnumerateBothDirections(
        Main.RelTableData relTable,
        object nodeId,
        BogDb.Core.Transaction.Transaction tx)
    {
        foreach (var edge in relTable.EnumerateOutgoingRows(nodeId, tx))
            yield return edge;
        foreach (var edge in relTable.EnumerateIncomingRows(nodeId, tx))
            yield return edge;
    }

    private bool TryResolveEndpoints(Main.EdgeKey edge, object activeSrcId, out object nextId)
    {
        nextId = activeSrcId; // Default initialization

        switch (_queryRel.Direction)
        {
            case ArrowDirection.RIGHT:
                if (Equals(edge.From, activeSrcId))
                {
                    nextId = edge.To;
                    return true;
                }
                return false;
            case ArrowDirection.LEFT:
                if (Equals(edge.To, activeSrcId))
                {
                    nextId = edge.From;
                    return true;
                }
                return false;
            case ArrowDirection.BOTH:
                if (Equals(edge.From, activeSrcId))
                {
                    nextId = edge.To;
                    return true;
                }
                else if (Equals(edge.To, activeSrcId))
                {
                    nextId = edge.From;
                    return true;
                }
                return false;
            default:
                if (Equals(edge.From, activeSrcId))
                {
                    nextId = edge.To;
                    return true;
                }
                return false;
        }
    }

    private bool TryResolveNode(
        object nodeId,
        IReadOnlyList<string> candidateTableNames,
        BogDb.Core.Transaction.Transaction tx,
        out string actualTableName,
        out Dictionary<string, object> properties)
    {
        var tableNames = candidateTableNames.Count > 0
            ? candidateTableNames
            : (IReadOnlyList<string>)new List<string>(_database.NodeTables.Keys);
        var sawConfiguredCandidate = false;

        foreach (var tableName in tableNames)
        {
            if (!_database.NodeTables.TryGetValue(tableName, out var table))
                continue;
            sawConfiguredCandidate = true;
            if (!table.TryGetProperties(tx, nodeId, out var props) || props is null)
                continue;

            properties = _database.NormalizeNodePropertiesForRead(tableName, props);
            if (!properties.ContainsKey("id"))
                properties["id"] = nodeId;
            actualTableName = tableName;
            return true;
        }

        if (candidateTableNames.Count > 0 && !sawConfiguredCandidate)
        {
            actualTableName = candidateTableNames[0];
            properties = new Dictionary<string, object> { ["id"] = nodeId };
            return true;
        }

        actualTableName = string.Empty;
        properties = null!;
        return false;
    }

    private IEnumerable<string> ResolveNextTableNames(
        string relTableName,
        string currentTableName,
        object nextId,
        BogDb.Core.Transaction.Transaction tx)
    {
        var candidateTableNames = new List<string>();

        foreach (var connection in _queryRel.AllowedConnections)
        {
            if (!string.Equals(connection.TableName, relTableName, System.StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.Equals(connection.SrcTableName, currentTableName, System.StringComparison.OrdinalIgnoreCase))
                continue;
            if (!candidateTableNames.Contains(connection.DstTableName, System.StringComparer.OrdinalIgnoreCase))
                candidateTableNames.Add(connection.DstTableName);
        }

        var tableNamesToProbe = candidateTableNames.Count > 0
            ? candidateTableNames
            : new List<string>(_database.NodeTables.Keys);

        var yielded = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var tableName in tableNamesToProbe)
        {
            if (!_database.NodeTables.TryGetValue(tableName, out var table))
                continue;
            if (!table.TryGetProperties(tx, nextId, out var props) || props is null)
                continue;
            if (yielded.Add(tableName))
                yield return tableName;
        }
    }

    private Dictionary<string, object?> BuildPathValue(ExecutionContext context, List<Main.EdgeKey> edges)
    {
        var nodeIds = new List<object?>(edges.Count + 1);
        nodeIds.Add(context.CurrentVariableIds!.TryGetValue(_queryRel.SrcNode.VariableName, out var sId) ? sId : context.CurrentNodeId);
        foreach (var edge in edges)
            nodeIds.Add(Equals(edge.From, nodeIds[nodeIds.Count - 1]) ? (object?)edge.To : (object?)edge.From);

        var rels = edges.Select(edge => (object?)edge).ToList();
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["_nodes"] = nodeIds,
            ["_rels"] = rels,
            ["_length"] = (long)edges.Count,
            ["nodes"] = nodeIds,
            ["rels"] = rels
        };
    }
}
