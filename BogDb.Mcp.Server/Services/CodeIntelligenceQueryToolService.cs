using System.Globalization;
using System.Text.Json;

namespace BogDb.Mcp.Server.Services;

/// <summary>
/// Curated, schema-aware code-intelligence queries over a BO code graph
/// (the <c>.bo/graph</c> BogDB database produced by <c>bo index</c>, BoV01 schema).
/// These save agents from hand-writing Cypher against the BO node/edge tables — the
/// same convenience layer <see cref="OrchestrationQueryToolService"/> provides for the
/// orchestration graph, built on the read-only <see cref="BogDbQueryToolService"/>.
/// </summary>
public sealed class CodeIntelligenceQueryToolService
{
    private const int DefaultRowLimit = 100;
    private const int DefaultTimeoutMs = 10_000;

    private readonly BogDbQueryToolService _queryService;
    private readonly string _workspaceRoot;

    public CodeIntelligenceQueryToolService(BogDbQueryToolService queryService, string? workspaceRoot = null)
    {
        _queryService = queryService;
        _workspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(workspaceRoot);
    }

    /// <summary>
    /// The BO code graph defaults to <c>&lt;workspace&gt;/.bo/graph</c> — the agent's
    /// worktree at run time (bogdb-mcp inherits the agent CWD, or BO_WORKSPACE_ROOT).
    /// An explicit <c>databasePath</c> argument overrides it, so an agent can query a
    /// graph other than its own worktree's.
    /// </summary>
    private string ResolveDatabasePath(JsonElement arguments)
    {
        var explicitPath = JsonArgumentReader.GetOptionalString(arguments, "databasePath");
        return string.IsNullOrWhiteSpace(explicitPath)
            ? Path.Combine(_workspaceRoot, ".bo", "graph")
            : explicitPath;
    }

    /// <summary>code_symbol_search: find symbols by (case-insensitive) name substring, optional kind filter.</summary>
    public Task<QueryToolResult> SearchSymbolsAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var databasePath = ResolveDatabasePath(arguments);
        var query = JsonArgumentReader.GetRequiredString(arguments, "query");
        var kind = JsonArgumentReader.GetOptionalString(arguments, "kind");
        var rowLimit = JsonArgumentReader.GetOptionalInt32(arguments, "rowLimit") ?? DefaultRowLimit;
        var timeoutMs = JsonArgumentReader.GetOptionalInt32(arguments, "timeoutMs") ?? DefaultTimeoutMs;

