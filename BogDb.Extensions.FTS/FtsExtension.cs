using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BogDb.Core.Extension;
using BogDb.Core.Main;

namespace BogDb.Extensions.FTS;

/// <summary>
/// Persisted definition of an FTS index — enough to rebuild the inverted index from table data.
/// Only definitions are persisted (not the inverted index itself), mirroring the vector extension.
/// </summary>
internal sealed record FtsIndexDefinition(
    string TableName,
    string IndexName,
    IReadOnlyList<string> Properties,
    double K1,
    double B);

/// <summary>
/// Full-text search extension — C++ parity with bogdb-master/extension/fts.
/// Provides CREATE_FTS_INDEX, DROP_FTS_INDEX, QUERY_FTS_INDEX, REBUILD_FTS_INDEX table functions,
/// and STEM/TOKENIZE scalar functions.
/// </summary>
public class FtsExtension : IExtension
{
    public string Name => "fts";

    /// <summary>Registry of all live FTS indexes, keyed by index name.</summary>
    public Dictionary<string, FtsIndex> Indexes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Persisted index definitions, keyed by index name — used to rebuild on open and on staleness.</summary>
    internal Dictionary<string, FtsIndexDefinition> Definitions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Node-write fingerprint captured when each live index was last (re)built.</summary>
    internal Dictionary<string, long> Fingerprints { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Load(BogDatabase database)
    {
        // ── Table functions ──────────────────────────────────────────────
        var createFts = new CreateFtsIndexTableFunction(this, database);
        database.StandaloneTableFunctionRegistry.Register(createFts);

        var dropFts = new DropFtsIndexTableFunction(this, database);
        database.StandaloneTableFunctionRegistry.Register(dropFts);

        var queryFts = new QueryFtsIndexTableFunction(this, database);
        database.StandaloneTableFunctionRegistry.Register(queryFts);

        var rebuildFts = new RebuildFtsIndexTableFunction(this, database);
        database.StandaloneTableFunctionRegistry.Register(rebuildFts);

        // ── Scalar functions ─────────────────────────────────────────────
        database.ScalarFunctionRegistry.Register("stem", args =>
        {
            if (args.Length == 0 || args[0] is null) return null;
            return (object?)PorterStemmer.Stem(args[0]!.ToString()!.ToLowerInvariant());
        });

        database.ScalarFunctionRegistry.Register("tokenize", args =>
        {
            if (args.Length == 0 || args[0] is null) return null;
            var tokenizer = new FtsTokenizer(enableStemming: false);
            var tokens = tokenizer.Tokenize(args[0]!.ToString()!);
            return (object?)new List<object?>(tokens.Cast<object?>());
        });

        // Incrementally maintain live indexes as nodes change (commit-deferred; see INodeMutationListener),
        // so queries stay on the maintained index instead of triggering a rebuild after each write.
        database.RegisterNodeMutationListener(new FtsMutationListener(this, database));

        // Restore persisted definitions and inverted indexes (validated against the current committed row
        // count) so a reopen is a load, not a rebuild; register a checkpoint participant to write them back;
        // then rebuild from table data only for indexes that were NOT restored from disk (best-effort).
        LoadDefinitions(database);
        LoadIndexes(database);
        database.RegisterExtensionService("fts.persistence", new FtsPersistenceParticipant(this));
        RebuildAll(database);
    }

    /// <summary>Incrementally applies a committed node upsert to a live index (remove-then-add) and advances the fingerprint.</summary>
    internal void ApplyIncrementalUpsert(BogDatabase database, FtsIndexDefinition definition, long docId, string text)
    {
        if (!Indexes.TryGetValue(definition.IndexName, out var index))
            return;
        index.RemoveDocument(docId);
        index.AddDocument(docId, text);
        Fingerprints[definition.IndexName] = database.GetNodeWriteFingerprint(definition.TableName);
    }

    /// <summary>Incrementally applies a committed node delete to a live index and advances the fingerprint.</summary>
    internal void ApplyIncrementalDelete(BogDatabase database, FtsIndexDefinition definition, long docId)
    {
        if (!Indexes.TryGetValue(definition.IndexName, out var index))
            return;
        index.RemoveDocument(docId);
        Fingerprints[definition.IndexName] = database.GetNodeWriteFingerprint(definition.TableName);
    }

    /// <summary>
    /// (Re)builds the inverted index for a definition from current table data, records the staleness
    /// fingerprint, and registers both the definition and the live index. Shared by CREATE, REBUILD,
    /// rebuild-on-open, and the lazy rebuild in <see cref="EnsureFreshIndex"/>.
    /// </summary>
    internal FtsIndex BuildAndRegister(BogDatabase database, FtsIndexDefinition definition)
    {
        var index = FtsIndexBuilder.Build(database, definition);
        Definitions[definition.IndexName] = definition;
        Indexes[definition.IndexName] = index;
        Fingerprints[definition.IndexName] = database.GetNodeWriteFingerprint(definition.TableName);
        return index;
    }

    /// <summary>
    /// Returns a live, fresh inverted index, rebuilding it if it was only restored as a definition
    /// (e.g. after reopen) or if writes have landed on its table since it was last built. FTS has no
    /// cheap exact-scan fallback, so correctness after a write is preserved by rebuilding. Returns null
    /// for an unknown index (no live instance and no definition).
    /// </summary>
    internal FtsIndex? EnsureFreshIndex(BogDatabase database, string indexName)
    {
        if (!Definitions.TryGetValue(indexName, out var definition))
            return Indexes.TryGetValue(indexName, out var live) ? live : null;

        var current = database.GetNodeWriteFingerprint(definition.TableName);
        if (Indexes.TryGetValue(indexName, out var existing) &&
            Fingerprints.TryGetValue(indexName, out var built) && built == current)
            return existing;

        return BuildAndRegister(database, definition);
    }

    /// <summary>Removes an index (definition + live instance + fingerprint) and persists the change.</summary>
    internal bool Remove(BogDatabase database, string indexName)
    {
        var removed = Definitions.Remove(indexName);
        removed |= Indexes.Remove(indexName);
        Fingerprints.Remove(indexName);
        if (removed)
            SaveDefinitions(database);
        return removed;
    }

    /// <summary>Best-effort rebuild of every persisted index on open; failures defer to lazy rebuild on query.</summary>
    internal void RebuildAll(BogDatabase database)
    {
        foreach (var definition in Definitions.Values.ToList())
        {
            // Skip indexes already restored from disk by LoadIndexes — only rebuild the rest.
            if (Indexes.ContainsKey(definition.IndexName))
                continue;
            try { BuildAndRegister(database, definition); }
            catch { /* Left for lazy rebuild on the first QUERY_FTS_INDEX call. */ }
        }
    }

    private const int IndexFileVersion = 1;

    /// <summary>
    /// Persists every fresh (fingerprint-current) inverted index at checkpoint, each stamped with the table's
    /// committed row count so a reopen can tell whether it still matches the data. Stale indexes are skipped.
    /// </summary>
    internal void PersistIndexes(BogDatabase database)
    {
        var path = GetIndexDataPath(database);
        if (path == null)
            return;

        var fresh = new List<(FtsIndexDefinition Definition, FtsIndex Index, long Stamp)>();
        foreach (var definition in Definitions.Values)
        {
            if (!Indexes.TryGetValue(definition.IndexName, out var index))
                continue;
            if (!Fingerprints.TryGetValue(definition.IndexName, out var built) ||
                built != database.GetNodeWriteFingerprint(definition.TableName))
                continue; // stale — rebuilt on open

            fresh.Add((definition, index, database.EnumerateNodeRows(definition.TableName).Count()));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);
        writer.Write(IndexFileVersion);
        writer.Write(fresh.Count);
        foreach (var (definition, index, stamp) in fresh)
        {
            writer.Write(definition.IndexName);
            writer.Write(definition.TableName);
            writer.Write(stamp);
            index.WriteTo(writer);
        }
    }

    /// <summary>
    /// Restores persisted inverted indexes on open, but only when the persisted row-count stamp still matches
    /// the table's current committed row count. A mismatched or definition-less index is discarded, leaving it
    /// to <see cref="RebuildAll"/>.
    /// </summary>
    internal void LoadIndexes(BogDatabase database)
    {
        var path = GetIndexDataPath(database);
        if (path == null || !File.Exists(path))
            return;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);
        if (stream.Length == 0 || reader.ReadInt32() != IndexFileVersion)
            return;

        var count = reader.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var indexName = reader.ReadString();
            var tableName = reader.ReadString();
            var stamp = reader.ReadInt64();

            if (Definitions.TryGetValue(indexName, out var definition))
            {
                var index = FtsIndex.ReadFrom(reader, definition.IndexName, definition.TableName,
                    definition.Properties, new FtsTokenizer(), definition.K1, definition.B);
                try
                {
                    if (database.EnumerateNodeRows(tableName).Count() == stamp)
                    {
                        Indexes[indexName] = index;
                        Fingerprints[indexName] = database.GetNodeWriteFingerprint(tableName);
                    }
                }
                catch
                {
                    // Table data not ready / unreadable → leave for rebuild on open or first query.
                }
            }
            else
            {
                // Orphaned index (no matching definition): consume its bytes to keep the reader aligned.
                FtsIndex.ReadFrom(reader, string.Empty, string.Empty, Array.Empty<string>(), new FtsTokenizer(), 1.2, 0.75);
            }
        }
    }

    internal void SaveDefinitions(BogDatabase database)
    {
        var path = GetMetadataPath(database);
        if (path == null)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var ordered = Definitions.Values
            .OrderBy(definition => definition.TableName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(definition => definition.IndexName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        File.WriteAllText(path, JsonSerializer.Serialize(ordered));
    }

    internal void LoadDefinitions(BogDatabase database)
    {
        var path = GetMetadataPath(database);
        if (path == null || !File.Exists(path))
            return;

        var definitions = JsonSerializer.Deserialize<List<FtsIndexDefinition>>(File.ReadAllText(path));
        if (definitions == null)
            return;

        foreach (var definition in definitions)
            Definitions[definition.IndexName] = definition;
    }

    private static string? GetMetadataPath(BogDatabase database)
    {
        if (string.Equals(database.DatabasePath, ":memory:", StringComparison.OrdinalIgnoreCase))
            return null;

        return Path.Combine(database.DatabasePath, "extensions", "fts.indexes.json");
    }

    private static string? GetIndexDataPath(BogDatabase database)
    {
        if (string.Equals(database.DatabasePath, ":memory:", StringComparison.OrdinalIgnoreCase))
            return null;

        return Path.Combine(database.DatabasePath, "extensions", "fts.indexes.data.bin");
    }
}

/// <summary>
/// Writes all fresh inverted indexes to disk when the database checkpoints (including on clean shutdown),
/// so they can be restored on the next open instead of rebuilt.
/// </summary>
internal sealed class FtsPersistenceParticipant : BogDb.Core.Extension.IDatabasePersistenceParticipant
{
    private readonly FtsExtension _ext;

    public FtsPersistenceParticipant(FtsExtension ext) => _ext = ext;

    public void Persist(BogDatabase database) => _ext.PersistIndexes(database);
}

/// <summary>
/// Builds an <see cref="FtsIndex"/> from a definition by scanning its table's current rows, using the
/// same id(n)/property scan as CREATE_FTS_INDEX so every rebuild produces identically-keyed documents.
/// </summary>
internal static class FtsIndexBuilder
{
    public static FtsIndex Build(BogDatabase database, FtsIndexDefinition definition)
    {
        var index = new FtsIndex(
            definition.IndexName,
            definition.TableName,
            definition.Properties,
            new FtsTokenizer(),
            definition.K1,
            definition.B);

        using var conn = new BogConnection(database);
        var propExpressions = string.Join(", ", definition.Properties.Select(p => $"n.{p}"));
        var result = conn.Query($"MATCH (n:{definition.TableName}) RETURN id(n) AS _id, {propExpressions}");
        if (!result.IsSuccess)
            throw new InvalidOperationException(result.ErrorMessage);

        while (result.HasNext())
        {
            var row = result.GetNext();
            long docId = Convert.ToInt64(row.GetValue(0));

            var textParts = new List<string>();
            for (int i = 1; i <= definition.Properties.Count; i++)
            {
                var val = row.GetValue(i);
                if (val != null) textParts.Add(val.ToString()!);
            }

            index.AddDocument(docId, string.Join(" ", textParts));
        }

        return index;
    }
}

/// <summary>
/// Listens for node upserts/deletes and incrementally maintains any live FTS index on the affected table.
/// Deltas are registered as commit-deferred actions on the transaction, applied at commit and discarded on
/// rollback. Documents are keyed by the node's numeric id (id(n) == primary key), consistent with rebuild.
/// </summary>
internal sealed class FtsMutationListener : INodeMutationListener
{
    private readonly FtsExtension _ext;
    private readonly BogDatabase _db;

    public FtsMutationListener(FtsExtension ext, BogDatabase db)
    {
        _ext = ext;
        _db = db;
    }

    public void OnNodeMutation(
        BogDb.Core.Transaction.Transaction transaction,
        string tableName,
        object nodeId,
        NodeMutationKind kind,
        IReadOnlyDictionary<string, object>? properties)
    {
        // FTS keys documents by the node's numeric id (id(n) == primary key). A non-numeric key can't be
        // indexed by FTS — skip (CREATE would have failed the same way), leaving the fingerprint to rebuild.
        long docId;
        try { docId = Convert.ToInt64(nodeId); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return; }

        foreach (var definition in _ext.Definitions.Values)
        {
            if (!string.Equals(definition.TableName, tableName, StringComparison.OrdinalIgnoreCase))
                continue;
            // Only maintain indexes that already exist live; unbuilt ones are rebuilt lazily on query.
            if (!_ext.Indexes.ContainsKey(definition.IndexName))
                continue;

            if (kind == NodeMutationKind.Delete)
            {
                transaction.TrackVersionedAction(
                    BogDb.Core.Storage.UndoRecordType.UPDATE_INFO,
                    () => _ext.ApplyIncrementalDelete(_db, definition, docId),
                    () => { });
                continue;
            }

            // Upsert: capture the concatenated indexed text now so a rollback simply never applies it.
            var text = BuildText(definition, properties);
            transaction.TrackVersionedAction(
                BogDb.Core.Storage.UndoRecordType.UPDATE_INFO,
                () => _ext.ApplyIncrementalUpsert(_db, definition, docId, text),
                () => { });
        }
    }

    private static string BuildText(FtsIndexDefinition definition, IReadOnlyDictionary<string, object>? properties)
    {
        if (properties == null)
            return string.Empty;

        var parts = new List<string>();
        foreach (var prop in definition.Properties)
            if (properties.TryGetValue(prop, out var value) && value != null)
                parts.Add(value.ToString()!);
        return string.Join(" ", parts);
    }
}

// ── CREATE_FTS_INDEX table function ──────────────────────────────────────────

internal sealed class CreateFtsIndexTableFunction : ITableFunction
{
    private readonly FtsExtension _ext;
    private readonly BogDatabase _db;

    public CreateFtsIndexTableFunction(FtsExtension ext, BogDatabase db)
    {
        _ext = ext;
        _db = db;
    }

    public string Name => "CREATE_FTS_INDEX";

    public IEnumerable<Dictionary<string, object?>> Invoke(IReadOnlyList<object?> args)
    {
        // Expected: CREATE_FTS_INDEX('table_name', 'index_name', ['prop1', 'prop2', ...] [, k1 := 1.5] [, b := 0.75])
        // Separate positional arguments from optional named BM25 parameters (k1, b).
        var positional = new List<object?>();
        double k1 = 1.2, b = 0.75;
        foreach (var arg in args)
        {
            if (arg is not NamedFunctionArgument named)
            {
                positional.Add(arg);
                continue;
            }

            switch (named.Name.ToLowerInvariant())
            {
                case "k1":
                    if (!TryToDouble(named.Value, out k1)) { yield return Error("k1 must be a number"); yield break; }
                    break;
                case "b":
                    if (!TryToDouble(named.Value, out b)) { yield return Error("b must be a number"); yield break; }
                    break;
                default:
                    yield return Error($"unrecognized optional parameter '{named.Name}' (expected k1 or b)");
                    yield break;
            }
        }

        if (positional.Count < 3)
        {
            yield return Error("CREATE_FTS_INDEX requires (table_name, index_name, properties)");
            yield break;
        }
        if (double.IsNaN(k1) || k1 < 0)
        {
            yield return Error("k1 must be a number >= 0");
            yield break;
        }
        if (double.IsNaN(b) || b < 0 || b > 1)
        {
            yield return Error("b must be a number between 0 and 1");
            yield break;
        }

        var tableName  = positional[0]?.ToString() ?? "";
        var indexName  = positional[1]?.ToString() ?? "";
        var propsRaw   = positional[2];

        // Parse properties
        var props = new List<string>();
        if (propsRaw is IEnumerable<object?> list)
        {
            foreach (var p in list)
                if (p != null) props.Add(p.ToString()!);
        }
        else if (propsRaw is string propStr)
        {
            props.AddRange(propStr.Split(',').Select(s => s.Trim()));
        }

        if (props.Count == 0)
        {
            yield return Error("no properties specified for FTS index");
            yield break;
        }

        var definition = new FtsIndexDefinition(tableName, indexName, props, k1, b);

        // Build the inverted index from current table data. Scan failures (e.g. an invalid table or
        // property) are surfaced as a result-row error, matching the rest of this function.
        FtsIndex? index = null;
        string? buildError = null;
        try { index = _ext.BuildAndRegister(_db, definition); }
        catch (Exception ex) { buildError = ex.Message; }

        if (buildError != null)
        {
            yield return Error(buildError);
            yield break;
        }

        _ext.SaveDefinitions(_db);
        yield return new Dictionary<string, object?>
        {
            ["result"] = $"FTS index '{indexName}' created on {tableName}({string.Join(", ", props)}) with {index!.DocumentCount} documents, {index.TermCount} unique terms"
        };
    }

    private static Dictionary<string, object?> Error(string message)
        => new() { ["result"] = $"Error: {message}" };

    private static bool TryToDouble(object? value, out double result)
    {
        switch (value)
        {
            case double d: result = d; return true;
            case float f:  result = f; return true;
            case int i:    result = i; return true;
            case long l:   result = l; return true;
            case string s when double.TryParse(
                s,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed):
                result = parsed;
                return true;
            default:
                result = 0;
                return false;
        }
    }
}

// ── DROP_FTS_INDEX table function ────────────────────────────────────────────

internal sealed class DropFtsIndexTableFunction : ITableFunction
{
    private readonly FtsExtension _ext;
    private readonly BogDatabase _db;

    public DropFtsIndexTableFunction(FtsExtension ext, BogDatabase db)
    {
        _ext = ext;
        _db = db;
    }

    public string Name => "DROP_FTS_INDEX";

    public IEnumerable<Dictionary<string, object?>> Invoke(IReadOnlyList<object?> args)
    {
        if (args.Count < 1 || args[0] is null)
        {
            yield return new Dictionary<string, object?>
                { ["result"] = "Error: DROP_FTS_INDEX requires (index_name)" };
            yield break;
        }

        var indexName = args[0]!.ToString()!;
        yield return new Dictionary<string, object?>
        {
            ["result"] = _ext.Remove(_db, indexName)
                ? $"FTS index '{indexName}' dropped"
                : $"Error: FTS index '{indexName}' not found"
        };
    }
}

// ── QUERY_FTS_INDEX table function ───────────────────────────────────────────

internal sealed class QueryFtsIndexTableFunction : ITableFunction
{
    private readonly FtsExtension _ext;
    private readonly BogDatabase _db;

    public QueryFtsIndexTableFunction(FtsExtension ext, BogDatabase db)
    {
        _ext = ext;
        _db = db;
    }

    public string Name => "QUERY_FTS_INDEX";

    public IReadOnlyList<(string Name, string Type)>? Schema =>
        new[] { ("node_offset", "INT64"), ("score", "DOUBLE") };

    public IEnumerable<Dictionary<string, object?>> Invoke(IReadOnlyList<object?> args)
    {
        // Expected: QUERY_FTS_INDEX('index_name', 'search query')
        if (args.Count < 2 || args[0] is null || args[1] is null)
            yield break;

        var indexName = args[0]!.ToString()!;
        var queryText = args[1]!.ToString()!;

        // Rebuild lazily if the index was only restored as a definition (e.g. after reopen) or if the
        // table has been written to since the index was last built, so results reflect current data.
        var index = _ext.EnsureFreshIndex(_db, indexName);
        if (index == null)
            yield break;

        // Parse optional args (top_k from 3rd positional arg)
        int topK = 10;
        bool conjunctive = false;
        if (args.Count > 2 && args[2] != null)
        {
            try { topK = Convert.ToInt32(args[2]); } catch { /* ignore */ }
        }
        if (args.Count > 3 && args[3] != null)
        {
            var mode = args[3]?.ToString()?.ToLowerInvariant();
            conjunctive = mode == "conjunctive" || mode == "true";
        }

        var results = index.Query(queryText, topK, conjunctive);
        foreach (var (docId, score) in results)
        {
            yield return new Dictionary<string, object?>
            {
                ["node_offset"] = docId,
                ["score"] = Math.Round(score, 6)
            };
        }
    }
}

// ── REBUILD_FTS_INDEX table function ─────────────────────────────────────────

internal sealed class RebuildFtsIndexTableFunction : ITableFunction
{
    private readonly FtsExtension _ext;
    private readonly BogDatabase _db;

    public RebuildFtsIndexTableFunction(FtsExtension ext, BogDatabase db)
    {
        _ext = ext;
        _db = db;
    }

    public string Name => "REBUILD_FTS_INDEX";

    public IReadOnlyList<(string Name, string Type)>? Schema =>
        new[] { ("index", "STRING"), ("documents", "INT64"), ("terms", "INT64") };

    public IEnumerable<Dictionary<string, object?>> Invoke(IReadOnlyList<object?> args)
    {
        // REBUILD_FTS_INDEX('index_name') rebuilds one index; REBUILD_FTS_INDEX() rebuilds all.
        List<string> targets;
        if (args.Count >= 1 && args[0] != null)
        {
            var indexName = args[0]!.ToString()!;
            if (!_ext.Definitions.ContainsKey(indexName))
                throw new ArgumentException($"FTS index '{indexName}' not found.");
            targets = new List<string> { indexName };
        }
        else
        {
            targets = _ext.Definitions.Keys.ToList();
        }

        foreach (var indexName in targets)
        {
            var index = _ext.BuildAndRegister(_db, _ext.Definitions[indexName]);
            yield return new Dictionary<string, object?>
            {
                ["index"] = indexName,
                ["documents"] = (long)index.DocumentCount,
                ["terms"] = (long)index.TermCount
            };
        }
    }
}
