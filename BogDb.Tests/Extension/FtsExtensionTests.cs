using BogDb.Core.Main;
using BogDb.Core.Main.QueryResult;
using BogDb.Extensions.FTS;
using Xunit;

namespace BogDb.Tests.Extension;

/// <summary>
/// Tests for the FTS (full-text search) extension.
/// </summary>
[Trait("Category", "FtsExtension")]
public class FtsExtensionTests
{
    // ── FtsTokenizer unit tests ──────────────────────────────────────────

    [Fact]
    public void Tokenizer_SplitsAndLowercases()
    {
        var t = new FtsTokenizer(enableStemming: false);
        var tokens = t.Tokenize("Hello World");
        Assert.Equal(2, tokens.Count);
        Assert.Equal("hello", tokens[0]);
        Assert.Equal("world", tokens[1]);
    }

    [Fact]
    public void Tokenizer_RemovesStopWords()
    {
        var t = new FtsTokenizer(enableStemming: false);
        var tokens = t.Tokenize("the cat is on the mat");
        Assert.DoesNotContain("the", tokens);
        Assert.DoesNotContain("is", tokens);
        Assert.DoesNotContain("on", tokens);
        Assert.Contains("cat", tokens);
        Assert.Contains("mat", tokens);
    }

    [Fact]
    public void Tokenizer_StemsWords()
    {
        var t = new FtsTokenizer(enableStemming: true);
        var tokens = t.Tokenize("running jumps easily");
        // "running" → "run", "jumps" → "jump", "easily" → "easili"
        Assert.Contains("run", tokens);
        Assert.Contains("jump", tokens);
    }

    // ── FtsIndex unit tests ──────────────────────────────────────────────

    [Fact]
    public void FtsIndex_AddAndQuery()
    {
        var idx = new FtsIndex("test", "docs", new[] { "content" });
        idx.AddDocument(0, "the quick brown fox jumps over the lazy dog");
        idx.AddDocument(1, "a fast brown cat leaps over a sleeping hound");
        idx.AddDocument(2, "graph databases handle relationships efficiently");

        var results = idx.Query("brown fox", topK: 10);
        Assert.NotEmpty(results);
        // Doc 0 should rank first (both terms match)
        Assert.Equal(0, results[0].docId);
    }

    [Fact]
    public void FtsIndex_ConjunctiveMode()
    {
        var idx = new FtsIndex("test", "docs", new[] { "content" });
        idx.AddDocument(0, "apple orange banana");
        idx.AddDocument(1, "apple grape");
        idx.AddDocument(2, "orange grape cherry");

        // Conjunctive: both "apple" AND "orange" must match
        var results = idx.Query("apple orange", topK: 10, conjunctive: true);
        Assert.Single(results);
        Assert.Equal(0, results[0].docId);
    }

    [Fact]
    public void FtsIndex_BM25Scoring()
    {
        var idx = new FtsIndex("test", "docs", new[] { "content" });
        // Doc with repeated term should rank higher
        idx.AddDocument(0, "database optimization database tuning database performance");
        idx.AddDocument(1, "general article about software engineering");
        idx.AddDocument(2, "database tutorial for beginners");

        var results = idx.Query("database", topK: 3);
        Assert.True(results.Count >= 2);
        // Doc 0 has "database" 3 times, should rank first
        Assert.Equal(0, results[0].docId);
        Assert.True(results[0].score > results[1].score);
    }

    // ── Scalar functions ─────────────────────────────────────────────────

    [Fact]
    public void Stem_WorksViaQuery()
    {
        using var db = BogDatabase.CreateInMemory();
        new FtsExtension().Load(db);
        using var conn = new BogConnection(db);
        
        var r = conn.Query("RETURN stem('running') AS s");
        Assert.True(r.IsSuccess, r.ErrorMessage);
        Assert.Equal("run", r.GetNext().GetValue(0));
    }

    [Fact]
    public void Tokenize_ReturnsListViaQuery()
    {
        using var db = BogDatabase.CreateInMemory();
        new FtsExtension().Load(db);
        using var conn = new BogConnection(db);
        
        var r = conn.Query("RETURN tokenize('hello world test') AS tokens");
        Assert.True(r.IsSuccess, r.ErrorMessage);
        var val = r.GetNext().GetValue(0);
        var list = Assert.IsType<System.Collections.Generic.List<object?>>(val);
        Assert.Contains("hello", list.Cast<string>());
        Assert.Contains("world", list.Cast<string>());
        Assert.Contains("test", list.Cast<string>());
    }