        var where = "(toLower(s.qualified_name) CONTAINS toLower($q) OR toLower(s.display_name) CONTAINS toLower($q))";
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal) { ["q"] = query };
        if (!string.IsNullOrWhiteSpace(kind))
        {
            where += " AND s.kind = $kind";
            parameters["kind"] = kind;
        }

        // User input flows only through parameters; the Cypher string itself is fixed.
        var cypher = $$"""
            MATCH (s:Symbol)
            WHERE {{where}}
            OPTIONAL MATCH (f:File)-[:DEFINES_SYMBOL]->(s)
            RETURN
              s.qualified_name AS qualified_name,
              s.display_name AS display_name,
              s.kind AS kind,
              s.signature AS signature,
              s.declaration_line AS declaration_line,
              s.is_exported AS is_exported,
              f.normalized_path AS file
            ORDER BY qualified_name
            """;

        return _queryService.ExecuteAsync(databasePath, cypher, parameters, rowLimit, timeoutMs, cancellationToken);
    }

    /// <summary>
    /// code_dependencies: symbol-to-symbol dependency edges (CALLS / USES_TYPE / INSTANTIATES)
    /// for a symbol and/or the symbols declared in a file, in the requested direction(s).
    /// </summary>
    public async Task<QueryToolResult> QueryDependenciesAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var databasePath = ResolveDatabasePath(arguments);
        var symbol = JsonArgumentReader.GetOptionalString(arguments, "symbol");
        var file = JsonArgumentReader.GetOptionalString(arguments, "file");
        var direction = (JsonArgumentReader.GetOptionalString(arguments, "direction") ?? "both").ToLowerInvariant();
        var rowLimit = JsonArgumentReader.GetOptionalInt32(arguments, "rowLimit") ?? DefaultRowLimit;
        var timeoutMs = JsonArgumentReader.GetOptionalInt32(arguments, "timeoutMs") ?? DefaultTimeoutMs;

        if (direction is not ("in" or "out" or "both"))
            throw new InvalidOperationException("Argument 'direction' must be one of: 'in', 'out', 'both'.");
        if (string.IsNullOrWhiteSpace(symbol) && string.IsNullOrWhiteSpace(file))
            throw new InvalidOperationException("Provide at least one of 'symbol' or 'file'.");

        var wantOut = direction is "out" or "both";
        var wantIn = direction is "in" or "both";

        // (anchorClause, anchorParam) pairs select which symbol the dependency is anchored on.
        // Untyped (a:Symbol)-[r]->(b:Symbol) matches exactly CALLS/USES_TYPE/INSTANTIATES,
        // since those are the only Symbol->Symbol edges in BoV01.
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        long elapsed = 0;
        var truncated = false;

        async Task RunAsync(string anchor, string direction1, string matchPrefix, Dictionary<string, object?> parameters)
        {
            if (rows.Count >= rowLimit)
            {
                truncated = true;
                return;
            }

            var cypher = $$"""
                {{matchPrefix}}
                WHERE {{anchor}}
                RETURN
                  a.qualified_name AS from,
                  b.qualified_name AS to,
                  r.relation_type AS relation,
                  r.evidence AS evidence,
                  r.confidence AS confidence
                ORDER BY from, to
                """;

            var remaining = rowLimit - rows.Count;
            var result = await _queryService.ExecuteAsync(databasePath, cypher, parameters, remaining, timeoutMs, cancellationToken);
            if (!result.Success)
                throw new InvalidOperationException(result.Error ?? "code_dependencies query failed.");

            elapsed += result.ElapsedMs;
            truncated |= result.Truncated;
            foreach (var row in result.Rows)
            {
                var tagged = new Dictionary<string, object?>(row, StringComparer.Ordinal) { ["direction"] = direction1 };
                rows.Add(tagged);
            }
        }

        if (!string.IsNullOrWhiteSpace(symbol))
        {
            var p = new Dictionary<string, object?>(StringComparer.Ordinal) { ["q"] = symbol };
            if (wantOut) await RunAsync("toLower(a.qualified_name) CONTAINS toLower($q)", "out", "MATCH (a:Symbol)-[r]->(b:Symbol)", p);
            if (wantIn) await RunAsync("toLower(b.qualified_name) CONTAINS toLower($q)", "in", "MATCH (a:Symbol)-[r]->(b:Symbol)", p);
        }

        if (!string.IsNullOrWhiteSpace(file))
        {
            var p = new Dictionary<string, object?>(StringComparer.Ordinal) { ["file"] = file };
            if (wantOut) await RunAsync("toLower(fa.normalized_path) CONTAINS toLower($file)", "out", "MATCH (fa:File)-[:DEFINES_SYMBOL]->(a:Symbol)-[r]->(b:Symbol)", p);
            if (wantIn) await RunAsync("toLower(fb.normalized_path) CONTAINS toLower($file)", "in", "MATCH (a:Symbol)-[r]->(b:Symbol)<-[:DEFINES_SYMBOL]-(fb:File)", p);
        }

        return new QueryToolResult(
            Success: true,
            Columns: ["direction", "from", "to", "relation", "evidence", "confidence"],
            Rows: rows,
            RowCount: rows.Count,
            Truncated: truncated,
            ElapsedMs: elapsed,
            Error: null);
    }

    /// <summary>
    /// code_refactor_hotspots: files ranked by refactor pressure score (descending),
    /// with optional minimum-score and recommendation filters.
    /// </summary>
    public async Task<QueryToolResult> QueryRefactorHotspotsAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var databasePath = ResolveDatabasePath(arguments);
        var recommendation = JsonArgumentReader.GetOptionalString(arguments, "recommendation");
        var minScore = JsonArgumentReader.GetOptionalDouble(arguments, "minScore");
        var rowLimit = JsonArgumentReader.GetOptionalInt32(arguments, "rowLimit") ?? DefaultRowLimit;
        var timeoutMs = JsonArgumentReader.GetOptionalInt32(arguments, "timeoutMs") ?? DefaultTimeoutMs;

        const string cypher = """
            MATCH (f:File)-[:HAS_RPS]->(rps:RefactorPressureScore)
            RETURN
              f.normalized_path AS file,
              rps.score AS score,
              rps.recommendation AS recommendation,
              rps.drivers_json AS drivers,
              rps.fired_gates_json AS fired_gates
            """;

        // Scores are persisted as STRING (the BO write path stringifies all values), so
        // ordering and the minScore filter happen here, numerically — a lexical ORDER BY
        // in Cypher would rank "9.5" above "10.2".
        var result = await _queryService.ExecuteAsync(
            databasePath, cypher, parameters: null, rowLimit: int.MaxValue, timeoutMs, cancellationToken);
        if (!result.Success)
            return result;

        var ranked = result.Rows
            .Select(row => (row, score: ParseScore(row)))
            .Where(item => minScore is null || item.score >= minScore.Value)
            .Where(item => string.IsNullOrWhiteSpace(recommendation)
                || string.Equals(ReadString(item.row, "recommendation"), recommendation, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.score)
            .ThenBy(item => ReadString(item.row, "file"), StringComparer.Ordinal)
            .Take(rowLimit)
            .Select(item => item.row)
            .ToArray();

        return new QueryToolResult(
            Success: true,
            Columns: result.Columns,
            Rows: ranked,
            RowCount: ranked.Length,
            Truncated: result.Truncated || ranked.Length < result.Rows.Count,
            ElapsedMs: result.ElapsedMs,
            Error: null);
    }

    private static double ParseScore(IReadOnlyDictionary<string, object?> row)
        => double.TryParse(ReadString(row, "score"), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0d;

    private static string? ReadString(IReadOnlyDictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var value) ? value?.ToString() : null;
}
