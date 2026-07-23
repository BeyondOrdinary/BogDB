using System.Collections.Generic;

namespace BogDb.Core.Binder;

public readonly record struct QueryRelConnection(string TableName, string SrcTableName, string DstTableName);

/// <summary>
/// Bound MATCH clause: a resolved QueryGraph of nodes/rels + optional WHERE predicate.
/// </summary>
public class BoundMatchClause : BoundReadingClause
{
    public QueryGraph QueryGraph { get; }
    public Expression? WherePredicate { get; }

    public BoundMatchClause(QueryGraph graph, Expression? wherePredicate, Parser.ClauseType type = Parser.ClauseType.MATCH)
        : base(type)
    {
        QueryGraph = graph;
        WherePredicate = wherePredicate;
    }
}

/// <summary>
/// A collection of QueryNodes (and in future QueryRels) that make up a MATCH pattern.
/// </summary>
public class QueryGraph
{
    private readonly List<QueryNode> _nodes = new();
    private readonly List<QueryRel> _rels = new();
    private readonly List<QueryPatternPart> _patternParts = new();

    public void AddQueryNode(QueryNode node) => _nodes.Add(node);
    public void AddQueryRel(QueryRel rel) => _rels.Add(rel);
    public void AddPatternPart(QueryPatternPart part) => _patternParts.Add(part);
    public int GetNumQueryNodes() => _nodes.Count;
    public QueryNode GetQueryNode(int idx) => _nodes[idx];
    public IReadOnlyList<QueryNode> GetQueryNodes() => _nodes;
    public int GetNumQueryRels() => _rels.Count;
    public QueryRel GetQueryRel(int idx) => _rels[idx];
    public IReadOnlyList<QueryRel> GetQueryRels() => _rels;
    public int GetNumPatternParts() => _patternParts.Count;
    public QueryPatternPart GetPatternPart(int idx) => _patternParts[idx];
    public IReadOnlyList<QueryPatternPart> GetPatternParts() => _patternParts;
}

public class QueryPatternPart
{
    private readonly List<QueryNode> _nodes = new();
    private readonly List<QueryRel> _rels = new();
    private string? _pathVariableName;

    public void AddNode(QueryNode node) => _nodes.Add(node);
    public void AddRel(QueryRel rel) => _rels.Add(rel);
    public void SetPathVariableName(string pathVariableName) => _pathVariableName = pathVariableName;
    public int GetNumNodes() => _nodes.Count;
    public int GetNumRels() => _rels.Count;
    public QueryNode GetNode(int idx) => _nodes[idx];
    public QueryRel GetRel(int idx) => _rels[idx];
    public IReadOnlyList<QueryNode> GetNodes() => _nodes;
    public IReadOnlyList<QueryRel> GetRels() => _rels;
    public bool HasPathVariableName() => _pathVariableName != null;
    public string GetPathVariableName() => _pathVariableName!;
}

/// <summary>
/// A bound node variable resolved against the catalog.
/// Holds the table name, its property expressions, and its variable alias.
/// </summary>
public class QueryNode
{
    public string VariableName { get; }           // "n"
    public string UniqueName { get; }             // "_0_n"
    public IReadOnlyList<string> TableNames { get; }              // ["Person", "Admin"]
    public IReadOnlyList<PropertyExpression> PropertyExpressions { get; }
    public IReadOnlyList<(string Key, Expression Value)> InlineProperties { get; }
    public Expression? InlinePropertyBag { get; }

    public QueryNode(string variableName, string uniqueName, List<string> tableNames,
        List<PropertyExpression> properties, List<(string Key, Expression Value)> inlineProperties,
        Expression? inlinePropertyBag = null)
    {
        VariableName = variableName;
        UniqueName = uniqueName;
        TableNames = tableNames;
        PropertyExpressions = properties;
        InlineProperties = inlineProperties;
        InlinePropertyBag = inlinePropertyBag;
    }

    public PropertyExpression? GetPropertyExpression(string propName)
    {
        foreach (var p in PropertyExpressions)
            if (p.PropertyName == propName) return p;
        return null;
    }
}

public class QueryRel
{
    public string VariableName { get; }
    public IReadOnlyList<string> TableNames { get; }
    public IReadOnlyList<QueryRelConnection> AllowedConnections { get; }
    public Parser.ArrowDirection Direction { get; }
    public QueryNode SrcNode { get; }
    public QueryNode DstNode { get; }
    public IReadOnlyList<PropertyExpression> PropertyExpressions { get; }
    public IReadOnlyList<(string Key, Expression Value)> InlineProperties { get; }
    public Expression? InlinePropertyBag { get; }
    public string LowerBound { get; }
    public string UpperBound { get; }
    public string? PathVariableName { get; set; }

    // Per-hop comprehension predicate for variable-length traversal: bound WHERE over the intermediate
    // edge variable (PerHopRelVariable) and node variable (PerHopNodeVariable). Evaluated per edge inside
    // RecursiveExtend to prune non-matching hops. Null unless the pattern carries a (rr, nn | WHERE …) filter.
    public Expression? PerHopPredicate { get; set; }
    public string? PerHopRelVariable { get; set; }
    public string? PerHopNodeVariable { get; set; }

    public QueryRel(
        string variableName,
        List<string> tableNames,
        List<QueryRelConnection> allowedConnections,
        Parser.ArrowDirection direction,
        QueryNode srcNode,
        QueryNode dstNode,
        List<PropertyExpression> propertyExpressions,
        List<(string Key, Expression Value)> inlineProperties,
        Expression? inlinePropertyBag = null,
        string lowerBound = "1",
        string upperBound = "1",
        string? pathVariableName = null)
    {
        VariableName = variableName;
        TableNames = tableNames;
        AllowedConnections = allowedConnections;
        Direction = direction;
        SrcNode = srcNode;
        DstNode = dstNode;
        PropertyExpressions = propertyExpressions;
        InlineProperties = inlineProperties;
        InlinePropertyBag = inlinePropertyBag;
        LowerBound = lowerBound;
        UpperBound = upperBound;
        PathVariableName = pathVariableName;
    }

    public PropertyExpression? GetPropertyExpression(string propName)
    {
        foreach (var p in PropertyExpressions)
            if (p.PropertyName == propName) return p;
        return null;
    }

    public bool IsConnectionAllowed(string tableName, string srcTableName, string dstTableName)
    {
        if (AllowedConnections.Count == 0)
            return true;

        foreach (var connection in AllowedConnections)
        {
            if (!string.Equals(connection.TableName, tableName, System.StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.Equals(connection.SrcTableName, srcTableName, System.StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.Equals(connection.DstTableName, dstTableName, System.StringComparison.OrdinalIgnoreCase))
                continue;
            return true;
        }

        return false;
    }
}