    // ── Integration: create + query FTS index via Cypher ─────────────────

    [Fact]
    public void CreateAndQueryFtsIndex_EndToEnd()
    {
        using var db = BogDatabase.CreateInMemory();
        var fts = new FtsExtension();
        fts.Load(db);
        using var conn = new BogConnection(db);

        // Create table with text data
        var cr = conn.Query("CREATE NODE TABLE Article(id INT64 PRIMARY KEY, title STRING, body STRING)");
        Assert.True(cr.IsSuccess, cr.ErrorMessage);

        conn.Query("CREATE (:Article {id:1, title:'Graph Databases', body:'Graph databases use nodes and edges to model relationships between entities.'})");
        conn.Query("CREATE (:Article {id:2, title:'SQL Tutorial', body:'SQL is a standard language for accessing and manipulating databases.'})");
        conn.Query("CREATE (:Article {id:3, title:'Machine Learning', body:'Machine learning algorithms build mathematical models from training data.'})");
        conn.Query("CREATE (:Article {id:4, title:'Graph Algorithms', body:'Graph algorithms like PageRank and shortest path operate on graph structures.'})");

        // Create the FTS index programmatically (matches C++ CALL CREATE_FTS_INDEX pattern)
        var index = new FtsIndex("article_idx", "Article", new[] { "title", "body" });

        // Manually populate (in practice, the table function does this)
        var scanResult = conn.Query("MATCH (a:Article) RETURN a.id AS id, a.title AS t, a.body AS b ORDER BY a.id");
        Assert.True(scanResult.IsSuccess, scanResult.ErrorMessage);
        while (scanResult.HasNext())
        {
            var row = scanResult.GetNext();
            var id = System.Convert.ToInt64(row.GetValue(0));
            var text = $"{row.GetValue(1)} {row.GetValue(2)}";
            index.AddDocument(id, text);
        }
        fts.Indexes["article_idx"] = index;

        // Query the index
        var results = index.Query("graph database");
        Assert.NotEmpty(results);
        // Article 1 (graph databases) or Article 4 (graph algorithms) should be top
        Assert.True(results[0].docId == 1 || results[0].docId == 4);
        Assert.True(results[0].score > 0);
    }

    [Fact]
    public void FtsIndex_RemoveDocument()
    {
        var idx = new FtsIndex("test", "docs", new[] { "content" });
        idx.AddDocument(0, "apple orange");
        idx.AddDocument(1, "banana grape");

        Assert.Equal(2, idx.DocumentCount);
        idx.RemoveDocument(0);
        Assert.Equal(1, idx.DocumentCount);

        var results = idx.Query("apple");
        Assert.Empty(results);
    }

    // ── Configurable BM25 parameters (k1, b) ─────────────────────────────

    [Fact]
    public void FtsIndex_DefaultBm25Params_MatchStandardBm25()
    {
        var idx = new FtsIndex("test", "docs", new[] { "content" });
        Assert.Equal(1.2, idx.K1, 6);
        Assert.Equal(0.75, idx.B, 6);
    }

    [Fact]
    public void FtsIndex_CustomBm25Params_AreStoredAndChangeScoring()
    {
        static FtsIndex Build(double k1, double b)
        {
            var idx = new FtsIndex("test", "docs", new[] { "content" }, tokenizer: null, k1: k1, b: b);
            idx.AddDocument(0, "database database database database tuning");
            idx.AddDocument(1, "database tutorial for graph beginners");
            idx.AddDocument(2, "general article about software engineering craft");
            return idx;
        }

        var mempalace = Build(1.5, 0.75);
        Assert.Equal(1.5, mempalace.K1, 6);
        Assert.Equal(0.75, mempalace.B, 6);

        // k1 controls term-frequency saturation, so a different k1 must change the BM25 score
        // for a document whose term frequency is > 1.
        var defaultScore = Build(1.2, 0.75).Query("database", topK: 1)[0].score;
        var tunedScore = mempalace.Query("database", topK: 1)[0].score;
        Assert.NotEqual(defaultScore, tunedScore, precision: 9);
    }

