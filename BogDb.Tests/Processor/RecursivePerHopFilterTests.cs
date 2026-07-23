using System.Collections.Generic;
using BogDb.Core.Main;
using Xunit;

namespace BogDb.Tests.Processor;

/// <summary>
/// Variable-length traversal with a per-hop comprehension filter:
/// <c>MATCH (a)-[r:REL*lo..hi (rr, nn | WHERE &lt;predicate over rr/nn&gt;)]-&gt;(b)</c>.
/// The predicate is evaluated per edge inside RecursiveExtend to PRUNE non-matching hops, so a path that
/// would traverse a failing edge (or reach a failing node) does not continue. Enables native temporal
/// (valid-time) multi-hop traversal — "follow only edges valid at $t".
/// </summary>
public class RecursivePerHopFilterTests
{
    // Graph: chain 1->2 (w10) -> 3 (w1) -> 4 (w10), plus a direct 1->5 (w10, open validity). Node 3 has ok=false.
    private static BogConnection NewGraph()
    {
        var db = BogDatabase.CreateInMemory();
        var conn = new BogConnection(db);
        Assert.True(conn.Query("CREATE NODE TABLE A(id INT64 PRIMARY KEY, ok BOOL)").IsSuccess);
        Assert.True(conn.Query("CREATE REL TABLE R(FROM A TO A, w INT64, vf INT64, vt INT64)").IsSuccess);
        for (var i = 1; i <= 5; i++)
            Assert.True(conn.Query($"CREATE (:A {{id:{i}, ok:{(i != 3 ? "true" : "false")}}})").IsSuccess);
        Assert.True(conn.Query("MATCH (a:A{id:1}),(b:A{id:2}) CREATE (a)-[:R {w:10, vf:0, vt:100}]->(b)").IsSuccess);
        Assert.True(conn.Query("MATCH (a:A{id:2}),(b:A{id:3}) CREATE (a)-[:R {w:1,  vf:0, vt:100}]->(b)").IsSuccess);
        Assert.True(conn.Query("MATCH (a:A{id:3}),(b:A{id:4}) CREATE (a)-[:R {w:10, vf:0, vt:100}]->(b)").IsSuccess);
        Assert.True(conn.Query("MATCH (a:A{id:1}),(b:A{id:5}) CREATE (a)-[:R {w:10, vf:200}]->(b)").IsSuccess); // vt null = open
        return conn;
    }

    private static long[] Ids(BogDb.Core.Main.QueryResult.QueryResult q)
    {
        Assert.True(q.IsSuccess, q.ErrorMessage);
        var l = new List<long>();
        while (q.HasNext()) l.Add(q.GetNext().GetInt64(0));
        l.Sort();
        return l.ToArray();
    }

    [Fact]
    public void EdgePredicate_PrunesFailingHopsSoPathDoesNotContinue()
    {
        using var conn = NewGraph();
        // Baseline: within 3 hops everything is reachable.
        Assert.Equal(new[] { 2L, 3L, 4L, 5L }, Ids(conn.Query("MATCH (a:A{id:1})-[r:R*1..3]->(b:A) RETURN b.id")));

        // rr.w > 5 prunes the 2->3 (w:1) hop, so 3 and 4 become unreachable via the chain; 1->5 (w:10) survives.
        Assert.Equal(new[] { 2L, 5L },
            Ids(conn.Query("MATCH (a:A{id:1})-[r:R*1..3 (rr, nn | WHERE rr.w > 5)]->(b:A) RETURN b.id")));
    }

    [Fact]
    public void TemporalAsOf_MultiHopTraversalRespectsValidityAndOpenIntervals()
    {
        using var conn = NewGraph();
        const string asof =
            "MATCH (a:A{id:1})-[r:R*1..3 (rr, nn | WHERE rr.vf <= $t AND (rr.vt IS NULL OR rr.vt > $t))]->(b:A) RETURN b.id";

        // t=50: chain edges [0,100) are valid → reach 2,3,4; the 1->5 edge starts at 200 → not yet valid.
        var at50 = conn.Query(asof, new Dictionary<string, object?> { ["t"] = 50L });
        Assert.Equal(new[] { 2L, 3L, 4L }, Ids(at50));

        // t=250: chain edges expired (vt=100) → 2,3,4 unreachable; 1->5 (vf=200, open) is valid → only 5.
        var at250 = conn.Query(asof, new Dictionary<string, object?> { ["t"] = 250L });
        Assert.Equal(new[] { 5L }, Ids(at250));
    }

