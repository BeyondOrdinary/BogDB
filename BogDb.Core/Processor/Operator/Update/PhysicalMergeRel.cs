using System;
using System.Collections.Generic;
using System.Linq;
using BogDb.Core.Binder;
using BogDb.Core.Catalog;
using BogDb.Core.Common;

namespace BogDb.Core.Processor.Operator.Update;

public sealed class PhysicalMergeRel : PhysicalOperator
{
    private readonly Main.BogDatabase _database;
    private readonly QueryRel _mergeRel;
    private readonly IReadOnlyList<BoundMergeAction> _actions;
    private bool _executedWithoutChild;

    public PhysicalMergeRel(
        Main.BogDatabase database,
        QueryRel mergeRel,
        IReadOnlyList<BoundMergeAction> actions,
        PhysicalOperator? child,
        uint id)
        : base(PhysicalOperatorType.MERGE_REL, id)
    {
        _database = database;
        _mergeRel = mergeRel;
        _actions = actions;
        if (child != null)
            Children.Add(child);
    }

    public override bool GetNextTuple(ExecutionContext context)
    {
        if (Children.Count == 0)
        {
            if (_executedWithoutChild)
                return false;

            ExecuteMerge(context);
            _executedWithoutChild = true;
            return true;
        }

        if (!Children[0].GetNextTuple(context))
            return false;

        ExecuteMerge(context);
        return true;
    }

    private void ExecuteMerge(ExecutionContext context)
    {
        var (fromId, _, actualSrcTable, srcCreated) = EnsureNodeExists(_mergeRel.SrcNode, context);
        var (toId, _, actualDstTable, dstCreated) = EnsureNodeExists(_mergeRel.DstNode, context);
        var relEntry = ResolveRelCatalogEntry();
        var properties = PropertyValueCoercion.CoerceProperties(relEntry, EvaluateInlineProperties(context));
        var (tableName, table) = ResolveRelTable(actualSrcTable, actualDstTable);
        var edgeKey = new Main.EdgeKey(fromId, toId);
        var relCreated = false;
        Main.RelRowRef relRef;
        Dictionary<string, object> existingProps;

        if (table.TryFindVisibleRow(context.Transaction, edgeKey, props => MatchesProperties(props, properties), out var rowIndex, out var matchedProps) &&
            matchedProps != null)
        {
            existingProps = new Dictionary<string, object>(matchedProps, StringComparer.OrdinalIgnoreCase);
            relRef = new Main.RelRowRef(tableName, rowIndex, edgeKey);
        }
        else
        {
            relCreated = true;
            var insertedRowIndex = table.Insert(context.Transaction, edgeKey, properties);
            _database.GraphLog.AppendRelInsert(tableName, fromId, toId, properties);
            existingProps = new Dictionary<string, object>(properties, StringComparer.OrdinalIgnoreCase);
            relRef = new Main.RelRowRef(tableName, insertedRowIndex, edgeKey);
        }

        BindContext(relRef, existingProps, context);
        ApplyActions(srcCreated || dstCreated || relCreated, relRef, table, context);
    }

    private Dictionary<string, object> EvaluateInlineProperties(ExecutionContext context)
    {
        var properties = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        if (_mergeRel.InlinePropertyBag != null)
        {
            var propertyBag = ExpressionExecutionHelper.Evaluate(_mergeRel.InlinePropertyBag, context);
            if (propertyBag is not Dictionary<string, object> dict)
                throw new InvalidOperationException("MERGE relationship property bag expressions must evaluate to a dictionary.");

            foreach (var (key, value) in dict)
            {
                var normalized = TypeCoercionHelper.Normalize(value);
                if (normalized is not null)
                    properties[key] = normalized;
            }
        }

        foreach (var (key, expression) in _mergeRel.InlineProperties)
        {
            var normalized = TypeCoercionHelper.Normalize(ExpressionExecutionHelper.Evaluate(expression, context));
            if (normalized is not null)
                properties[key] = normalized;
        }

        return properties;
    }

