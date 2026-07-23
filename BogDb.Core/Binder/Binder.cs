using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BogDb.Core.Catalog;
using BogDb.Core.Common;
using BogDb.Core.Parser;
using BogDb.Core.Parser.Query;
using BogDb.Core.Parser.Antlr4;

namespace BogDb.Core.Binder;

/// <summary>
/// Master class connecting Parser AST output with the stored Catalog context.
/// Validates table constraints and produces BoundStatements for the Planner.
/// Phase 8: Full QUERY / MATCH / RETURN binding pipeline.
/// </summary>
public class Binder
{
    public BinderScope Scope { get; }
    public ExpressionBinder ExpressionBinder { get; }

    private readonly Catalog.Catalog _catalog;
    private readonly Func<string, List<string>>? _csvHeaderReader;
    private readonly Func<string, Extension.ExtensionOption?>? _extensionOptionResolver;
    private readonly Dictionary<string, LogicalTypeID> _parameterExpectedTypes = new(StringComparer.OrdinalIgnoreCase);
    public Catalog.Catalog Catalog => _catalog;
    public IReadOnlyDictionary<string, LogicalTypeID> ParameterExpectedTypes => _parameterExpectedTypes;
    private int _lastExpressionId;

    public Binder(
        Catalog.Catalog catalog,
        Func<string, List<string>>? csvHeaderReader = null,
        Func<string, Extension.ExtensionOption?>? extensionOptionResolver = null)
    {
        _catalog = catalog;
        _csvHeaderReader = csvHeaderReader;
        _extensionOptionResolver = extensionOptionResolver;
        Scope = new BinderScope();
        ExpressionBinder = new ExpressionBinder(this);
    }

    public BoundStatement Bind(Statement statement)
    {
        switch (statement.GetStatementType())
        {
            case StatementType.QUERY:
                return BindQuery((RegularQuery)statement);
            case StatementType.COPY_TO:
                return BindCopyTo((CopyTo)statement);
            case StatementType.COPY_FROM:
                return BindCopyFrom((CopyFrom)statement);
            case StatementType.CREATE_TABLE:
                return BindCreateTable((DdlStatement)statement);
            case StatementType.DROP:
                return BindDropTable((DropTable)statement);
            case StatementType.ALTER:
                return BindAlterTable((AlterTable)statement);
            case StatementType.CREATE_MACRO:
                return BindCreateMacro((CreateMacro)statement);
            case StatementType.ATTACH_DATABASE:
                return BindAttachDatabase((AttachDatabaseStatement)statement);
            case StatementType.DETACH_DATABASE:
                return BindDetachDatabase((DetachDatabaseStatement)statement);
            case StatementType.USE_DATABASE:
                return BindUseDatabase((UseDatabaseStatement)statement);
            case StatementType.EXTENSION:
                return BindExtensionStatement((ExtensionStatement)statement);
            case StatementType.EXPLAIN:
                return BindExplain((ExplainStatement)statement);
            case StatementType.STANDALONE_CALL_FUNCTION:
                return BindStandaloneCallFunction((StandaloneCallFunction)statement);
            case StatementType.STANDALONE_CALL:
                return BindStandaloneCall((StandaloneCall)statement);
            case StatementType.CREATE_SEQUENCE:
                throw new NotSupportedException(
                    "CREATE SEQUENCE is recognized by the parser but not yet implemented in the execution engine.");
            case StatementType.CREATE_TYPE:
                throw new NotSupportedException(
                    "CREATE TYPE is recognized by the parser but not yet implemented in the execution engine.");
            case StatementType.COMMENT_ON:
                throw new NotSupportedException(
                    "COMMENT ON is recognized by the parser but not yet implemented in the execution engine.");
            case StatementType.EXPORT_DATABASE:
                throw new NotSupportedException(
                    "EXPORT DATABASE is recognized by the parser but not yet implemented in the execution engine.");
            case StatementType.IMPORT_DATABASE:
                throw new NotSupportedException(
                    "IMPORT DATABASE is recognized by the parser but not yet implemented in the execution engine.");
            default:
                throw new NotSupportedException($"Binding engine does not support: {statement.GetStatementType()}");
        }
    }

    internal void RegisterParameterExpectedType(string parameterName, LogicalTypeID expectedType)
    {
        if (string.IsNullOrWhiteSpace(parameterName) || expectedType == LogicalTypeID.ANY)
            return;

        if (_parameterExpectedTypes.TryGetValue(parameterName, out var existingType))
        {
            if (existingType == expectedType)
                return;

            _parameterExpectedTypes[parameterName] =
                LogicalTypeUtils.IsNumerical(existingType) && LogicalTypeUtils.IsNumerical(expectedType)
                    ? LogicalTypeID.DOUBLE
                    : LogicalTypeID.ANY;
            return;
        }

        _parameterExpectedTypes[parameterName] = expectedType;
    }

    // ─── DDL ───────────────────────────────────────────────────────────────────

    private BoundCreateTableBase BindCreateTable(DdlStatement createTable)
    {
        if (createTable is CreateNodeTable nodeTable)
        {
            var props = new List<BoundColumnDefinition>();
            foreach (var prop in nodeTable.Info.PropertyDefinitions)
            {
                var typeId = ParseLogicalType(prop.TypeName);
                props.Add(new BoundColumnDefinition(prop.Name, typeId, prop.TypeName));
            }
            return new BoundCreateNodeTable(new BoundCreateTableInfo(nodeTable.Info.TableName, props));
        }
        else if (createTable is CreateRelTable relTable)
        {
            var props = new List<BoundColumnDefinition>();
            foreach (var prop in relTable.Info.PropertyDefinitions)
            {
                var typeId = ParseLogicalType(prop.TypeName);
                props.Add(new BoundColumnDefinition(prop.Name, typeId, prop.TypeName));
            }

            var srcEntry = _catalog.GetTableEntry(relTable.SrcTableName);
            if (srcEntry == null) throw new InvalidOperationException($"Table {relTable.SrcTableName} does not exist.");
            
            var dstEntry = _catalog.GetTableEntry(relTable.DstTableName);
            if (dstEntry == null) throw new InvalidOperationException($"Table {relTable.DstTableName} does not exist.");

            return new BoundCreateRelTable(new BoundCreateTableInfo(relTable.Info.TableName, props), srcEntry.TableID, dstEntry.TableID);
        }
        throw new NotSupportedException("Only CreateNodeTable and CreateRelTable are supported.");
    }

