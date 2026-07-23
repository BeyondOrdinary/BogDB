using System;
using System.Collections.Generic;
using System.Linq;
using BogDb.Core.Binder;
using BogDb.Core.Catalog;
using BogDb.Core.Common;

namespace BogDb.Core.Processor.Operator.Update;

public sealed class PhysicalMergeNode : PhysicalOperator
{
    private readonly Main.BogDatabase _database;
    private readonly QueryNode _mergeNode;
    private readonly IReadOnlyList<BoundMergeAction> _actions;
    private bool _executedWithoutChild;

    public PhysicalMergeNode(
        Main.BogDatabase database,
        QueryNode mergeNode,
        IReadOnlyList<BoundMergeAction> actions,
        PhysicalOperator? child,
        uint id)
        : base(PhysicalOperatorType.MERGE_NODE, id)
    {
        _database = database;
        _mergeNode = mergeNode;
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
        var tableName = _mergeNode.TableNames.Count > 0 ? _mergeNode.TableNames[0] : string.Empty;
        if (string.IsNullOrEmpty(tableName) || !_database.NodeTables.TryGetValue(tableName, out var table))
            throw new InvalidOperationException("MERGE node table must exist.");

        var properties = EvaluateInlineProperties(context);
        var tableEntry = _database.Catalog.GetTableCatalogEntry(context.Transaction, tableName, useInternal: false);
        properties = PropertyValueCoercion.CoerceProperties(tableEntry, properties);
        var foundId = TryFindExistingNodeId(table, properties, context, out var existingProps);
        var created = false;

        object nodeId;
        Dictionary<string, object> visibleProps;
        if (foundId != null)
        {
            nodeId = foundId;
            visibleProps = existingProps ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
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
            visibleProps = new Dictionary<string, object>(properties, StringComparer.OrdinalIgnoreCase);
        }

        BindContext(nodeId, visibleProps, context);
        ApplyActions(created, nodeId, tableName, table, context);
    }

    private Dictionary<string, object> EvaluateInlineProperties(ExecutionContext context)
    {
        var properties = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        if (_mergeNode.InlinePropertyBag != null)
        {
            var propertyBag = ExpressionExecutionHelper.Evaluate(_mergeNode.InlinePropertyBag, context);
            if (propertyBag is not Dictionary<string, object> dict)
                throw new InvalidOperationException("MERGE property bag expressions must evaluate to a dictionary.");

            foreach (var (key, value) in dict)
            {
                var normalized = TypeCoercionHelper.Normalize(value);
                if (normalized is not null)
                    properties[key] = normalized;
            }
        }

        foreach (var (key, expression) in _mergeNode.InlineProperties)
        {
            var normalized = TypeCoercionHelper.Normalize(ExpressionExecutionHelper.Evaluate(expression, context));
            if (normalized is not null)
                properties[key] = normalized;
        }

        return properties;
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
            MatchesProperties,
            out var nodeId,
            out existingProps)
            ? nodeId
            : null;
    }

    private static bool MatchesProperties(
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

    private void ApplyActions(
        bool created,
        object nodeId,
        string tableName,
        Main.NodeTableData table,
        ExecutionContext context)
    {
        var tableEntry = _database.Catalog.GetTableCatalogEntry(context.Transaction, tableName, useInternal: false);
        foreach (var action in _actions)
        {
            if (created == action.IsOnMatch)
                continue;

            foreach (var setItem in action.SetClause.SetItems)
            {
                if (setItem is not BoundComparisonExpression comp || comp.ExpressionType != ExpressionType.EQUALS)
                    continue;

                if (comp.Left is VariableExpression variableExpr &&
                    (variableExpr.QueryNode != null || variableExpr.DataType == LogicalTypeID.NODE))
                {
                    ApplyPropertyBagReplacement(variableExpr.VariableName, comp.Right, nodeId, tableName, table, context);
                    continue;
                }

                if (comp.Left is not PropertyExpression propertyExpr)
                    continue;

                var value = tableEntry != null && tableEntry.ContainsProperty(propertyExpr.PropertyName)
                    ? PropertyValueCoercion.CoercePropertyValue(
                        tableEntry.GetProperty(propertyExpr.PropertyName),
                        ExpressionExecutionHelper.Evaluate(comp.Right, context))
                    : TypeCoercionHelper.Normalize(ExpressionExecutionHelper.Evaluate(comp.Right, context));
                table.SetProperty(context.Transaction, nodeId, propertyExpr.PropertyName, value);
                _database.RefreshNodeIndexesForVisibleRow(tableName, context.Transaction, nodeId, table);
                UpdateVisibleProperty(propertyExpr.NodeVariableName, propertyExpr.PropertyName, value, context);
            }
        }
    }

    private void ApplyPropertyBagReplacement(
        string variableName,
        Expression propertyBagExpression,
        object nodeId,
        string tableName,
        Main.NodeTableData table,
        ExecutionContext context)
    {
        var raw = ExpressionExecutionHelper.Evaluate(propertyBagExpression, context);
        if (raw is not Dictionary<string, object> dict)
            throw new InvalidOperationException("MERGE SET property bag expressions must evaluate to a dictionary.");

        var replacement = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in dict)
        {
            if (key.Equals("id", StringComparison.OrdinalIgnoreCase))
                continue;
            var normalized = TypeCoercionHelper.Normalize(value);
            if (normalized is not null)
                replacement[key] = normalized;
        }

        var tableEntry = _database.Catalog.GetTableCatalogEntry(context.Transaction, tableName, useInternal: false);
        replacement = PropertyValueCoercion.CoerceProperties(tableEntry, replacement);

        table.Upsert(context.Transaction, nodeId, replacement);
        _database.RefreshNodeIndexesForVisibleRow(tableName, context.Transaction, nodeId, table);

        context.CurrentVariableProperties ??= new Dictionary<string, Dictionary<string, object>>();
        context.CurrentVariableProperties[variableName] = new Dictionary<string, object>(replacement, StringComparer.OrdinalIgnoreCase);
        if (context.CurrentNodeId == null || Equals(context.CurrentNodeId, nodeId))
            context.CurrentNodeProperties = context.CurrentVariableProperties[variableName];
    }

    private static void UpdateVisibleProperty(
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
            if (context.CurrentNodeId != null && context.CurrentVariableIds != null && context.CurrentVariableIds.ContainsKey(variableName))
                context.CurrentNodeProperties = updated;
            return;
        }

        if (context.CurrentNodeProperties != null)
        {
            if (value is null)
                context.CurrentNodeProperties.Remove(propertyName);
            else
                context.CurrentNodeProperties[propertyName] = value;
        }
    }

    private void BindContext(object nodeId, Dictionary<string, object> props, ExecutionContext context)
    {
        context.CurrentNodeId = nodeId;
        context.CurrentNodeProperties = props;
        context.CurrentVariableIds ??= new Dictionary<string, object>();
        context.CurrentVariableIds[_mergeNode.VariableName] = nodeId;
        context.CurrentVariableProperties ??= new Dictionary<string, Dictionary<string, object>>();
        context.CurrentVariableProperties[_mergeNode.VariableName] = props;
        context.CurrentProjectionRow = null;
    }
}