    private (object NodeId, Dictionary<string, object> Properties, string TableName, bool Created) EnsureNodeExists(
        QueryNode node,
        ExecutionContext context)
    {
        if (context.CurrentVariableIds != null &&
            context.CurrentVariableIds.TryGetValue(node.VariableName, out var existingId))
        {
            var existingTableName = ResolveNodeTableName(existingId, node.TableNames, context);
            if (!_database.NodeTables.TryGetValue(existingTableName, out var existingTable) ||
                !existingTable.TryGetProperties(context.Transaction, existingId, out var existingProps) ||
                existingProps == null)
            {
                throw new InvalidOperationException("MERGE endpoint node must be visible in its resolved table.");
            }

            var visibleProps = new Dictionary<string, object>(existingProps, StringComparer.OrdinalIgnoreCase);
            if (!visibleProps.ContainsKey("id"))
                visibleProps["id"] = existingId;
            context.CurrentVariableProperties ??= new Dictionary<string, Dictionary<string, object>>();
            context.CurrentVariableProperties[node.VariableName] = visibleProps;
            return (existingId, visibleProps, existingTableName, false);
        }

        var tableName = node.TableNames.Count > 0 ? node.TableNames[0] : string.Empty;
        if (string.IsNullOrEmpty(tableName) || !_database.NodeTables.TryGetValue(tableName, out var table))
            throw new InvalidOperationException("MERGE endpoint node table must exist.");

        var nodeEntry = ResolveNodeCatalogEntry(tableName);
        var properties = PropertyValueCoercion.CoerceProperties(nodeEntry, EvaluateNodeInlineProperties(node, context));
        var foundId = TryFindExistingNodeId(table, properties, context, out var existingNodeProps);

        object nodeId;
        Dictionary<string, object> visibleNodeProps;
        var created = false;
        if (foundId != null)
        {
            nodeId = foundId;
            visibleNodeProps = existingNodeProps ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            created = true;
            nodeId = properties.TryGetValue("id", out var idValue)
                ? idValue
                : Guid.NewGuid().ToString();

            table.Upsert(context.Transaction, nodeId, properties);
            _database.GraphLog.AppendNode(tableName, nodeId, properties);
            _database.UpdateNodeIndexes(tableName, context.Transaction, nodeId, properties, table);
            visibleNodeProps = new Dictionary<string, object>(properties, StringComparer.OrdinalIgnoreCase);
        }

        BindNodeContext(node.VariableName, nodeId, visibleNodeProps, context);
        return (nodeId, visibleNodeProps, tableName, created);
    }

    private static Dictionary<string, object> EvaluateNodeInlineProperties(QueryNode node, ExecutionContext context)
    {
        var properties = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        if (node.InlinePropertyBag != null)
        {
            var propertyBag = ExpressionExecutionHelper.Evaluate(node.InlinePropertyBag, context);
            if (propertyBag is not Dictionary<string, object> dict)
                throw new InvalidOperationException("MERGE endpoint property bag expressions must evaluate to a dictionary.");

            foreach (var (key, value) in dict)
            {
                var normalized = TypeCoercionHelper.Normalize(value);
                if (normalized is not null)
                    properties[key] = normalized;
            }
        }

        foreach (var (key, expression) in node.InlineProperties)
        {
            var normalized = TypeCoercionHelper.Normalize(ExpressionExecutionHelper.Evaluate(expression, context));
            if (normalized is not null)
                properties[key] = normalized;
        }

        return properties;
    }

    private string ResolveNodeTableName(
        object nodeId,
        IReadOnlyList<string> candidateTableNames,
        ExecutionContext context)
    {
        var tableNames = candidateTableNames.Count > 0
            ? candidateTableNames
            : (IReadOnlyList<string>)new List<string>(_database.NodeTables.Keys);

        foreach (var tableName in tableNames)
        {
            if (_database.NodeTables.TryGetValue(tableName, out var table) &&
                table.TryGetProperties(context.Transaction, nodeId, out _))
            {
                return tableName;
            }
        }

        throw new InvalidOperationException($"Could not resolve bound node {nodeId} to an eligible endpoint table for MERGE.");
    }

    private static object? TryFindExistingNodeId(
        Main.NodeTableData table,
        Dictionary<string, object> properties,
        ExecutionContext context,
        out Dictionary<string, object>? existingProps)
    {
        return MergeConflictHelper.TryFindExistingNodeIdOrThrow(
            table,
            properties,
            context,
            MatchesNodeProperties,
            out var nodeId,
            out existingProps)
            ? nodeId
            : null;
    }

    private static bool MatchesNodeProperties(
        Dictionary<string, object> visibleProps,
        Dictionary<string, object> expectedProperties,
        object nodeId)
    {
        foreach (var (key, expectedValue) in expectedProperties)
        {
            object? actual = key.Equals("id", StringComparison.OrdinalIgnoreCase)
                ? nodeId
                : visibleProps.TryGetValue(key, out var value) ? value : null;

            if (!StructuralValueComparer.AreEqual(actual, expectedValue))
                return false;
        }

        return true;
    }

    private (string TableName, Main.RelTableData Table) ResolveRelTable(string actualSrcTable, string actualDstTable)
    {
        var candidateTableNames = _mergeRel.TableNames.Count > 0
            ? _mergeRel.TableNames
            : (IReadOnlyList<string>)new List<string>(_database.RelTables.Keys);

        foreach (var tableName in candidateTableNames)
        {
            if (!_mergeRel.IsConnectionAllowed(tableName, actualSrcTable, actualDstTable))
                continue;

            if (_database.RelTables.TryGetValue(tableName, out var table))
                return (tableName, table);
        }

        throw new InvalidOperationException("MERGE relationship table must exist and accept the resolved endpoint tables.");
    }