    private BoundDropTable BindDropTable(DropTable dropTable)
    {
        var entry = _catalog.GetTableEntry(dropTable.TableName);
        if (entry == null) throw new InvalidOperationException($"Table {dropTable.TableName} does not exist.");
        return new BoundDropTable(entry.TableID);
    }

    private BoundAlterTable BindAlterTable(AlterTable alterTable)
    {
        var entry = _catalog.GetTableEntry(alterTable.TableName);
        if (entry == null) throw new InvalidOperationException($"Table {alterTable.TableName} does not exist.");
        return new BoundAlterTable(entry.TableID);
    }

    private static LogicalTypeID ParseLogicalType(string typeName)
        => DeclaredTypeDescriptor.Parse(typeName).RuntimeType;

    private BoundStandaloneCall BindStandaloneCall(StandaloneCall call)
    {
        var option = _extensionOptionResolver?.Invoke(call.OptionName);
        if (option == null)
            throw new InvalidOperationException($"Invalid option name: {call.OptionName}.");

        var boundValue = ExpressionBinder.BindExpression(call.OptionValue);
        ExpressionBinder.ValidateExpectedDataType(boundValue, option.Type);
        return new BoundStandaloneCall(call.OptionName, boundValue, option.Type);
    }

    private BoundStandaloneCallFunction BindStandaloneCallFunction(StandaloneCallFunction call)
    {
        var boundExpr = ExpressionBinder.BindExpression(call.FunctionExpression);
        if (boundExpr is not BoundFunctionExpression boundFunc)
            throw new InvalidOperationException("CALL function must be a function invocation.");

        return new BoundStandaloneCallFunction(boundFunc);
    }

    private BoundCreateMacro BindCreateMacro(CreateMacro createMacro)
        => new BoundCreateMacro(createMacro.Name, createMacro.Parameters, createMacro.BodyExpression);

    private BoundAttachDatabase BindAttachDatabase(AttachDatabaseStatement statement)
    {
        if (string.IsNullOrWhiteSpace(statement.Path))
            throw new InvalidOperationException("ATTACH requires a non-empty database path.");
        if (string.IsNullOrWhiteSpace(statement.Alias))
            throw new InvalidOperationException("ATTACH requires a database alias.");
        if (string.IsNullOrWhiteSpace(statement.DbType))
            throw new InvalidOperationException("ATTACH requires a dbtype.");

        return new BoundAttachDatabase(statement.Path, statement.Alias, statement.DbType, statement.Options);
    }

    private BoundDetachDatabase BindDetachDatabase(DetachDatabaseStatement statement)
    {
        if (string.IsNullOrWhiteSpace(statement.DbName))
            throw new InvalidOperationException("DETACH requires a database name.");
        return new BoundDetachDatabase(statement.DbName);
    }

    private BoundUseDatabase BindUseDatabase(UseDatabaseStatement statement)
    {
        if (string.IsNullOrWhiteSpace(statement.DbName))
            throw new InvalidOperationException("USE requires a database name.");
        return new BoundUseDatabase(statement.DbName);
    }

    private BoundExtensionStatement BindExtensionStatement(ExtensionStatement statement)
        => new(statement);

    private BoundExplain BindExplain(ExplainStatement statement)
        => new(Bind(statement.StatementToExplain), statement.ExplainType);

    // ─── QUERY ─────────────────────────────────────────────────────────────────

    public BoundRegularQuery BindQuery(RegularQuery query)
        => BindQuery(query, preserveInitialScope: false);

    internal BoundRegularQuery BindQuery(RegularQuery query, bool preserveInitialScope)
    {
        var bound = new BoundRegularQuery();
        bound.SetIsProfile(query.GetIsProfile());
        for (int i = 0; i < query.GetNumSingleQueries(); i++)
        {
            if (i > 0 || !preserveInitialScope)
                Scope.Clear();
            var normalizedSingle = BindSingleQuery(query.GetSingleQuery(i));
            bound.AddSingleQuery(normalizedSingle, i > 0 && query.GetIsUnionAll(i - 1));
        }
        return bound;
    }

    private NormalizedSingleQuery BindSingleQuery(SingleQuery singleQuery)
    {
        var normalized = new NormalizedSingleQuery();

        for (int i = 0; i < singleQuery.GetNumReadingClauses(); i++)
        {
            var clause = BindReadingClause(singleQuery.GetReadingClause(i));
            normalized.AddReadingClause(clause);
        }

        for (int i = 0; i < singleQuery.GetNumUpdatingClauses(); i++)
        {
            var clause = BindUpdatingClause(singleQuery.GetUpdatingClause(i));
            normalized.AddUpdatingClause(clause);
        }

        if (singleQuery.HasReturnClause())
        {
            var boundReturn = BindReturnClause(singleQuery.GetReturnClause());
            normalized.SetReturnClause(boundReturn);
        }

        return normalized;
    }

    // ─── Reading Clauses ───────────────────────────────────────────────────────

