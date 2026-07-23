using System;
using System.Collections.Generic;
using System.IO;
using BogDb.Core.Main;
using Xunit;

namespace BogDb.Tests.Function;

/// <summary>
/// Tests for the built-in CREATE_INDEX table function — the query-based path to a non-primary-key
/// secondary index, so predicates plan as INDEX_SCAN instead of a full SCAN_NODE_PROPERTY.
/// </summary>
public class CreateIndexFunctionTests
{
    private static void Seed(BogConnection conn)
    {
        Assert.True(conn.Query("CREATE NODE TABLE Drawer(id INT64 PRIMARY KEY, wing STRING, created INT64)").IsSuccess);
        for (var i = 1; i <= 6; i++)
            Assert.True(conn.Query($"CREATE (:Drawer {{id:{i}, wing:'{(i % 2 == 0 ? "east" : "west")}', created:{i * 10}}})").IsSuccess);
    }

    [Fact]
    public void CreateIndex_ViaCall_EnablesIndexScanAndReportsStatus()
    {
        using var db = BogDatabase.CreateInMemory();
        using var conn = new BogConnection(db);
        Seed(conn);

        // Baseline: without an index, the predicate is a full scan.
        var before = conn.Query("EXPLAIN MATCH (d:Drawer) WHERE d.wing = 'east' RETURN d.id");
        Assert.True(before.HasNext());
        Assert.Contains("SCAN_NODE_PROPERTY", before.GetNext().GetValue(0)!.ToString());

        var create = conn.Query("CALL create_index('Drawer', 'wing') RETURN *");
        Assert.True(create.IsSuccess, create.ErrorMessage);
        Assert.True(create.HasNext());
        var row = create.GetNext();
        Assert.Equal("Drawer", row.GetString(0));
        Assert.Equal("wing", row.GetString(1));
        Assert.Equal("created", row.GetString(2));
        Assert.True(db.Catalog.ContainsIndexEntry("Drawer", "wing"));

        // Now the same predicate plans as an index scan.
        var after = conn.Query("EXPLAIN MATCH (d:Drawer) WHERE d.wing = 'east' RETURN d.id");
        Assert.True(after.HasNext());
        Assert.Contains("INDEX_SCAN", after.GetNext().GetValue(0)!.ToString());
    }

    [Fact]
    public void CreateIndex_ViaCall_IsIdempotent()
    {
        using var db = BogDatabase.CreateInMemory();
        using var conn = new BogConnection(db);
        Seed(conn);

        Assert.Equal("created", conn.Query("CALL create_index('Drawer', 'wing') RETURN *").GetNext().GetString(2));
        Assert.Equal("exists", conn.Query("CALL create_index('Drawer', 'wing') RETURN *").GetNext().GetString(2));
    }

    [Fact]
    public void CreateIndex_ViaCall_ValidatesTableAndProperty()
    {
        using var db = BogDatabase.CreateInMemory();
        using var conn = new BogConnection(db);
        Seed(conn);

        var badTable = conn.Query("CALL create_index('NoSuchTable', 'wing') RETURN *");
        Assert.False(badTable.IsSuccess);
        Assert.Contains("existing node table", badTable.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var badProp = conn.Query("CALL create_index('Drawer', 'nope') RETURN *");
        Assert.False(badProp.IsSuccess);
        Assert.Contains("does not exist", badProp.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IndexedQuery_SupportsFilterOrderSkipLimitPagination()
    {
        using var db = BogDatabase.CreateInMemory();
        using var conn = new BogConnection(db);
        Seed(conn);
        Assert.True(conn.Query("CALL create_index('Drawer', 'wing') RETURN *").IsSuccess);

        // east drawers are ids 2,4,6 (created 20,40,60). ORDER BY created DESC → 6,4,2; SKIP 1 LIMIT 1 → 4.
        var page = conn.Query("MATCH (d:Drawer) WHERE d.wing = 'east' RETURN d.id ORDER BY d.created DESC SKIP 1 LIMIT 1");
        Assert.True(page.IsSuccess, page.ErrorMessage);
        Assert.True(page.HasNext());
        Assert.Equal(4L, page.GetNext().GetInt64(0));
        Assert.False(page.HasNext());
    }

    [Fact]
    public void CreateIndex_Persistent_SurvivesReopenAndStillPlansIndexScan()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bogdb-createidx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        try
        {
            using (var db = BogDatabase.Open(path))
            {
                using var conn = new BogConnection(db);
                Assert.True(conn.Query("CREATE NODE TABLE Drawer(id INT64 PRIMARY KEY, wing STRING)").IsSuccess);
                conn.BeginWriteTransaction();
                for (var i = 1; i <= 4; i++)
                    conn.UpsertNode("Drawer", (long)i, new Dictionary<string, object>
                    {
                        ["id"] = (long)i,
                        ["wing"] = i % 2 == 0 ? "east" : "west"
                    });
                conn.Commit();
                Assert.True(conn.Query("CALL create_index('Drawer', 'wing') RETURN *").IsSuccess);
            }

            using (var reopened = BogDatabase.Open(path))
            {
                using var conn = new BogConnection(reopened);
                Assert.True(reopened.Catalog.ContainsIndexEntry("Drawer", "wing"));

                var explain = conn.Query("EXPLAIN MATCH (d:Drawer) WHERE d.wing = 'east' RETURN d.id");
                Assert.True(explain.HasNext());
                Assert.Contains("INDEX_SCAN", explain.GetNext().GetValue(0)!.ToString());

                var q = conn.Query("MATCH (d:Drawer) WHERE d.wing = 'east' RETURN d.id ORDER BY d.id");
                var ids = new List<long>();
                while (q.HasNext()) ids.Add(q.GetNext().GetInt64(0));
                Assert.Equal(new[] { 2L, 4L }, ids);
            }
        }
        finally
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
    }
}
