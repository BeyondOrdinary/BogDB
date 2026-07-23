using System.Collections.Generic;
using System.Linq;
using BogDb.Core.Main;
using Xunit;

namespace BogDb.Tests.Processor;

/// <summary>
/// Reference/regression suite proving BogMem's uni-temporal (valid-time) knowledge-graph pattern runs on
/// BogDB's native rel edges with NO engine changes: entity nodes, a predicate rel table carrying INT64-epoch
/// <c>valid_from</c>/<c>valid_to</c> properties, and Cypher <c>WHERE</c> half-open interval filters. Also the
/// copy-paste template for the BogMem-side port.
///
/// Temporal contract: a fact is active at T iff (valid_from IS NULL OR valid_from &lt;= T) AND
/// (valid_to IS NULL OR valid_to &gt; T)  — inclusive lower, EXCLUSIVE upper, NULL = open end.
/// "current" = valid_to IS NULL, independent of the as-of instant.
/// </summary>
public class TemporalKnowledgeGraphTests
{
    private static BogConnection NewGraph()
    {
        var db = BogDatabase.CreateInMemory();
        var conn = new BogConnection(db);
        Assert.True(conn.Query("CREATE NODE TABLE Entity(id STRING PRIMARY KEY, name STRING)").IsSuccess);
        Assert.True(conn.Query("CREATE REL TABLE WORKS(FROM Entity TO Entity, predicate STRING, valid_from INT64, valid_to INT64)").IsSuccess);
        foreach (var (id, nm) in new[] { ("alice", "Alice"), ("acme", "Acme"), ("newco", "NewCo") })
            Assert.True(conn.Query($"CREATE (:Entity {{id:'{id}', name:'{nm}'}})").IsSuccess);
        return conn;
    }

    // as-of query: entity-anchored (bound source → O(out-degree) via adjacency), half-open + NULL-open.
    private static List<(string id, bool current)> AsOf(BogConnection conn, long t)
    {
        var q = conn.Query(
            $"MATCH (a:Entity {{id:'alice'}})-[r:WORKS]->(o:Entity) " +
            $"WHERE r.valid_from <= {t} AND (r.valid_to IS NULL OR r.valid_to > {t}) " +
            $"RETURN o.id AS id, (r.valid_to IS NULL) AS current ORDER BY o.id");
        Assert.True(q.IsSuccess, q.ErrorMessage);
        var rows = new List<(string, bool)>();
        while (q.HasNext())
        {
            var r = q.GetNext();
            rows.Add((r.GetString(0), r.GetBoolean(1)));
        }
        return rows;
    }

    [Fact]
    public void AsOfQuery_HasHalfOpenIntervalAndCurrentFlagSemantics()
    {
        using var conn = NewGraph();
        // acme: valid [0, 100).  newco: valid [100, ∞) — open end via omitted valid_to (reads back null).
        Assert.True(conn.Query("MATCH (a:Entity {id:'alice'}),(b:Entity {id:'acme'}) CREATE (a)-[:WORKS {predicate:'works_at', valid_from:0, valid_to:100}]->(b)").IsSuccess);
        Assert.True(conn.Query("MATCH (a:Entity {id:'alice'}),(b:Entity {id:'newco'}) CREATE (a)-[:WORKS {predicate:'works_at', valid_from:100}]->(b)").IsSuccess);

        Assert.Equal(new[] { ("acme", false) }, AsOf(conn, 50));    // acme active, closed → current=false
        Assert.Equal(new[] { ("newco", true) }, AsOf(conn, 150));   // newco active, open → current=true
        // Boundary at 100: acme's exclusive upper excludes it; newco's inclusive lower includes it.
        Assert.Equal(new[] { ("newco", true) }, AsOf(conn, 100));
    }

    [Fact]
    public void Invalidate_ClosesOpenIntervalViaSet()
    {
        using var conn = NewGraph();
        Assert.True(conn.Query("MATCH (a:Entity {id:'alice'}),(b:Entity {id:'newco'}) CREATE (a)-[:WORKS {predicate:'works_at', valid_from:100}]->(b)").IsSuccess);
        Assert.Single(AsOf(conn, 150));

        Assert.True(conn.Query("MATCH (:Entity {id:'alice'})-[r:WORKS]->(:Entity {id:'newco'}) SET r.valid_to = 140").IsSuccess);
        Assert.Empty(AsOf(conn, 150));       // no longer active at 150
        Assert.Single(AsOf(conn, 120));      // still active at 120 (< 140)
    }