    [Fact]
    public void NodePredicate_PrunesHopsReachingFailingNodes()
    {
        using var conn = NewGraph();
        // nn.ok = true prunes any hop landing on node 3 (ok=false), so 4 (only reachable through 3) drops out.
        Assert.Equal(new[] { 2L, 5L },
            Ids(conn.Query("MATCH (a:A{id:1})-[r:R*1..3 (rr, nn | WHERE nn.ok = true)]->(b:A) RETURN b.id")));
    }

    [Fact]
    public void Directions_LeftAndBothApplyThePredicate()
    {
        using var conn = NewGraph();
        // LEFT from 4: 4<-3 (w10) reaches 3; 3<-2 (w1) pruned → 1,2 unreachable this way.
        Assert.Equal(new[] { 3L },
            Ids(conn.Query("MATCH (a:A{id:4})<-[r:R*1..3 (rr, nn | WHERE rr.w > 5)]-(b:A) RETURN b.id")));

        // BOTH from 2 (w>5): reaches 1 (via 1->2 w10); 2->3 (w1) pruned; from 1 reaches 5 (1->5 w10).
        Assert.Equal(new[] { 1L, 5L },
            Ids(conn.Query("MATCH (a:A{id:2})-[r:R*1..2 (rr, nn | WHERE rr.w > 5)]-(b:A) RETURN b.id")));
    }

    [Fact]
    public void LimitStaysEffective_OverPrunedResults()
    {
        using var conn = NewGraph();
        var q = conn.Query("MATCH (a:A{id:1})-[r:R*1..3 (rr, nn | WHERE rr.w > 5)]->(b:A) RETURN b.id LIMIT 1");
        Assert.True(q.IsSuccess, q.ErrorMessage);
        var rows = 0;
        long? id = null;
        while (q.HasNext()) { id = q.GetNext().GetInt64(0); rows++; }
        Assert.Equal(1, rows);
        Assert.Contains(id!.Value, new[] { 2L, 5L }); // one of the pruned-valid results
    }

    [Fact]
    public void ComprehensionVariable_DoesNotLeakIntoShadowedOuterVariable()
    {
        using var db = BogDatabase.CreateInMemory();
        using var conn = new BogConnection(db);
        Assert.True(conn.Query("CREATE NODE TABLE A(id INT64 PRIMARY KEY)").IsSuccess);
        Assert.True(conn.Query("CREATE REL TABLE R(FROM A TO A, w INT64)").IsSuccess);
        for (var i = 1; i <= 5; i++) Assert.True(conn.Query($"CREATE (:A {{id:{i}}})").IsSuccess);
        for (var i = 1; i < 5; i++)
            Assert.True(conn.Query($"MATCH (a:A{{id:{i}}}),(b:A{{id:{i + 1}}}) CREATE (a)-[:R {{w:10}}]->(b)").IsSuccess);

        // The comprehension deliberately names its intermediate node variable 'seed', shadowing the outer
        // 'seed' pinned to id=1. The per-hop binding must be scoped to the predicate — so 'seed' stays id=1
        // on every result row and is not clobbered by the landing node of each hop.
        var q = conn.Query(
            "MATCH (seed:A{id:1})-[:R]->(mid:A)-[r:R*1..2 (rr, seed | WHERE rr.w > 5)]->(b:A) " +
            "RETURN seed.id AS s, b.id AS bid ORDER BY b.id");
        Assert.True(q.IsSuccess, q.ErrorMessage);

        var results = new List<(long Seed, long B)>();
        while (q.HasNext()) { var r = q.GetNext(); results.Add((r.GetInt64(0), r.GetInt64(1))); }
        Assert.NotEmpty(results);                              // 1->2 then 2->3, 2->3->4
        Assert.All(results, r => Assert.Equal(1L, r.Seed));    // outer 'seed' never leaked
    }

    [Fact]
    public void NoComprehension_LeavesVariableLengthTraversalUnchanged()
    {
        using var conn = NewGraph();
        // Plain var-length and an OUTER WHERE (post-traversal on the bound endpoints) are unaffected.
        Assert.Equal(new[] { 2L, 3L, 4L, 5L }, Ids(conn.Query("MATCH (a:A{id:1})-[r:R*1..3]->(b:A) RETURN b.id")));
        Assert.Equal(new[] { 4L },
            Ids(conn.Query("MATCH (a:A{id:1})-[r:R*1..3]->(b:A) WHERE b.id = 4 RETURN b.id")));
    }
}