    [Theory]
    [InlineData(-0.1, 0.75)]
    [InlineData(0.0, 1.5)]
    [InlineData(1.2, -0.1)]
    public void FtsIndex_InvalidBm25Params_Throw(double k1, double b)
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => new FtsIndex("test", "docs", new[] { "content" }, tokenizer: null, k1: k1, b: b));
    }

    [Fact]
    public void CreateFtsIndex_NamedArgs_ConfigureBm25()
    {
        using var db = BogDatabase.CreateInMemory();
        var fts = new FtsExtension();
        fts.Load(db);
        using var conn = new BogConnection(db);

        Assert.True(conn.Query("CREATE NODE TABLE Doc(id INT64 PRIMARY KEY, body STRING)").IsSuccess);
        Assert.True(conn.Query("CREATE (:Doc {id:1, body:'alpha beta gamma'})").IsSuccess);

        var create = conn.Query("CALL CREATE_FTS_INDEX('Doc', 'doc_idx', ['body'], k1 := 1.5, b := 0.5) RETURN *");
        Assert.True(create.IsSuccess, create.ErrorMessage);

        Assert.True(fts.Indexes.TryGetValue("doc_idx", out var idx));
        Assert.Equal(1.5, idx!.K1, 6);
        Assert.Equal(0.5, idx.B, 6);
    }

    [Fact]
    public void CreateFtsIndex_UnknownNamedArg_ReturnsError()
    {
        using var db = BogDatabase.CreateInMemory();
        var fts = new FtsExtension();
        fts.Load(db);
        using var conn = new BogConnection(db);

        Assert.True(conn.Query("CREATE NODE TABLE Doc(id INT64 PRIMARY KEY, body STRING)").IsSuccess);

        var bad = conn.Query("CALL CREATE_FTS_INDEX('Doc', 'doc_idx', ['body'], bogus := 1) RETURN *");
        Assert.True(bad.IsSuccess, bad.ErrorMessage);
        Assert.Contains(
            "unrecognized optional parameter",
            bad.GetNext().GetValue(0)!.ToString(),
            System.StringComparison.OrdinalIgnoreCase);
        Assert.False(fts.Indexes.ContainsKey("doc_idx"));
    }

    // ── Rebuild-on-open + staleness handling ─────────────────────────────

    [Fact]
    public void QueryFtsIndex_ReflectsInsertsAfterCreate_ViaAutoRebuild()
    {
        using var db = BogDatabase.CreateInMemory();
        var fts = new FtsExtension();
        fts.Load(db);
        using var conn = new BogConnection(db);

        Assert.True(conn.Query("CREATE NODE TABLE Doc(id INT64 PRIMARY KEY, body STRING)").IsSuccess);
        Assert.True(conn.Query("CREATE (:Doc {id:1, body:'alpha beta'})").IsSuccess);
        Assert.True(conn.Query("CALL CREATE_FTS_INDEX('Doc', 'doc_idx', ['body']) RETURN *").IsSuccess);

        // 'gamma' is not indexed yet.
        var before = conn.Query("CALL QUERY_FTS_INDEX('doc_idx', 'gamma') RETURN *");
        Assert.True(before.IsSuccess, before.ErrorMessage);
        Assert.False(before.HasNext());

        // Insert a document containing 'gamma' AFTER the index was built. The inverted index is now
        // stale; the query must reflect the new document (FTS rebuilds since it has no exact fallback).
        Assert.True(conn.Query("CREATE (:Doc {id:2, body:'gamma delta'})").IsSuccess);

        var after = conn.Query("CALL QUERY_FTS_INDEX('doc_idx', 'gamma') RETURN *");
        Assert.True(after.IsSuccess, after.ErrorMessage);
        Assert.True(after.HasNext());
    }

    [Fact]
    public void RebuildFtsIndex_ReindexesCurrentRows()
    {
        using var db = BogDatabase.CreateInMemory();
        var fts = new FtsExtension();
        fts.Load(db);
        using var conn = new BogConnection(db);

        Assert.True(conn.Query("CREATE NODE TABLE Doc(id INT64 PRIMARY KEY, body STRING)").IsSuccess);
        Assert.True(conn.Query("CREATE (:Doc {id:1, body:'alpha beta'})").IsSuccess);
        Assert.True(conn.Query("CALL CREATE_FTS_INDEX('Doc', 'doc_idx', ['body']) RETURN *").IsSuccess);
        Assert.True(conn.Query("CREATE (:Doc {id:2, body:'gamma delta'})").IsSuccess);

        var rebuild = conn.Query("CALL REBUILD_FTS_INDEX('doc_idx') RETURN *");
        Assert.True(rebuild.IsSuccess, rebuild.ErrorMessage);
        Assert.True(rebuild.HasNext());
        var row = rebuild.GetNext();
        Assert.Equal("doc_idx", row.GetString(0));
        Assert.Equal(2L, row.GetInt64(1)); // documents now indexed

        var bad = conn.Query("CALL REBUILD_FTS_INDEX('nope') RETURN *");
        Assert.False(bad.IsSuccess);
        Assert.Contains("not found", bad.ErrorMessage, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DropFtsIndex_RemovesDefinition_NotResurrectedOnWrite()
    {
        using var db = BogDatabase.CreateInMemory();
        var fts = new FtsExtension();
        fts.Load(db);
        using var conn = new BogConnection(db);

        Assert.True(conn.Query("CREATE NODE TABLE Doc(id INT64 PRIMARY KEY, body STRING)").IsSuccess);
        Assert.True(conn.Query("CREATE (:Doc {id:1, body:'alpha beta'})").IsSuccess);
        Assert.True(conn.Query("CALL CREATE_FTS_INDEX('Doc', 'doc_idx', ['body']) RETURN *").IsSuccess);

        Assert.True(conn.Query("CALL DROP_FTS_INDEX('doc_idx') RETURN *").IsSuccess);
        Assert.False(fts.Indexes.ContainsKey("doc_idx"));

        // A later write must not resurrect the dropped index via the auto-rebuild path.
        Assert.True(conn.Query("CREATE (:Doc {id:2, body:'gamma'})").IsSuccess);
        var q = conn.Query("CALL QUERY_FTS_INDEX('doc_idx', 'alpha') RETURN *");
        Assert.True(q.IsSuccess, q.ErrorMessage);
        Assert.False(q.HasNext());
    }

    [Fact]
    public void Reopen_RebuildsFtsIndexFromPersistedDefinition()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bogdb-fts-{System.Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(path);
        try
        {
            using (var db = BogDatabase.Open(path))
            {
                var fts = new FtsExtension();
                fts.Load(db);
                using var conn = new BogConnection(db);

                Assert.True(conn.Query("CREATE NODE TABLE Article(id INT64 PRIMARY KEY, body STRING)").IsSuccess);
                conn.BeginWriteTransaction();
                conn.UpsertNode("Article", 1L, new System.Collections.Generic.Dictionary<string, object>
                {
                    ["id"] = 1L,
                    ["body"] = "graph databases model relationships between entities"
                });
                conn.Commit();

                Assert.True(conn.Query("CALL CREATE_FTS_INDEX('Article', 'art_idx', ['body'], k1 := 1.5) RETURN *").IsSuccess);
            }

            using (var reopened = BogDatabase.Open(path))
            {
                var fts = new FtsExtension();
                fts.Load(reopened);
                using var conn = new BogConnection(reopened);

                // The definition survived reopen and the inverted index was rebuilt from current data.
                var result = conn.Query("CALL QUERY_FTS_INDEX('art_idx', 'graph') RETURN *");
                Assert.True(result.IsSuccess, result.ErrorMessage);
                Assert.True(result.HasNext());

                // The persisted BM25 parameter round-tripped through JSON.
                Assert.True(fts.Indexes.TryGetValue("art_idx", out var idx));
                Assert.Equal(1.5, idx!.K1, 6);
            }
        }
        finally
        {
            if (System.IO.Directory.Exists(path))
                System.IO.Directory.Delete(path, recursive: true);
        }
    }

    // ── Incremental maintenance (commit-deferred) ────────────────────────

    [Fact]
    public void QueryFtsIndex_ReflectsDeleteAfterCreate_Incrementally()
    {
        using var db = BogDatabase.CreateInMemory();
        var fts = new FtsExtension();
        fts.Load(db);
        using var conn = new BogConnection(db);

        Assert.True(conn.Query("CREATE NODE TABLE Doc(id INT64 PRIMARY KEY, body STRING)").IsSuccess);
        Assert.True(conn.Query("CREATE (:Doc {id:1, body:'alpha unique'})").IsSuccess);
        Assert.True(conn.Query("CREATE (:Doc {id:2, body:'beta common'})").IsSuccess);
        Assert.True(conn.Query("CALL CREATE_FTS_INDEX('Doc', 'doc_idx', ['body']) RETURN *").IsSuccess);

        Assert.True(conn.Query("CALL QUERY_FTS_INDEX('doc_idx', 'alpha') RETURN *").HasNext());

        // Deleting doc 1 must drop its terms from the index without a rebuild.
        Assert.True(conn.Query("MATCH (n:Doc {id:1}) DELETE n").IsSuccess);

        var after = conn.Query("CALL QUERY_FTS_INDEX('doc_idx', 'alpha') RETURN *");
        Assert.True(after.IsSuccess, after.ErrorMessage);
        Assert.False(after.HasNext());
    }

    [Fact]
    public void QueryFtsIndex_ReflectsUpdateAfterCreate_Incrementally()
    {
        using var db = BogDatabase.CreateInMemory();
        var fts = new FtsExtension();
        fts.Load(db);
        using var conn = new BogConnection(db);

        Assert.True(conn.Query("CREATE NODE TABLE Doc(id INT64 PRIMARY KEY, body STRING)").IsSuccess);
        Assert.True(conn.Query("CREATE (:Doc {id:1, body:'alpha unique'})").IsSuccess);
        Assert.True(conn.Query("CALL CREATE_FTS_INDEX('Doc', 'doc_idx', ['body']) RETURN *").IsSuccess);

        // Rewriting the body re-indexes the document (remove old terms, add new).
        Assert.True(conn.Query("MATCH (n:Doc {id:1}) SET n.body = 'gamma rewritten'").IsSuccess);

        Assert.False(conn.Query("CALL QUERY_FTS_INDEX('doc_idx', 'alpha') RETURN *").HasNext());
        var gamma = conn.Query("CALL QUERY_FTS_INDEX('doc_idx', 'gamma') RETURN *");
        Assert.True(gamma.IsSuccess, gamma.ErrorMessage);
        Assert.True(gamma.HasNext());
    }

    [Fact]
    public void QueryFtsIndex_RolledBackInsert_NotReflected()
    {
        using var db = BogDatabase.CreateInMemory();
        var fts = new FtsExtension();
        fts.Load(db);
        using var conn = new BogConnection(db);

        Assert.True(conn.Query("CREATE NODE TABLE Doc(id INT64 PRIMARY KEY, body STRING)").IsSuccess);
        Assert.True(conn.Query("CREATE (:Doc {id:1, body:'alpha unique'})").IsSuccess);
        Assert.True(conn.Query("CALL CREATE_FTS_INDEX('Doc', 'doc_idx', ['body']) RETURN *").IsSuccess);

        // The commit-deferred delta must be discarded when the transaction rolls back.
        conn.BeginWriteTransaction();
        Assert.True(conn.Query("CREATE (:Doc {id:2, body:'gamma ghost'})").IsSuccess);
        conn.Rollback();

        var after = conn.Query("CALL QUERY_FTS_INDEX('doc_idx', 'gamma') RETURN *");
        Assert.True(after.IsSuccess, after.ErrorMessage);
        Assert.False(after.HasNext());
    }

    [Fact]
    public void Reopen_RestoresPersistedFtsIndexFromDisk()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bogdb-fts-persist-{System.Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(path);
        try
        {
            using (var db = BogDatabase.Open(path))
            {
                var fts = new FtsExtension();
                fts.Load(db);
                using var conn = new BogConnection(db);

                Assert.True(conn.Query("CREATE NODE TABLE Article(id INT64 PRIMARY KEY, body STRING)").IsSuccess);
                conn.BeginWriteTransaction();
                conn.UpsertNode("Article", 1L, new System.Collections.Generic.Dictionary<string, object>
                {
                    ["id"] = 1L,
                    ["body"] = "graph databases model relationships"
                });
                conn.Commit();
                Assert.True(conn.Query("CALL CREATE_FTS_INDEX('Article', 'art_idx', ['body']) RETURN *").IsSuccess);
            } // Dispose → checkpoint → the inverted index is written to disk.

            var indexFile = System.IO.Path.Combine(path, "extensions", "fts.indexes.data.bin");
            Assert.True(System.IO.File.Exists(indexFile));
            Assert.True(new System.IO.FileInfo(indexFile).Length > 0);

            using (var reopened = BogDatabase.Open(path))
            {
                var fts = new FtsExtension();
                fts.Load(reopened);
                using var conn = new BogConnection(reopened);

                var result = conn.Query("CALL QUERY_FTS_INDEX('art_idx', 'graph') RETURN *");
                Assert.True(result.IsSuccess, result.ErrorMessage);
                Assert.True(result.HasNext());
            }
        }
        finally
        {
            if (System.IO.Directory.Exists(path))
                System.IO.Directory.Delete(path, recursive: true);
        }
    }
}