    private static bool MatchesProperties(
        Dictionary<string, object> visibleProps,
        Dictionary<string, object> expectedProperties)
    {
        foreach (var (key, expectedValue) in expectedProperties)
        {
            var actual = visibleProps.TryGetValue(key, out var value) ? value : null;
            if (!StructuralValueComparer.AreEqual(actual, expectedValue))
                return false;
        }

        return true;
    }

    private void ApplyActions(
        bool created,
        Main.RelRowRef relRef,
        Main.RelTableData table,
        ExecutionContext context)
    {
        var relEntry = ResolveRelCatalogEntry();
        foreach (var action in _actions)
        {
            if (created == action.IsOnMatch)
                continue;

            foreach (var setItem in action.SetClause.SetItems)
            {
                if (setItem is not BoundComparisonExpression comp || comp.ExpressionType != ExpressionType.EQUALS)
                    continue;

                if (comp.Left is VariableExpression variableExpr)
                {
                    if (variableExpr.QueryRel != null || variableExpr.DataType == LogicalTypeID.REL)
                    {
                        ApplyRelPropertyBagReplacement(variableExpr.VariableName, comp.Right, relRef, table, context);
                        continue;
                    }

                    if (variableExpr.QueryNode != null || variableExpr.DataType == LogicalTypeID.NODE)
                    {
                        ApplyNodePropertyBagReplacement(variableExpr.VariableName, comp.Right, context);
                        continue;
                    }
                }

                if (comp.Left is not PropertyExpression propertyExpr)
                    continue;

                var value = TypeCoercionHelper.Normalize(ExpressionExecutionHelper.Evaluate(comp.Right, context));
                ApplyPropertyAssignment(propertyExpr.NodeVariableName, propertyExpr.PropertyName, value, relRef, table, relEntry, context);
            }
        }
    }

    private void ApplyPropertyAssignment(
        string variableName,
        string propertyName,
        object? value,
        Main.RelRowRef relRef,
        Main.RelTableData table,
        RelGroupCatalogEntry? relEntry,
        ExecutionContext context)
    {
        if (context.CurrentVariableIds != null &&
            context.CurrentVariableIds.TryGetValue(variableName, out var variableId) &&
            variableId is Main.RelRowRef currentRelRef)
        {
            var assignedValue = relEntry != null && relEntry.ContainsProperty(propertyName)
                ? PropertyValueCoercion.CoercePropertyValue(relEntry.GetProperty(propertyName), value)
                : value;
            table.SetProperty(context.Transaction, currentRelRef.RowIndex, propertyName, assignedValue);
            UpdateVisibleRelProperty(variableName, propertyName, assignedValue, context);
            return;
        }

        if (context.CurrentVariableIds != null &&
            context.CurrentVariableIds.TryGetValue(variableName, out var nodeId))
        {
            foreach (var (tableName, nodeTable) in _database.NodeTables)
            {
                var nodeEntry = ResolveNodeCatalogEntry(tableName);
                var assignedValue = nodeEntry != null && nodeEntry.ContainsProperty(propertyName)
                    ? PropertyValueCoercion.CoercePropertyValue(nodeEntry.GetProperty(propertyName), value)
                    : value;
                if (!nodeTable.SetProperty(context.Transaction, nodeId, propertyName, assignedValue))
                    continue;

                _database.RefreshNodeIndexesForVisibleRow(tableName, context.Transaction, nodeId, nodeTable);
                UpdateVisibleNodeProperty(variableName, propertyName, assignedValue, context);
                return;
            }
        }
    }

    private void ApplyRelPropertyBagReplacement(
        string variableName,
        Expression propertyBagExpression,
        Main.RelRowRef relRef,
        Main.RelTableData table,
        ExecutionContext context)
    {
        var raw = ExpressionExecutionHelper.Evaluate(propertyBagExpression, context);
        if (raw is not Dictionary<string, object> dict)
            throw new InvalidOperationException("MERGE SET relationship property bag expressions must evaluate to a dictionary.");

        var normalized = dict.ToDictionary(
            kvp => kvp.Key,
            kvp => TypeCoercionHelper.Normalize(kvp.Value),
            StringComparer.OrdinalIgnoreCase)
            .Where(kvp => kvp.Value is not null)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!, StringComparer.OrdinalIgnoreCase);
        var replacement = PropertyValueCoercion.CoerceProperties(ResolveRelCatalogEntry(), normalized);