    public BoundReadingClause BindReadingClause(ReadingClause clause)
    {
        return clause.ClauseType switch
        {
            ClauseType.MATCH or ClauseType.OPTIONAL_MATCH => BindMatchClause((MatchClause)clause),
            ClauseType.WITH => BindWithClause((WithClause)clause),
            ClauseType.UNWIND => BindUnwindClause((UnwindClause)clause),
            ClauseType.IN_QUERY_CALL => BindInQueryCall((InQueryCallClause)clause),
            ClauseType.CALL_SUBQUERY => BindCallSubquery((ParsedCallSubquery)clause),
            _ => throw new InvalidOperationException($"Internal error: unreachable reading clause branch: {clause.ClauseType}"),
        };
    }

    private BoundInQueryCall BindInQueryCall(InQueryCallClause clause)
    {
        var boundFuncExpr = ExpressionBinder.BindExpression(clause.FunctionExpression);
        
        var outVars = new List<Expression>();
        foreach (var yieldVar in clause.YieldVariables)
        {
            // The yieldVar is stored as ParsedVariableExpression natively
            var parsedVar = (ParsedVariableExpression)yieldVar;
            var boundVar = new VariableExpression(parsedVar.VariableName, LogicalTypeID.ANY);
            outVars.Add(boundVar);
            Scope.AddExpression(parsedVar.VariableName, boundVar);
        }

        Expression? wherePredicate = null;
        if (clause.WherePredicate != null)
        {
            wherePredicate = ExpressionBinder.BindExpression(clause.WherePredicate);
            ExpressionBinder.ValidateExpectedDataType(wherePredicate, LogicalTypeID.BOOL);
        }

        return new BoundInQueryCall(boundFuncExpr, outVars, wherePredicate);
    }

    private BoundWithClause BindWithClause(WithClause withClause)
    {
        var boundBody = BindProjectionBody(withClause.ProjectionBody);
        
        // WITH limits the trailing scope. The WHERE clause depends on the projected variables.
        Scope.Clear();
        foreach (var item in boundBody.ProjectionItems)
        {
            // WITH projects a new scope. Any computed scalar alias must resolve from the
            // materialized projection slot rather than re-binding to the original expression,
            // because the original variables may be out of scope after aggregation/projection.
            // Preserve direct node/relationship pass-through aliases so property access like
            // `WITH n AS m RETURN m.age` still carries the underlying graph binding.
            var scopeExpr =
                item.Expression is VariableExpression variableExpr &&
                (variableExpr.QueryNode != null ||
                 variableExpr.QueryRel != null ||
                 variableExpr.DataType == LogicalTypeID.NODE ||
                 variableExpr.DataType == LogicalTypeID.REL)
                    ? item.Expression
                    : new VariableExpression(item.ColumnName, item.Expression.DataType);
            Scope.AddExpression(item.ColumnName, scopeExpr);
        }

        Expression? wherePredicate = null;
        if (withClause.WherePredicate != null)
        {
            wherePredicate = ExpressionBinder.BindExpression(withClause.WherePredicate);
            ExpressionBinder.ValidateExpectedDataType(wherePredicate, LogicalTypeID.BOOL);
        }

        return new BoundWithClause(boundBody, wherePredicate);
    }

    private BoundUnwindClause BindUnwindClause(UnwindClause unwindClause)
    {
        var expr = ExpressionBinder.BindExpression(unwindClause.Expression);
        // Register the alias as a scalar variable placeholder — PhysicalUnwind populates it
        // at runtime via context.CurrentScalarBindings[alias] = currentElement.
        var aliasVar = new VariableExpression(unwindClause.Alias, Common.LogicalTypeID.ANY);
        Scope.AddExpression(unwindClause.Alias, aliasVar);
        return new BoundUnwindClause(expr, unwindClause.Alias);
    }

    private BoundCallSubquery BindCallSubquery(ParsedCallSubquery callSubquery)
    {
        // Save the current outer scope so we can detect correlation
        var outerScopeNames = new HashSet<string>(Scope.GetAllNames(), StringComparer.OrdinalIgnoreCase);

        // Detect correlated variables: if the inner query's first SingleQuery starts
        // with a WITH clause that references outer-scope variables, those are correlations.
        var correlatedVars = new List<string>();
        var correlatedVarInfos = new List<CorrelatedVarInfo>();
        var innerQuery = callSubquery.InnerQuery;

        if (innerQuery.GetNumSingleQueries() > 0)
        {
            var firstSingle = innerQuery.GetSingleQuery(0);
            if (firstSingle.GetNumReadingClauses() > 0 &&
                firstSingle.GetReadingClause(0) is WithClause leadingWith)
            {
                // The leading WITH clause imports outer variables
                foreach (var expr in leadingWith.ProjectionBody.ProjectionExpressions)
                {
                    // Simple variable reference — the variable name is the correlation
                    if (expr is ParsedVariableExpression pve &&
                        outerScopeNames.Contains(pve.VariableName))
                    {
                        correlatedVars.Add(pve.VariableName);

                        // Resolve metadata: node label and primary key property name
                        var varInfo = ResolveCorrelatedVarInfo(pve.VariableName);
                        correlatedVarInfos.Add(varInfo);
                    }
                }
            }
        }

        // Bind the inner query with the current scope preserved (for correlated access)
        var boundInner = BindQuery(innerQuery, preserveInitialScope: true);

        // Collect output column names from the inner query's RETURN clause
        var outputColumnNames = new List<string>();
        if (boundInner.GetNumSingleQueries() > 0)
        {
            var sq = boundInner.GetSingleQuery(0);
            if (sq.HasReturnClause())
            {
                foreach (var item in sq.ReturnClause!.Items)
                {
                    outputColumnNames.Add(item.ColumnName);
                    // Register output columns in the outer scope for downstream clauses
                    var outVar = new VariableExpression(item.ColumnName, item.Expression.DataType);
                    Scope.AddExpression(item.ColumnName, outVar);
                }
            }
        }

        if (outputColumnNames.Count == 0)
            throw new InvalidOperationException(
                "CALL subquery must have a RETURN clause that produces at least one column.");

        return new BoundCallSubquery(
            boundInner, correlatedVars, correlatedVarInfos,
            outputColumnNames, callSubquery.InnerQueryText);
    }

