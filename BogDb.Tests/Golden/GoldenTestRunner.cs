// Golden/GoldenTestRunner.cs
// Parses a corpus .cypher file and executes it against an in-memory BogDatabase.
//
// Corpus format:
//
//   -- SCHEMA
//   NODE <TableName>  <col1>:<type1>  <col2>:<type2>  ...
//   REL  <RelType>    FROM:<FromTable>  TO:<ToTable>  [<prop>:<prop_type> ...]
//
//   -- FIXTURE: <relpath>
//   (Optional, zero or more) declares a file under {corpusDir}/fixtures/<relpath>
//   to be copied into the temp directory BEFORE execution.
//   Use this for INPUT fixtures that queries read from (e.g. CSV files for COPY FROM).
//
//   -- SETUP
//   <Cypher DML statements, one per line, ending with ';'>
//
//   -- QUERY: <name>
//   <single RETURN statement or DML>
//
// Fixture path token:
//   {fixture:<relpath>}  resolves to an absolute path inside the corpus temp directory.
//
//   Declared paths (via -- FIXTURE:):   pre-seeded from fixtures/ before execution.
//   Undeclared paths (no -- FIXTURE:):  also resolved to the same temp dir, but NOT
//                                       pre-seeded. Use these for OUTPUT paths that
//                                       queries will create (e.g. COPY TO output files).
//                                       LOAD FROM can reference an undeclared path if
//                                       a COPY TO in an earlier query already created it.
//
//   There is no -- FIXTURE_OUT: directive; omitting the declaration is sufficient.
//   The temp dir is created fresh per corpus run and cleaned up afterward.
//
// The SCHEMA section is routed to EnsureNodeTable/EnsureRelTable (programmatic API).
// The SETUP section is routed to connection.Query() (DML: CREATE, MATCH+CREATE, etc.).
// The QUERY sections are executed and captured as GoldenQueryResult objects.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BogDb.Core.Common;
using BogDb.Core.Function.Sequence;
using BogDb.Core.Main;
using BogDb.Core.Main.QueryResult;
using BogDb.Extensions.Json;

namespace BogDb.Tests.Golden;

// ─── Schema model ─────────────────────────────────────────────────────────────

public sealed record SchemaNodeTable(string TableName, Dictionary<string, LogicalTypeID> Columns);
public sealed record SchemaRelTable(string RelType, string FromTable, string ToTable, Dictionary<string, LogicalTypeID> Properties);

public sealed record CorpusSchema(List<SchemaNodeTable> NodeTables, List<SchemaRelTable> RelTables);

// ─── Corpus model ─────────────────────────────────────────────────────────────

public sealed record CorpusQuery(string Name, string Cypher);

/// <summary>Relative paths (under fixtures/) declared by <c>-- FIXTURE: relpath</c> lines.</summary>
public sealed record CorpusDefinition(
    string CorpusName,
    CorpusSchema Schema,
    List<string> SetupStatements,
    List<CorpusQuery> Queries,
    List<string> FixturePaths,
    string CorpusDir);

// ─── Runner ───────────────────────────────────────────────────────────────────

public static class GoldenTestRunner
{
    private static readonly object CorpusRunLock = new();

    // ─── Type map ─────────────────────────────────────────────────────────

