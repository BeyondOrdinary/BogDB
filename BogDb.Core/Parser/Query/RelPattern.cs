using System.Collections.Generic;

namespace BogDb.Core.Parser;

public enum ArrowDirection { LEFT, RIGHT, BOTH }

/// <summary>
/// A parsed relationship pattern like -[r:KNOWS]-> 
/// Minimal definition for Phase 8; recursive/weighted rels are Phase 9+.
/// </summary>
public class RelPattern
{
    public string VariableName { get; }
    public IReadOnlyList<string> RelTypes { get; }
    public ArrowDirection Direction { get; }
    public IReadOnlyList<(string Key, ParsedExpression Value)> PropertyKeyValues { get; }
    public ParsedExpression? PropertyBagExpression { get; }
    public string LowerBound { get; }
    public string UpperBound { get; }

    // Variable-length per-hop comprehension: -[r:REL*lo..hi (rr, nn | WHERE <predicate over rr/nn>)]->
    // rr is the intermediate edge variable, nn the intermediate node variable, and the filter (if present)
    // is evaluated per hop during traversal to prune non-matching edges. Null when no comprehension is used.
    public string? RecursiveRelVariable { get; }
    public string? RecursiveNodeVariable { get; }
    public ParsedExpression? RecursiveFilter { get; }

    public RelPattern(
        string variableName,
        List<string> relTypes,
        ArrowDirection direction,
        List<(string, ParsedExpression)> propertyKeyValues,
        ParsedExpression? propertyBagExpression = null,
        string lowerBound = "1",
        string upperBound = "1",
        string? recursiveRelVariable = null,
        string? recursiveNodeVariable = null,
        ParsedExpression? recursiveFilter = null)
    {
        VariableName = variableName;
        RelTypes = relTypes;
        Direction = direction;
        PropertyKeyValues = propertyKeyValues;
        PropertyBagExpression = propertyBagExpression;
        LowerBound = lowerBound;
        UpperBound = upperBound;
        RecursiveRelVariable = recursiveRelVariable;
        RecursiveNodeVariable = recursiveNodeVariable;
        RecursiveFilter = recursiveFilter;
    }
}