    /// <summary>
    /// Resolves a correlated variable's node label and primary key property name
    /// from the current scope and catalog.
    /// </summary>
    private CorrelatedVarInfo ResolveCorrelatedVarInfo(string varName)
    {
        // Try to find the QueryNode for this variable in scope
        if (Scope.Contains(varName))
        {
            var scopeExpr = Scope.GetExpression(varName);
            if (scopeExpr is VariableExpression ve && ve.QueryNode != null)
            {
                var qn = ve.QueryNode;
                // Get the first table name to determine label and PK
                if (qn.TableNames.Count > 0)
                {
                    var label = qn.TableNames[0];
                    var pkName = "id"; // fallback default

                    var tableEntry = _catalog.GetTableEntry(label);
                    if (tableEntry is Catalog.NodeTableCatalogEntry nodeEntry)
                    {
                        var props = nodeEntry.GetProperties().ToList();
                        if (nodeEntry.PrimaryKeyPropertyID < props.Count)
                        {
                            pkName = props[(int)nodeEntry.PrimaryKeyPropertyID].ColumnDef.Name;
                        }
                    }

                    return new CorrelatedVarInfo(varName, label, pkName);
                }
            }
        }

        // Fallback: unknown label/PK — will need heuristic at execution time
        return new CorrelatedVarInfo(varName, "", "id");
    }


    private BoundMatchClause BindMatchClause(MatchClause match)
    {
        var graph = new QueryGraph();

        for (int i = 0; i < match.GetNumPatternElements(); i++)
        {
            var element = match.GetPatternElement(i);
            var patternPart = new QueryPatternPart();
            var currentNode = BindQueryNode(element.GetFirstNodePattern(), graph);
            patternPart.AddNode(currentNode);
            for (var chainIdx = 0; chainIdx < element.GetNumPatternElementChains(); chainIdx++)
            {
                var chain = element.GetPatternElementChain(chainIdx);
                var nextNode = BindQueryNode(chain.NodePattern, graph);
                var queryRel = BindQueryRel(chain.RelPattern, currentNode, nextNode, graph);
                patternPart.AddRel(queryRel);
                patternPart.AddNode(nextNode);
                currentNode = nextNode;
            }

            if (element.HasPathName())
            {
                var pathVariableName = element.GetPathName();
                patternPart.SetPathVariableName(pathVariableName);
                if (patternPart.GetNumRels() == 1)
                {
                    patternPart.GetRel(0).PathVariableName = pathVariableName;
                }

                Scope.AddExpression(pathVariableName, new VariableExpression(pathVariableName, LogicalTypeID.ANY));
            }

            graph.AddPatternPart(patternPart);
        }

        Expression? wherePredicate = null;
        if (match.HasWherePredicate())
        {
            wherePredicate = ExpressionBinder.BindExpression(match.GetWherePredicate());
            ExpressionBinder.ValidateExpectedDataType(wherePredicate, LogicalTypeID.BOOL);
        }

        return new BoundMatchClause(graph, wherePredicate, match.ClauseType);
    }

    // ─── Updating Clauses ──────────────────────────────────────────────────────

    public BoundUpdatingClause BindUpdatingClause(UpdatingClause clause)
    {
        return clause.ClauseType switch
        {
            ClauseType.CREATE => BindCreateClause((CreateClause)clause),
            ClauseType.MERGE => BindMergeClause((MergeClause)clause),
            ClauseType.DELETE => BindDeleteClause((DeleteClause)clause),
            ClauseType.SET => BindSetClause((SetClause)clause),
            _ => throw new InvalidOperationException($"Internal error: unreachable updating clause branch: {clause.ClauseType}"),
        };
    }

    private BoundCreateClause BindCreateClause(CreateClause create)
    {
        var graph = new QueryGraph();
        for (int i = 0; i < create.PatternElements.Count; i++)
        {
            var element = create.PatternElements[i];
            var currentNode = BindQueryNode(element.GetFirstNodePattern(), graph);
            for (var chainIdx = 0; chainIdx < element.GetNumPatternElementChains(); chainIdx++)
            {
                var chain = element.GetPatternElementChain(chainIdx);
                var nextNode = BindQueryNode(chain.NodePattern, graph);
                var queryRel = BindQueryRel(chain.RelPattern, currentNode, nextNode, graph);
                currentNode = nextNode;
            }
        }
        
        var createNodes = new List<QueryNode>();
        for (int i = 0; i < graph.GetNumQueryNodes(); i++) createNodes.Add(graph.GetQueryNode(i));
        var createRels = new List<QueryRel>();
        for (int i = 0; i < graph.GetNumQueryRels(); i++) createRels.Add(graph.GetQueryRel(i));
        
        return new BoundCreateClause(createNodes, createRels);
    }

    private BoundMergeClause BindMergeClause(MergeClause merge)
    {
        var graph = new QueryGraph();
        for (int i = 0; i < merge.PatternElements.Count; i++)
        {
            var element = merge.PatternElements[i];
            var currentNode = BindQueryNode(element.GetFirstNodePattern(), graph);
            for (var chainIdx = 0; chainIdx < element.GetNumPatternElementChains(); chainIdx++)
            {
                var chain = element.GetPatternElementChain(chainIdx);
                var nextNode = BindQueryNode(chain.NodePattern, graph);
                BindQueryRel(chain.RelPattern, currentNode, nextNode, graph);
                currentNode = nextNode;
                currentNode = nextNode;
            }
        }

        var actions = new List<BoundMergeAction>();
        foreach (var action in merge.Actions)
        {
            var boundSet = BindSetClause(action.SetClause);
            actions.Add(new BoundMergeAction(action.IsOnMatch, boundSet));
        }

        return new BoundMergeClause(graph, actions);
    }