    private static readonly Dictionary<string, LogicalTypeID> TypeMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["INT64"]  = LogicalTypeID.INT64,
            ["INT32"]  = LogicalTypeID.INT32,
            ["INT16"]  = LogicalTypeID.INT16,
            ["INT8"]   = LogicalTypeID.INT8,
            ["UINT64"] = LogicalTypeID.UINT64,
            ["UINT32"] = LogicalTypeID.UINT32,
            ["UINT16"] = LogicalTypeID.UINT16,
            ["UINT8"]  = LogicalTypeID.UINT8,
            ["DOUBLE"] = LogicalTypeID.DOUBLE,
            ["FLOAT"]  = LogicalTypeID.FLOAT,
            ["BOOL"]   = LogicalTypeID.BOOL,
            ["BOOLEAN"]= LogicalTypeID.BOOL,
            ["STRING"] = LogicalTypeID.STRING,
            ["DATE"]   = LogicalTypeID.DATE,
            ["TIMESTAMP"] = LogicalTypeID.TIMESTAMP,
            ["TIMESTAMP_MS"] = LogicalTypeID.TIMESTAMP_MS,
            ["TIMESTAMP_NS"] = LogicalTypeID.TIMESTAMP_NS,
            ["TIMESTAMP_SEC"] = LogicalTypeID.TIMESTAMP_SEC,
        };

    private enum Section { None, Schema, Setup, Query, Fixture }

    // ─── Parser ───────────────────────────────────────────────────────────

    /// <summary>
    /// Parses a corpus .cypher file.
    /// SCHEMA → SchemaNodeTable/SchemaRelTable declarations.
    /// SETUP  → list of DML Cypher strings (split on ';').
    /// QUERY  → named CorpusQuery entries.
    /// </summary>
    public static CorpusDefinition ParseCorpus(string corpusPath)
    {
        var name       = Path.GetFileName(corpusPath);
        var corpusDir  = Path.GetDirectoryName(Path.GetFullPath(corpusPath)) ?? ".";
        var nodeTabls  = new List<SchemaNodeTable>();
        var relTabls   = new List<SchemaRelTable>();
        var setup      = new List<string>();
        var queries    = new List<CorpusQuery>();
        var fixtures   = new List<string>();    // relative paths under fixtures/

        var lines = File.ReadAllLines(corpusPath);

        var section      = Section.None;
        string? qName    = null;
        var qBuilder     = new StringBuilder();
        var setupBuf     = new StringBuilder();

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            // Section headers — FIXTURE is a header-only directive (no body lines)
            if (line.StartsWith("-- FIXTURE:", StringComparison.OrdinalIgnoreCase))
            {
                var relPath = line["-- FIXTURE:".Length..].Trim();
                if (!string.IsNullOrEmpty(relPath))
                    fixtures.Add(relPath);
                // Stay in current section; FIXTURE: is a pure declaration line
                continue;
            }
            if (line.StartsWith("-- SCHEMA", StringComparison.OrdinalIgnoreCase))
            {
                FlushQuery(queries, qName, qBuilder);
                qName = null;
                section = Section.Schema;
                continue;
            }
            if (line.StartsWith("-- SETUP", StringComparison.OrdinalIgnoreCase))
            {
                FlushQuery(queries, qName, qBuilder);
                qName = null;
                section = Section.Setup;
                continue;
            }
            if (line.StartsWith("-- QUERY:", StringComparison.OrdinalIgnoreCase))
            {
                FlushQuery(queries, qName, qBuilder);
                qName = line["-- QUERY:".Length..].Trim();
                qBuilder.Clear();
                section = Section.Query;
                continue;
            }

            // Skip remaining comment lines and blanks
            if (line.StartsWith("--") || string.IsNullOrWhiteSpace(line))
                continue;

            switch (section)
            {
                case Section.Schema:
                    ParseSchemaLine(line, nodeTabls, relTabls);
                    break;

                case Section.Setup:
                    setupBuf.Append(' ').Append(line);
                    FlushSetupBuffer(setupBuf, setup);
                    break;

                case Section.Query:
                    if (qBuilder.Length > 0) qBuilder.Append(' ');
                    qBuilder.Append(line);
                    break;
            }
        }
        FlushQuery(queries, qName, qBuilder);

        // Flush any remaining setup buffer content (no trailing ';')
        var rem = setupBuf.ToString().Trim();
        if (!string.IsNullOrEmpty(rem)) setup.Add(rem);

        return new CorpusDefinition(
            name,
            new CorpusSchema(nodeTabls, relTabls),
            setup,
            queries,
            fixtures,
            corpusDir);
    }

    private static void FlushSetupBuffer(StringBuilder buf, List<string> setup)
    {
        // Consume every ';'-terminated segment from the buffer
        var s = buf.ToString();
        int idx;
        while ((idx = s.IndexOf(';')) >= 0)
        {
            var stmt = s[..idx].Trim();
            if (!string.IsNullOrEmpty(stmt)) setup.Add(stmt);
            s = s[(idx + 1)..];
        }
        buf.Clear();
        buf.Append(s);
    }

    private static void ParseSchemaLine(
        string line,
        List<SchemaNodeTable> nodeTabls,
        List<SchemaRelTable> relTabls)
    {
        // NODE <TableName>  <col>:<type>  ...
        // REL  <RelType>    FROM:<FromTable>  TO:<ToTable>  [<prop>:<type> ...]
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return;

        var kind = parts[0].ToUpperInvariant();
        if (kind == "NODE")
        {
            var tableName = parts[1];
            var cols = new Dictionary<string, LogicalTypeID>(StringComparer.OrdinalIgnoreCase);
            for (int i = 2; i < parts.Length; i++)
                ParseColDef(parts[i], cols);
            nodeTabls.Add(new SchemaNodeTable(tableName, cols));
        }
        else if (kind == "REL")
        {
            var relType   = parts[1];
            string from = "", to = "";
            var props = new Dictionary<string, LogicalTypeID>(StringComparer.OrdinalIgnoreCase);
            for (int i = 2; i < parts.Length; i++)
            {
                var kv = parts[i].Split(':', 2);
                if (kv.Length != 2) continue;
                var key = kv[0];
                var val = kv[1];
                if (key.Equals("FROM", StringComparison.OrdinalIgnoreCase)) from = val;
                else if (key.Equals("TO", StringComparison.OrdinalIgnoreCase)) to = val;
                else ParseColDef(parts[i], props);
            }
            relTabls.Add(new SchemaRelTable(relType, from, to, props));
        }
    }

    private static void ParseColDef(string colDef, Dictionary<string, LogicalTypeID> target)
    {
        var kv = colDef.Split(':', 2);
        if (kv.Length != 2) return;
        var name = kv[0].Trim();
        var typStr = kv[1].Trim();
        if (TypeMap.TryGetValue(typStr, out var tid))
            target[name] = tid;
        // else: unknown type — skip silently; corpus should use known types
    }

    private static void FlushQuery(List<CorpusQuery> queries, string? name, StringBuilder cypher)
    {
        if (name == null) return;
        var text = cypher.ToString().Trim().TrimEnd(';');
        if (!string.IsNullOrEmpty(text))
            queries.Add(new CorpusQuery(name, text));
    }

    // ─── Corpus runner ────────────────────────────────────────────────────

    /// <summary>
    /// Runs an entire corpus against a fresh in-memory database.
    /// Fixture files declared via <c>-- FIXTURE: relpath</c> are copied from
    /// <c>{CorpusDir}/fixtures/relpath</c> to a temp directory. Every occurrence
    /// of <c>{fixture:relpath}</c> in SETUP and QUERY statements is replaced with
    /// the absolute temp path before execution. The temp directory is deleted on return.
    /// </summary>
    public static List<GoldenQueryResult> RunCorpus(CorpusDefinition corpus)
    {
        lock (CorpusRunLock)
        {
            SequenceFunctions.ResetAll();

            // ── Fixture provisioning ──────────────────────────────────────────────
            string? tempDir = null;
            Dictionary<string, string> fixtureMap = new(StringComparer.OrdinalIgnoreCase);

            if (corpus.FixturePaths.Count > 0 || HasFixtureTokens(corpus))
            {
                tempDir = Path.Combine(Path.GetTempPath(), $"bogdb_golden_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);

                var fixturesDir = Path.Combine(corpus.CorpusDir, "fixtures");
                foreach (var relPath in corpus.FixturePaths)
                {
                    var src  = Path.Combine(fixturesDir, relPath);
                    var dest = Path.Combine(tempDir, Path.GetFileName(relPath));
                    if (File.Exists(src))
                    {
                        File.Copy(src, dest, overwrite: true);
                        fixtureMap[relPath] = dest;
                    }
                    else
                    {
                        // If the fixture file is missing, record the expected path
                        // so queries still substitute (they will receive a bad path
                        // and the COPY will fail with a clear file-not-found error).
                        fixtureMap[relPath] = src;
                    }
                }

                // ── Auto-register undeclared {fixture:X} output tokens ──────────
                // Scan all queries and setup statements for {fixture:relpath} tokens
                // that were NOT declared with -- FIXTURE:. These are output paths
                // (e.g. created by COPY TO). Map them to {tempDir}/relname so that
                // LOAD FROM queries in a later step find them after they are written.
                foreach (var token in CollectAllFixtureTokens(corpus))
                {
                    if (!fixtureMap.ContainsKey(token))
                        fixtureMap[token] = Path.Combine(tempDir, Path.GetFileName(token));
                }
            }

            try
            {
                return RunCorpusInternal(corpus, fixtureMap, tempDir);
            }
            finally
            {
                if (tempDir != null && Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, recursive: true); }
                    catch { /* best-effort cleanup */ }
                }
            }
        }
    }

    private static List<GoldenQueryResult> RunCorpusInternal(
        CorpusDefinition corpus,
        Dictionary<string, string> fixtureMap,
        string? tempDir = null)
    {
        using var db   = BogDatabase.Open(":memory:");
        new JsonExtension().Load(db);
        using var conn = new BogConnection(db);

        // 1. Apply schema (programmatic API, inside a write tx)
        conn.BeginWriteTransaction();
        foreach (var nt in corpus.Schema.NodeTables)
            conn.EnsureNodeTable(nt.TableName, nt.Columns);
        foreach (var rt in corpus.Schema.RelTables)
            conn.EnsureRelTable(rt.RelType, rt.FromTable, rt.ToTable, rt.Properties);
        conn.Commit();

        // 2. Apply DML setup statements (Query API, inside a write tx)
        conn.BeginWriteTransaction();
        foreach (var rawStmt in corpus.SetupStatements)
        {
            if (string.IsNullOrWhiteSpace(rawStmt)) continue;
            var stmt = SubstituteFixtures(rawStmt, fixtureMap);
            var r = conn.Query(stmt);
            if (!r.IsSuccess)
                throw new InvalidOperationException(
                    $"[{corpus.CorpusName}] SETUP failed on: {stmt}\n{r.ErrorMessage}");
        }
        conn.Commit();

        // 3. Execute named queries (with fixture token substitution)
        return corpus.Queries
            .Select(q => RunQuery(conn, q with { Cypher = SubstituteFixtures(q.Cypher, fixtureMap) }, tempDir))
            .ToList();
    }

    // ─── Fixture helpers ──────────────────────────────────────────────────────

    private static readonly Regex FixtureTokenRegex =
        new(@"\{fixture:([^}]+)\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Returns true if any corpus query or setup statement has a {fixture:...} token.</summary>
    private static bool HasFixtureTokens(CorpusDefinition corpus)
    {
        return corpus.Queries.Any(q => FixtureTokenRegex.IsMatch(q.Cypher))
            || corpus.SetupStatements.Any(s => FixtureTokenRegex.IsMatch(s));
    }

    /// <summary>
    /// Collects all unique relpath values from {fixture:relpath} tokens across all
    /// queries and setup statements in the corpus.
    /// </summary>
    private static IEnumerable<string> CollectAllFixtureTokens(CorpusDefinition corpus)
    {
        var all = corpus.Queries.Select(q => q.Cypher)
                       .Concat(corpus.SetupStatements);
        return all.SelectMany(text =>
            FixtureTokenRegex.Matches(text)
                             .Cast<Match>()
                             .Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Replaces every <c>{fixture:relpath}</c> token in <paramref name="text"/> with
    /// the absolute path stored in <paramref name="fixtureMap"/>.
    /// </summary>
    private static string SubstituteFixtures(string text, Dictionary<string, string> fixtureMap)
    {
        if (fixtureMap.Count == 0) return text;
        foreach (var (relPath, absPath) in fixtureMap)
        {
            var token = $"{{fixture:{relPath}}}";
            // Forward-slash the path for BogDb compatibility on all platforms
            text = text.Replace(token, absPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }

    /// <summary>
    /// Replaces the GUID-based temp directory path in an error message with the
    /// stable placeholder <c>{fixture_dir}</c> so that golden snapshots of
    /// expected-error results are path-independent across runs.
    /// </summary>
    private static string NormalizeErrorMessage(string message, string? tempDir)
    {
        if (tempDir == null || string.IsNullOrEmpty(message))
            return message;
        // Replace both backslash and forward-slash variants
        return message
            .Replace(tempDir.Replace('\\', '/'), "{fixture_dir}", StringComparison.OrdinalIgnoreCase)
            .Replace(tempDir.Replace('/', '\\'), "{fixture_dir}", StringComparison.OrdinalIgnoreCase);
    }

    private static GoldenQueryResult RunQuery(BogConnection conn, CorpusQuery query, string? tempDir = null)
    {
        QueryResult qr;
        try { qr = conn.Query(query.Cypher); }
        catch (Exception ex)
        {
            return new GoldenQueryResult
            {
                Name = query.Name, Cypher = query.Cypher,
                IsSuccess = false, ErrorMessage = NormalizeErrorMessage(ex.Message, tempDir),
                ColumnNames = [], Rows = [],
            };
        }

        if (!qr.IsSuccess)
        {
            return new GoldenQueryResult
            {
                Name = query.Name, Cypher = query.Cypher,
                IsSuccess = false, ErrorMessage = NormalizeErrorMessage(qr.ErrorMessage ?? "(unknown error)", tempDir),
                ColumnNames = [], Rows = [],
            };
        }

        var colNames = qr.ColumnNames.ToList();
        var rows     = new List<List<string>>();

        while (qr.HasNext())
        {
            var row   = qr.GetNext();
            var cells = new List<string>();
            for (int i = 0; i < colNames.Count; i++)
                cells.Add(Normalize(row.GetValue(i)));
            rows.Add(cells);
        }

        // Canonical sort for queries that don't impose ORDER BY
        rows.Sort(RowComparer);

        return new GoldenQueryResult
        {
            Name        = query.Name,
            Cypher      = query.Cypher,
            IsSuccess   = true,
            ErrorMessage= null,
            ColumnNames = colNames,
            Rows        = rows,
        };
    }

    // ─── Normalization ────────────────────────────────────────────────────

    public static string Normalize(object? value)
    {
        if (value is null || value is DBNull) return "<null>";
        if (value is BogDbInterval interval) return TypeCoercionHelper.ToBogDbString(interval) ?? "<null>";
        if (value is double d) return NormalizeFloatingPoint(d, "G17");
        if (value is float  f) return NormalizeFloatingPoint(f, "G9");
        if (TryNormalizeNested(value, out var nested)) return nested;
        return value.ToString() ?? "<null>";
    }

    private static bool TryNormalizeNested(object value, out string normalized)
    {
        var canonical = CanonicalizeNestedValue(value);
        if (ReferenceEquals(canonical, value))
        {
            normalized = string.Empty;
            return false;
        }

        normalized = JsonSerializer.Serialize(canonical);
        return true;
    }

    private static object? CanonicalizeNestedValue(object? value)
    {
        if (value is null || value is DBNull) return null;

        if (value is BogDbInterval interval)
        {
            return TypeCoercionHelper.ToBogDbString(interval);
        }

        if (value is JsonElement element)
        {
            return CanonicalizeJsonElement(element);
        }

        if (value is IEnumerable<KeyValuePair<object?, object?>> kvPairs)
        {
            return kvPairs
                .OrderBy(kv => TypeCoercionHelper.ToBogDbString(kv.Key) ?? string.Empty, StringComparer.Ordinal)
                .ToDictionary(
                    kv => TypeCoercionHelper.ToBogDbString(kv.Key) ?? string.Empty,
                    kv => CanonicalizeNestedValue(kv.Value));
        }

        if (value is IEnumerable<KeyValuePair<string, object?>> stringPairs)
        {
            return stringPairs
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key, kv => CanonicalizeNestedValue(kv.Value));
        }

        if (value is IReadOnlyDictionary<string, object?> readOnlyDict)
        {
            return readOnlyDict
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key, kv => CanonicalizeNestedValue(kv.Value));
        }

        if (value is IDictionary<string, object?> dict)
        {
            return dict
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key, kv => CanonicalizeNestedValue(kv.Value));
        }

        if (value is System.Collections.IDictionary objDict)
        {
            return objDict.Keys
                .Cast<object?>()
                .Select(k => (Key: k?.ToString() ?? string.Empty, Value: objDict[k!]))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key, kv => CanonicalizeNestedValue(kv.Value));
        }

        if (value is System.Collections.IEnumerable seq && value is not string && value is not byte[])
        {
            return seq.Cast<object?>().Select(CanonicalizeNestedValue).ToList();
        }

        return value;
    }

    private static object? CanonicalizeJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ToDictionary(p => p.Name, p => CanonicalizeJsonElement(p.Value)),
            JsonValueKind.Array => element.EnumerateArray()
                .Select(CanonicalizeJsonElement)
                .ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l)
                ? l
                : element.TryGetDouble(out var d) ? d : element.GetRawText(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.GetRawText(),
        };
    }

    private static int RowComparer(List<string> a, List<string> b)
    {
        int len = Math.Min(a.Count, b.Count);
        for (int i = 0; i < len; i++)
        {
            int c = string.Compare(a[i], b[i], StringComparison.Ordinal);
            if (c != 0) return c;
        }
        return a.Count.CompareTo(b.Count);
    }

    // ─── Diff helper ──────────────────────────────────────────────────────

    public static List<string> Diff(GoldenQueryResult actual, GoldenQueryResult expected)
    {
        var diffs  = new List<string>();
        var prefix = $"[{expected.Name}]";

        if (actual.IsSuccess != expected.IsSuccess)
        {
            diffs.Add($"{prefix} IsSuccess: got {actual.IsSuccess}, want {expected.IsSuccess}");
            if (!actual.IsSuccess) diffs.Add($"{prefix} Error: {actual.ErrorMessage}");
            return diffs;
        }
        if (!actual.IsSuccess)
        {
            if (!string.Equals(
                    NormalizeLineEndings(actual.ErrorMessage),
                    NormalizeLineEndings(expected.ErrorMessage),
                    StringComparison.Ordinal))
            {
                diffs.Add($"{prefix} ErrorMessage: got [{actual.ErrorMessage}], want [{expected.ErrorMessage}]");
            }

            return diffs;
        }

        if (!actual.ColumnNames.SequenceEqual(expected.ColumnNames))
            diffs.Add($"{prefix} ColumnNames: got [{string.Join(", ", actual.ColumnNames)}], " +
                      $"want [{string.Join(", ", expected.ColumnNames)}]");

        if (actual.Rows.Count != expected.Rows.Count)
            diffs.Add($"{prefix} RowCount: got {actual.Rows.Count}, want {expected.Rows.Count}");

        int n = Math.Min(actual.Rows.Count, expected.Rows.Count);
        for (int r = 0; r < n; r++)
        {
            if (!RowsMatch(actual.Rows[r], expected.Rows[r]))
                diffs.Add($"{prefix} Row[{r}]: got [{string.Join(", ", actual.Rows[r])}], " +
                          $"want [{string.Join(", ", expected.Rows[r])}]");
        }

        return diffs;
    }

    private static bool RowsMatch(IReadOnlyList<string> actualRow, IReadOnlyList<string> expectedRow)
    {
        if (actualRow.Count != expectedRow.Count)
            return false;

        for (var i = 0; i < actualRow.Count; i++)
        {
            if (!string.Equals(
                    NormalizeGoldenText(actualRow[i]),
                    NormalizeGoldenText(expectedRow[i]),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeFloatingPoint(double value, string format)
    {
        if (double.IsNegativeInfinity(value)) return "-Infinity";
        if (double.IsPositiveInfinity(value)) return "Infinity";
        if (double.IsNaN(value)) return "NaN";
        return value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string NormalizeGoldenText(string? value)
    {
        return NormalizeLineEndings(value)
            .Replace("-∞", "-Infinity", StringComparison.Ordinal)
            .Replace("∞", "Infinity", StringComparison.Ordinal);
    }

    private static string NormalizeLineEndings(string? value)
        => (value ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);
}