        table.ReplaceRow(context.Transaction, relRef.RowIndex, replacement);
        context.CurrentVariableProperties ??= new Dictionary<string, Dictionary<string, object>>();
        context.CurrentVariableProperties[variableName] = new Dictionary<string, object>(replacement, StringComparer.OrdinalIgnoreCase);
    }

    private void ApplyNodePropertyBagReplacement(
        string variableName,
        Expression propertyBagExpression,
        ExecutionContext context)
    {
        if (context.CurrentVariableIds == null ||
            !context.CurrentVariableIds.TryGetValue(variableName, out var nodeId))
            return;

        var raw = ExpressionExecutionHelper.Evaluate(propertyBagExpression, context);
        if (raw is not Dictionary<string, object> dict)
            throw new InvalidOperationException("MERGE SET node property bag expressions must evaluate to a dictionary.");

        var replacement = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in dict)
        {
            if (key.Equals("id", StringComparison.OrdinalIgnoreCase))
                continue;
            var normalized = TypeCoercionHelper.Normalize(value);
            if (normalized is not null)
                replacement[key] = normalized;
        }

        foreach (var (tableName, table) in _database.NodeTables)
        {
            if (!table.TryGetProperties(context.Transaction, nodeId, out _))
                continue;

            var coercedReplacement = PropertyValueCoercion.CoerceProperties(ResolveNodeCatalogEntry(tableName), replacement);
            table.Upsert(context.Transaction, nodeId, coercedReplacement);
            _database.RefreshNodeIndexesForVisibleRow(tableName, context.Transaction, nodeId, table);
            context.CurrentVariableProperties ??= new Dictionary<string, Dictionary<string, object>>();
            context.CurrentVariableProperties[variableName] = new Dictionary<string, object>(coercedReplacement, StringComparer.OrdinalIgnoreCase);
            return;
        }
    }

    private NodeTableCatalogEntry? ResolveNodeCatalogEntry(string tableName)
    {
        return _database.Catalog.GetTableCatalogEntry(null, tableName, useInternal: false) as NodeTableCatalogEntry;
    }

    private RelGroupCatalogEntry? ResolveRelCatalogEntry()
    {
        if (_mergeRel.TableNames.Count == 0)
            return null;

        return _database.Catalog.GetTableCatalogEntry(null, _mergeRel.TableNames[0], useInternal: false) as RelGroupCatalogEntry;
    }

    private static void UpdateVisibleRelProperty(
        string variableName,
        string propertyName,
        object? value,
        ExecutionContext context)
    {
        if (context.CurrentVariableProperties == null ||
            !context.CurrentVariableProperties.TryGetValue(variableName, out var props))
            return;

        var updated = new Dictionary<string, object>(props, StringComparer.OrdinalIgnoreCase);
        if (value is null)
            updated.Remove(propertyName);
        else
            updated[propertyName] = value;
        context.CurrentVariableProperties[variableName] = updated;
    }

    private static void UpdateVisibleNodeProperty(
        string variableName,
        string propertyName,
        object? value,
        ExecutionContext context)
    {
        if (context.CurrentVariableProperties != null &&
            context.CurrentVariableProperties.TryGetValue(variableName, out var props))
        {
            var updated = new Dictionary<string, object>(props, StringComparer.OrdinalIgnoreCase);
            if (value is null)
                updated.Remove(propertyName);
            else
                updated[propertyName] = value;
            context.CurrentVariableProperties[variableName] = updated;
            if (context.CurrentNodeId != null &&
                context.CurrentVariableIds != null &&
                context.CurrentVariableIds.TryGetValue(variableName, out var currentNodeId) &&
                Equals(context.CurrentNodeId, currentNodeId))
            {
                context.CurrentNodeProperties = updated;
            }
        }
    }

    private static void BindNodeContext(
        string variableName,
        object nodeId,
        Dictionary<string, object> props,
        ExecutionContext context)
    {
        context.CurrentNodeId = nodeId;
        context.CurrentNodeProperties = props;
        context.CurrentVariableIds ??= new Dictionary<string, object>();
        context.CurrentVariableProperties ??= new Dictionary<string, Dictionary<string, object>>();
        context.CurrentVariableIds[variableName] = nodeId;
        context.CurrentVariableProperties[variableName] = props;
        context.CurrentProjectionRow = null;
    }

    private void BindContext(
        Main.RelRowRef relRef,
        Dictionary<string, object> props,
        ExecutionContext context)
    {
        context.CurrentVariableIds ??= new Dictionary<string, object>();
        context.CurrentVariableProperties ??= new Dictionary<string, Dictionary<string, object>>();
        context.CurrentVariableIds[_mergeRel.VariableName] = relRef;
        context.CurrentVariableProperties[_mergeRel.VariableName] = new Dictionary<string, object>(props, StringComparer.OrdinalIgnoreCase);
        context.CurrentProjectionRow = null;
    }
}