    [Fact]
    public void Supersede_IsAtomicAndRollbackSafe()
    {
        using var conn = NewGraph();
        // alice → acme, open from 0.
        Assert.True(conn.Query("MATCH (a:Entity {id:'alice'}),(b:Entity {id:'acme'}) CREATE (a)-[:WORKS {predicate:'works_at', valid_from:0}]->(b)").IsSuccess);

        // Supersede at boundary 100 inside ONE transaction: close acme at 100, open newco at 100.
        // As-of 100 must return only the successor (acme excluded by exclusive upper, newco by inclusive lower).
        conn.BeginWriteTransaction();
        Assert.True(conn.Query("MATCH (:Entity {id:'alice'})-[r:WORKS]->(:Entity {id:'acme'}) SET r.valid_to = 100").IsSuccess);
        Assert.True(conn.Query("MATCH (a:Entity {id:'alice'}),(b:Entity {id:'newco'}) CREATE (a)-[:WORKS {predicate:'works_at', valid_from:100}]->(b)").IsSuccess);
        conn.Commit();

        Assert.Equal(new[] { ("newco", true) }, AsOf(conn, 100));
        Assert.Equal(new[] { ("acme", false) }, AsOf(conn, 50));

        // Rollback discards a supersede: reopening acme + closing newco inside a rolled-back tx must not persist.
        conn.BeginWriteTransaction();
        Assert.True(conn.Query("MATCH (:Entity {id:'alice'})-[r:WORKS]->(:Entity {id:'newco'}) SET r.valid_to = 120").IsSuccess);
        conn.Rollback();
        Assert.Equal(new[] { ("newco", true) }, AsOf(conn, 150));   // newco still open — rollback undid the close
    }

    [Fact]
    public void Timeline_ReturnsFullHistoryOrderedByValidFrom()
    {
        using var conn = NewGraph();
        Assert.True(conn.Query("MATCH (a:Entity {id:'alice'}),(b:Entity {id:'acme'}) CREATE (a)-[:WORKS {predicate:'works_at', valid_from:0, valid_to:100}]->(b)").IsSuccess);
        Assert.True(conn.Query("MATCH (a:Entity {id:'alice'}),(b:Entity {id:'newco'}) CREATE (a)-[:WORKS {predicate:'works_at', valid_from:100}]->(b)").IsSuccess);

        var q = conn.Query("MATCH (:Entity {id:'alice'})-[r:WORKS]->(o:Entity) RETURN o.id AS id, r.valid_from AS vf ORDER BY r.valid_from");
        Assert.True(q.IsSuccess, q.ErrorMessage);
        var history = new List<(string, long)>();
        while (q.HasNext()) { var r = q.GetNext(); history.Add((r.GetString(0), r.GetInt64(1))); }
        Assert.Equal(new[] { ("acme", 0L), ("newco", 100L) }, history);
    }

    [Fact]
    public void BothDirections_QueryIncomingAndOutgoingEdges()
    {
        using var conn = NewGraph();
        Assert.True(conn.Query("MATCH (a:Entity {id:'alice'}),(b:Entity {id:'acme'}) CREATE (a)-[:WORKS {predicate:'works_at', valid_from:0}]->(b)").IsSuccess);
        // acme ← newco (someone at newco reports to acme), also open.
        Assert.True(conn.Query("MATCH (a:Entity {id:'newco'}),(b:Entity {id:'acme'}) CREATE (a)-[:WORKS {predicate:'reports_to', valid_from:0}]->(b)").IsSuccess);

        var q = conn.Query("MATCH (a:Entity {id:'acme'})-[r:WORKS]-(o:Entity) WHERE r.valid_to IS NULL RETURN o.id ORDER BY o.id");
        Assert.True(q.IsSuccess, q.ErrorMessage);
        var neighbors = new List<string>();
        while (q.HasNext()) neighbors.Add(q.GetNext().GetString(0));
        Assert.Equal(new[] { "alice", "newco" }, neighbors);
    }
}