    private BoundDeleteClause BindDeleteClause(DeleteClause deleteClause)
    {
        var exprs = new List<Expression>();
        foreach (var parsed in deleteClause.Expressions)
        {
            exprs.Add(ExpressionBinder.BindExpression(parsed));
        }
        return new BoundDeleteClause(exprs);
    }

    private BoundSetClause BindSetClause(SetClause setClause)
    {
        var items = new List<Expression>();
        foreach (var parsed in setClause.SetItems)
        {
            var boundItem = ExpressionBinder.BindExpression(parsed);
            if (boundItem is BoundComparisonExpression comp && boundItem.ExpressionType == ExpressionType.EQUALS)
            {
                if (comp.Left is PropertyExpression property)
                {
                    ExpressionBinder.ValidateExpectedDataType(comp.Right, property.DataType);
                }
                else if (comp.Left is VariableExpression variable &&
                    (variable.QueryNode != null || variable.QueryRel != null ||
                     variable.DataType == LogicalTypeID.NODE || variable.DataType == LogicalTypeID.REL))
                {
                    // SET n = $props and SET r = $props replace the visible property map.
                }
                else
                {
                    throw new InvalidOperationException("Left hand side of SET must be a property expression or bound node/relationship variable.");
                }
            }
            items.Add(boundItem);
        }
        return new BoundSetClause(items);
    }

    private QueryNode BindQueryNode(NodePattern nodePattern, QueryGraph graph)
    {
        var varName = nodePattern.VariableName;

        // Already in scope — re-use the existing bound node (same variable in multiple patterns)
        if (!string.IsNullOrEmpty(varName) && Scope.Contains(varName))
        {
            var existing = Scope.GetExpression(varName);
            if (existing is VariableExpression varExpr && varExpr.QueryNode != null)
            {
                var qn = varExpr.QueryNode;
                bool found = false;
                for (int i = 0; i < graph.GetNumQueryNodes(); i++)
                {
                    if (graph.GetQueryNode(i).VariableName == varName)
                    {
                        found = true;
                        break;
                    }
                }
                if (!found) graph.AddQueryNode(qn);
                return qn;
            }
        }

        // Bind all associated table names
        var tableNames = new List<string>(nodePattern.TableNames);
        if (tableNames.Count == 0)
        {
            foreach (var entry in _catalog.GetNodeTableEntries())
                tableNames.Add(entry.Name);
            if (tableNames.Count == 0)
                throw new InvalidOperationException("No node tables exist in the catalog.");
        }
        else
        {
            // Validate every explicitly specified label exists in the catalog
            foreach (var tableName in tableNames)
            {
                if (_catalog.GetTableEntry(tableName) == null)
                    throw new InvalidOperationException(
                        $"Node table '{tableName}' does not exist in the catalog.");
            }
        }

        // Build property list from catalog across all mapped tables
        var properties = BuildPropertyExpressions(varName, tableNames);

        var inlineProperties = new List<(string Key, Expression Value)>();
        foreach (var kvp in nodePattern.PropertyKeyValues)
        {
            var boundExpr = ExpressionBinder.BindExpression(kvp.Value);
            
            // Phase 8: Strict Semantic Validation -> Type check against property definitions.
            LogicalTypeID? commonType = null;
            bool foundProperty = false;
            foreach (var tableName in tableNames)
            {
                var entry = _catalog.GetTableEntry(tableName);
                if (entry == null) continue;
                foreach (var prop in entry.GetProperties())
                {
                    if (prop.ColumnDef.Name == kvp.Key)
                    {
                        foundProperty = true;
                        commonType = prop.ColumnDef.Type;
                        break;
                    }
                }
            }
            if (foundProperty && commonType.HasValue)
            {
                ExpressionBinder.ValidateExpectedDataType(boundExpr, commonType.Value);
            }

            inlineProperties.Add((kvp.Key, boundExpr));
        }

        var inlinePropertyBag = nodePattern.PropertyBagExpression == null
            ? null
            : ExpressionBinder.BindExpression(nodePattern.PropertyBagExpression);

        var uniqueName = GetUniqueExpressionName(varName);
        var queryNode = new QueryNode(varName, uniqueName, tableNames, properties, inlineProperties, inlinePropertyBag);

        graph.AddQueryNode(queryNode);

        // Register in scope so subsequent references resolve correctly
        if (!string.IsNullOrEmpty(varName))
        {
            // Wrap in a VariableExpression that the ExpressionBinder can locate
            var nodeVar = new VariableExpression(varName, LogicalTypeID.ANY);
            nodeVar.QueryNode = queryNode;
            Scope.AddExpression(varName, nodeVar);
        }

        return queryNode;
    }

    private QueryRel BindQueryRel(RelPattern relPattern, QueryNode srcNode, QueryNode dstNode, QueryGraph graph)
    {
        var relName = relPattern.VariableName;
        if (!string.IsNullOrEmpty(relName) && Scope.Contains(relName))
        {
            var existing = Scope.GetExpression(relName);
            if (existing is VariableExpression varExpr && varExpr.QueryRel != null)
            {
                var qr = varExpr.QueryRel;
                bool found = false;
                for (int i = 0; i < graph.GetNumQueryRels(); i++)
                {
                    if (graph.GetQueryRel(i).VariableName == relName)
                    {
                        found = true;
                        break;
                    }
                }
                if (!found) graph.AddQueryRel(qr);
                return qr;
            }
        }

        var tableNames = new List<string>(relPattern.RelTypes);
        if (tableNames.Count == 0)
        {
            foreach (var entry in _catalog.GetRelTableEntries())
                tableNames.Add(entry.Name);
            if (tableNames.Count == 0)
                throw new InvalidOperationException("No relationship tables exist in the catalog.");
        }
        
        // If empty, parse property bindings against ALL relationships in catalog, or leave loosely bound context.
        var properties = new List<PropertyExpression>();
        
        if (tableNames.Count > 0)
        {
            foreach (var tableName in tableNames)
            {
                var entry = _catalog.GetTableEntry(tableName);
                if (entry == null)
                {
                    throw new InvalidOperationException($"Relationship table '{tableName}' does not exist.");
                }
                
                foreach (var prop in BuildPropertyExpressionsForEntry(relPattern.VariableName, entry))
                {
                    // Basic distinct addition stub logic 
                    if (!properties.Exists(p => p.PropertyName == prop.PropertyName))
                    {
                        properties.Add(prop);
                    }
                }
            }
        }
        else
        {
            // Label-less relationship -> (a)-[]->(b)
            // Leave properties loosely bound as empty for now, Planner resolves physical fallback
        }

        var inlineProperties = new List<(string Key, Expression Value)>();
        foreach (var kvp in relPattern.PropertyKeyValues)
        {
            var boundExpr = ExpressionBinder.BindExpression(kvp.Value);
            
            LogicalTypeID? commonType = null;
            bool foundProperty = false;
            foreach (var tableName in tableNames)
            {
                var entry = _catalog.GetTableEntry(tableName);
                if (entry == null) continue;
                foreach (var prop in entry.GetProperties())
                {
                    if (prop.ColumnDef.Name == kvp.Key)
                    {
                        foundProperty = true;
                        commonType = prop.ColumnDef.Type;
                        break;
                    }
                }
            }
            if (foundProperty && commonType.HasValue)
            {
                ExpressionBinder.ValidateExpectedDataType(boundExpr, commonType.Value);
            }

            inlineProperties.Add((kvp.Key, boundExpr));
        }

        var inlinePropertyBag = relPattern.PropertyBagExpression == null
            ? null
            : ExpressionBinder.BindExpression(relPattern.PropertyBagExpression);

        var isRecursive = relPattern.LowerBound != "1" || relPattern.UpperBound != "1";
        var allowedConnections = BuildAllowedConnections(tableNames, srcNode, dstNode, relPattern.Direction, isRecursive);
        var queryRel = new QueryRel(
            relPattern.VariableName,
            tableNames,
            allowedConnections,
            relPattern.Direction,
            srcNode,
            dstNode,
            properties,
            inlineProperties,
            inlinePropertyBag,
            relPattern.LowerBound,
            relPattern.UpperBound);
        graph.AddQueryRel(queryRel);

        if (!string.IsNullOrEmpty(relPattern.VariableName))
        {
            var relVar = new VariableExpression(relPattern.VariableName, LogicalTypeID.REL)
            {
                QueryRel = queryRel
            };
            Scope.AddExpression(relPattern.VariableName, relVar);
        }

        if (relPattern.RecursiveFilter != null && !string.IsNullOrEmpty(relPattern.RecursiveRelVariable))
            BindPerHopComprehension(relPattern, queryRel, tableNames, dstNode);

        return queryRel;
    }

    // Binds a variable-length per-hop comprehension filter -[r:REL*lo..hi (rr, nn | WHERE …)]-> by
    // introducing temporary scope entries for the intermediate edge (rr) and node (nn) variables, binding
    // the predicate against them, then restoring scope. The bound predicate + variable names are stored on
    // the QueryRel and evaluated per hop inside RecursiveExtend to prune non-matching edges.
    private void BindPerHopComprehension(RelPattern relPattern, QueryRel queryRel, List<string> tableNames, QueryNode dstNode)
    {
        var relVarName = relPattern.RecursiveRelVariable!;
        var nodeVarName = relPattern.RecursiveNodeVariable;

        var relProps = BuildPropertyExpressions(relVarName, tableNames.ToList());
        var innerRel = new QueryRel(
            relVarName, tableNames.ToList(), queryRel.AllowedConnections.ToList(), queryRel.Direction,
            queryRel.SrcNode, queryRel.DstNode, relProps, new List<(string, Expression)>());
        var relVarExpr = new VariableExpression(relVarName, LogicalTypeID.REL) { QueryRel = innerRel };

        VariableExpression? nodeVarExpr = null;
        if (!string.IsNullOrEmpty(nodeVarName))
        {
            var nodeTableNames = dstNode.TableNames.ToList();
            var nodeProps = BuildPropertyExpressions(nodeVarName!, nodeTableNames);
            var innerNode = new QueryNode(nodeVarName!, nodeVarName!, nodeTableNames, nodeProps, new List<(string, Expression)>());
            nodeVarExpr = new VariableExpression(nodeVarName!, LogicalTypeID.NODE) { QueryNode = innerNode };
        }

        var savedRel = PushScopedExpression(relVarName, relVarExpr);
        var pushedNode = nodeVarExpr != null;
        var savedNode = pushedNode ? PushScopedExpression(nodeVarName!, nodeVarExpr!) : null;
        try
        {
            queryRel.PerHopPredicate = ExpressionBinder.BindExpression(relPattern.RecursiveFilter!);
            queryRel.PerHopRelVariable = relVarName;
            queryRel.PerHopNodeVariable = nodeVarName;
        }
        finally
        {
            if (pushedNode)
                PopScopedExpression(nodeVarName!, savedNode);
            PopScopedExpression(relVarName, savedRel);
        }
    }

    // Adds a temporary binding for <paramref name="name"/>, returning any prior binding it shadowed.
    private Expression? PushScopedExpression(string name, Expression expression)
    {
        Expression? prior = null;
        if (Scope.Contains(name))
        {
            prior = Scope.GetExpression(name);
            Scope.RemoveExpression(name);
        }
        Scope.AddExpression(name, expression);
        return prior;
    }

    private void PopScopedExpression(string name, Expression? prior)
    {
        Scope.RemoveExpression(name);
        if (prior != null)
            Scope.AddExpression(name, prior);
    }

    private List<QueryRelConnection> BuildAllowedConnections(
        List<string> tableNames,
        QueryNode srcNode,
        QueryNode dstNode,
        ArrowDirection direction,
        bool isRecursive)
    {
        var allowed = new List<QueryRelConnection>();
        var seen = new HashSet<QueryRelConnection>();

        foreach (var tableName in tableNames)
        {
            if (_catalog.GetTableEntry(tableName) is not BogDb.Core.Catalog.RelGroupCatalogEntry relEntry)
                continue;

            foreach (var connection in relEntry.GetConnections())
            {
                var orientedSrc = direction == ArrowDirection.LEFT ? connection.DstTableName : connection.SrcTableName;
                var orientedDst = direction == ArrowDirection.LEFT ? connection.SrcTableName : connection.DstTableName;

                if (!isRecursive &&
                    srcNode.TableNames.Count > 0 &&
                    !srcNode.TableNames.Contains(orientedSrc, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!isRecursive &&
                    dstNode.TableNames.Count > 0 &&
                    !dstNode.TableNames.Contains(orientedDst, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var candidate = new QueryRelConnection(tableName, orientedSrc, orientedDst);
                if (seen.Add(candidate))
                    allowed.Add(candidate);

                if (direction != ArrowDirection.BOTH)
                    continue;

                var reverseCandidate = new QueryRelConnection(tableName, connection.DstTableName, connection.SrcTableName);
                if (!isRecursive &&
                    srcNode.TableNames.Count > 0 &&
                    !srcNode.TableNames.Contains(reverseCandidate.SrcTableName, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!isRecursive &&
                    dstNode.TableNames.Count > 0 &&
                    !dstNode.TableNames.Contains(reverseCandidate.DstTableName, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (seen.Add(reverseCandidate))
                    allowed.Add(reverseCandidate);
            }
        }

        return allowed;
    }

    private List<PropertyExpression> BuildPropertyExpressions(string varName, List<string> tableNames)
    {
        var result = new List<PropertyExpression>();
        if (tableNames.Count == 0) return result;

        var seenProperties = new HashSet<string>();

        foreach (var tableName in tableNames)
        {
            var entry = _catalog.GetTableEntry(tableName);
            if (entry == null) continue;

            foreach (var prop in entry.GetProperties())
            {
                if (seenProperties.Add(prop.ColumnDef.Name))
                {
                    var typeId = prop.ColumnDef.Type;
                    result.Add(new PropertyExpression(
                        prop.ColumnDef.Name,
                        varName,
                        typeId,
                        tableNames.Count == 1 ? tableName : null));
                }
            }
        }

        return result;
    }

    private List<PropertyExpression> BuildPropertyExpressionsForEntry(string varName, TableCatalogEntry entry)
    {
        var result = new List<PropertyExpression>();
        foreach (var prop in entry.GetProperties())
        {
            result.Add(new PropertyExpression(prop.ColumnDef.Name, varName, prop.ColumnDef.Type, entry.Name));
        }
        return result;
    }

    // ─── RETURN ────────────────────────────────────────────────────────────────

    private BoundReturnClause BindReturnClause(ReturnClause returnClause)
        => new BoundReturnClause(BindProjectionBody(returnClause.ProjectionBody));

    private BoundProjectionBody BindProjectionBody(ProjectionBody body)
    {
        var items = new List<BoundProjectionItem>();

        foreach (var expr in body.ProjectionExpressions)
        {
            if (expr is ParsedStarExpression)
            {
                var expressions = Scope.GetAllExpressions().ToList();
                if (expressions.Count == 0)
                {
                    // Empty scope — this is valid after CALL fn() without YIELD.
                    // The Planner will omit the Projection node, and the ResultCollector
                    // reads rows directly from the PhysicalTableFunctionCall output.
                    continue;
                }
                foreach (var (name, e) in expressions)
                {
                    items.Add(new BoundProjectionItem(e, name));
                }
            }
            else
            {
                var boundExpr = ExpressionBinder.BindExpression(expr);
                var columnName = expr.HasAlias() ? expr.GetAlias() : expr.GetRawName();
                items.Add(new BoundProjectionItem(boundExpr, columnName));
            }
        }

        var orderByElements = new List<BoundOrderByElement>();

        // Cypher §4.3.3: ORDER BY can reference aliases defined in the same RETURN/WITH projection.
        // We temporarily add the projection aliases to scope, bind each ORDER BY expression,
        // then remove the temporarily added names so the binder scope stays clean.
        var tempAliasesAdded = new List<string>();
        foreach (var item in items)
        {
            if (!Scope.Contains(item.ColumnName))
            {
                Scope.AddExpression(item.ColumnName, item.Expression);
                tempAliasesAdded.Add(item.ColumnName);
            }
        }

        foreach (var elem in body.OrderByElements)
        {
            var boundExpr = ExpressionBinder.BindExpression(elem.Expression);
            orderByElements.Add(new BoundOrderByElement(boundExpr, elem.IsAscending));
        }

        // Clean up temporarily added aliases — scope must not leak RETURN aliases into later clauses.
        foreach (var alias in tempAliasesAdded)
            Scope.RemoveExpression(alias);

        Expression? skipExpr = null;
        if (body.SkipExpression != null)
        {
            skipExpr = ExpressionBinder.BindExpression(body.SkipExpression);
            ExpressionBinder.ValidateExpectedDataType(skipExpr, LogicalTypeID.INT64);
            if (skipExpr is not LiteralExpression && skipExpr is not BoundParameterExpression)
                throw new InvalidOperationException("The expression in a SKIP clause must evaluate to a constant or a parameter.");
        }

        Expression? limitExpr = null;
        if (body.LimitExpression != null)
        {
            limitExpr = ExpressionBinder.BindExpression(body.LimitExpression);
            ExpressionBinder.ValidateExpectedDataType(limitExpr, LogicalTypeID.INT64);
            if (limitExpr is not LiteralExpression && limitExpr is not BoundParameterExpression)
                throw new InvalidOperationException("The expression in a LIMIT clause must evaluate to a constant or a parameter.");
        }

        if (body.IsDistinct && orderByElements.Count > 0)
        {
            foreach (var orderElem in orderByElements)
            {
                bool found = false;
                foreach (var item in items)
                {
                    if (ExpressionBinder.AreEquivalent(orderElem.Expression, item.Expression))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                    throw new InvalidOperationException("In a WITH/RETURN with DISTINCT, all expressions in ORDER BY must appear in the projection.");
            }
        }

        return new BoundProjectionBody(body.IsDistinct, items, orderByElements, skipExpr, limitExpr);
    }

    // ─── COPY FROM (pre-existing) ──────────────────────────────────────────────

    private BoundStatement BindCopyTo(CopyTo copyTo)
    {
        var boundQuery = BindQuery(copyTo.Query);
        if (boundQuery.GetNumSingleQueries() == 0 ||
            !boundQuery.GetSingleQuery(0).HasReturnClause())
        {
            throw new InvalidOperationException("COPY TO requires a query with a RETURN clause.");
        }

        var columnNames = boundQuery.GetSingleQuery(0).ReturnClause!.Items
            .Select(item => item.ColumnName)
            .ToList();
        return new BoundCopyTo(boundQuery, copyTo.FilePath, columnNames);
    }

    private BoundStatement BindCopyFrom(CopyFrom copyFrom)
    {
        var tableEntry = _catalog.GetTableEntry(copyFrom.TableName) as TableCatalogEntry
            ?? throw new InvalidOperationException($"Table '{copyFrom.TableName}' does not exist.");

        List<string> columnOrder;
        if (Processor.Operator.Persistent.CopyNode.IsParquetFile(copyFrom.FilePath))
        {
            columnOrder = ReadParquetColumnNames(copyFrom.FilePath);
        }
        else
        {
            columnOrder = _csvHeaderReader != null
                ? _csvHeaderReader(copyFrom.FilePath)
                : ReadCsvHeader(copyFrom.FilePath);
        }

        ValidateCopyFromColumns(tableEntry, columnOrder);
        return new BoundCopyFrom(copyFrom.TableName, copyFrom.FilePath, columnOrder);
    }

    private static List<string> ReadParquetColumnNames(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"COPY source file '{filePath}' does not exist.", filePath);

        using var stream = File.OpenRead(filePath);
        var schema = Parquet.ParquetReader.ReadSchemaAsync(stream).GetAwaiter().GetResult();
        var columns = schema.GetDataFields()
            .Select(f => f.Name)
            .ToList();

        if (columns.Count == 0)
            throw new InvalidOperationException($"COPY source file '{filePath}' has no columns.");

        return columns;
    }

    private static List<string> ReadCsvHeader(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"COPY source file '{filePath}' does not exist.", filePath);

        var headerLine = File.ReadLines(filePath).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(headerLine))
            throw new InvalidOperationException($"COPY source file '{filePath}' is empty.");

        var columns = headerLine
            .Split(',', StringSplitOptions.TrimEntries)
            .Select(column => column.Trim())
            .ToList();

        if (columns.Count == 0 || columns.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException($"COPY source file '{filePath}' has an invalid header row.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            if (!seen.Add(column))
                throw new InvalidOperationException($"COPY source file '{filePath}' contains duplicate column '{column}'.");
        }

        return columns;
    }

    private static void ValidateCopyFromColumns(TableCatalogEntry tableEntry, IReadOnlyList<string> columnOrder)
    {
        var actual = new HashSet<string>(columnOrder, StringComparer.OrdinalIgnoreCase);

        if (tableEntry is RelGroupCatalogEntry)
        {
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "from_id",
                "to_id"
            };
            foreach (var property in tableEntry.GetProperties())
                expected.Add(property.ColumnDef.Name);

            ValidateCopyHeaderMatches(tableEntry.Name, expected, actual);
            return;
        }

        if (tableEntry is NodeTableCatalogEntry)
        {
            var expected = new HashSet<string>(
                tableEntry.GetProperties().Select(property => property.ColumnDef.Name),
                StringComparer.OrdinalIgnoreCase);
            ValidateCopyHeaderMatches(tableEntry.Name, expected, actual);
            return;
        }

        throw new NotSupportedException($"COPY FROM is not supported for table '{tableEntry.Name}'.");
    }

    private static void ValidateCopyHeaderMatches(
        string tableName,
        HashSet<string> expected,
        HashSet<string> actual)
    {
        if (expected.SetEquals(actual))
            return;

        var missing = expected.Except(actual, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        var extra = actual.Except(expected, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();

        var details = new List<string>();
        if (missing.Count > 0)
            details.Add($"missing: {string.Join(", ", missing)}");
        if (extra.Count > 0)
            details.Add($"unexpected: {string.Join(", ", extra)}");

        throw new InvalidOperationException(
            $"COPY source columns do not match table '{tableName}' schema ({string.Join("; ", details)}).");
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────

    public string GetUniqueExpressionName(string name)
        => $"_{_lastExpressionId++}_{name}";
}
