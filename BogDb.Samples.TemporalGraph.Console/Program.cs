using System.Globalization;
using BogDb.Core.Main;

// ─────────────────────────────────────────────────────────────────────────────
// BogDb.Samples.TemporalGraph.Console
//
// Demonstrates BogDB's per-hop comprehension filter in variable-length traversal:
//
//     MATCH (a)-[:REL*lo..hi (e, n | WHERE <predicate over e / n>)]->(b)
//
// The WHERE is evaluated on every hop DURING traversal, pruning edges (and nodes)
// that fail — so a path only continues through hops that pass. This makes
// "temporal reachability" a single query: *who can X reach through relationships
// that were ALL valid at time T?* — which you cannot express by filtering paths
// after the fact, because two edges on a path may never be valid at the same time.
//
// Usage:
//   temporalgraph                 → run the guided demo
//   temporalgraph reach <name> --at <t> [--min-weight <w>] [--active-only]
// ─────────────────────────────────────────────────────────────────────────────

Console.WriteLine("""

  ┌──────────────────────────────────────────────────────────────┐
  │  Temporal Trust Network · BogDB Console Sample                │
  │  Per-hop temporal + edge filtering in a single Cypher query    │
  └──────────────────────────────────────────────────────────────┘
""");

using var db = BogDatabase.CreateInMemory();
using var conn = new BogConnection(db);
BuildNetwork(conn);

if (args.Length > 0 && args[0].Equals("reach", StringComparison.OrdinalIgnoreCase))
{
    RunReachCommand(conn, args);
    return;
}

RunGuidedDemo(conn);

// ── Guided demo ──────────────────────────────────────────────────────────────
static void RunGuidedDemo(BogConnection conn)
{
    Section("The network");
    Console.WriteLine("""
  People trust one another, but relationships form and dissolve over time. Each TRUSTS
  edge carries a validity window [valid_from, valid_to) as INT64 "day" epochs (an open
  valid_to = still active) and a trust weight in [0,1]:

    ana ──0.9,[10,100)──▶ ben ──0.8,[10,50)──▶ cara ──0.9,[10,100)──▶ gus
                          ben ──0.9,[60,  ∞)──▶ dan  ──0.9,[60,  ∞)──▶ gus
    ana ──0.4,[10,  ∞)──▶ eve ──0.9,[10,  ∞)──▶ finn
""");

    Section("1 · Naive reachability — ignores time");
    Show(conn, "Who has Ana EVER been able to reach (within 4 hops)?",
        "MATCH (p:Person {name:'ana'})-[:TRUSTS*1..4]->(q:Person) RETURN DISTINCT q.name AS name ORDER BY q.name",
        null);
    Console.WriteLine("  → Everyone. But some of these were never reachable at any single point in time.\n");

    Section("2 · As of day 30 — follow only edges valid then");
    Show(conn, "Reachable from Ana on day 30:",
        ReachQuery(temporal: true, weight: false, active: false),
        Params(t: 30));
    Console.WriteLine("  → Note 'cara' (Ben→Cara valid [10,50)) and 'gus' (via Cara). 'dan' is absent — Ben→Dan\n    only opens on day 60.\n");

    Section("3 · As of day 70 — the network has changed");
    Show(conn, "Reachable from Ana on day 70:",
        ReachQuery(temporal: true, weight: false, active: false),
        Params(t: 70));
    Console.WriteLine("""
  → Now 'dan' is reachable (Ben→Dan opened on day 60) and 'gus' is reached THROUGH Dan —
    but 'cara' has vanished, because Ben→Cara dissolved on day 50. The reachable set AND
    the paths depend on when you ask. Filtering whole paths afterward can't do this: the
    Ben→Cara and Ben→Dan edges are never valid at the same instant.
""");

    Section("4 · As of day 30, trust ≥ 0.5 — drop weak links");
    Show(conn, "Strong-and-valid reachability from Ana on day 30:",
        ReachQuery(temporal: true, weight: true, active: false),
        Params(t: 30, w: 0.5));
    Console.WriteLine("""
  → 'eve' and 'finn' drop out: Ana→Eve is only 0.4 trust, so that hop is pruned and the
    traversal never reaches Finn behind it. Temporal + edge-weight filters compose in one
    pass, pruning early instead of materializing every path first.
""");

    Section("Try it yourself");
    Console.WriteLine("""
  dotnet run --project BogDb.Samples.TemporalGraph.Console -- reach ana --at 55
  dotnet run --project BogDb.Samples.TemporalGraph.Console -- reach ana --at 70 --min-weight 0.85
""");
}

// ── `reach` command ──────────────────────────────────────────────────────────
static void RunReachCommand(BogConnection conn, string[] args)
{
    if (args.Length < 2)
    {
        Console.WriteLine("  Usage: reach <name> --at <t> [--min-weight <w>] [--active-only]");
        return;
    }

    var start = args[1].ToLowerInvariant();
    var t = GetLong(args, "--at", 0);
    var w = GetDouble(args, "--min-weight", double.NaN);
    var activeOnly = args.Contains("--active-only");
    var useWeight = !double.IsNaN(w);

    var query = ReachQuery(temporal: true, weight: useWeight, active: activeOnly, startName: start);
    var label = $"Reachable from '{start}' as of day {t}" +
                (useWeight ? $", trust ≥ {w.ToString(CultureInfo.InvariantCulture)}" : "") +
                (activeOnly ? ", active nodes only" : "") + ":";
    Show(conn, label, query, Params(t: t, w: useWeight ? w : (double?)null));
}

// ── Query construction ───────────────────────────────────────────────────────
// The per-hop comprehension: (e, n | WHERE …). 'e' is each intermediate edge, 'n' each
// intermediate node. The predicate is evaluated per hop to prune non-matching edges.
static string ReachQuery(bool temporal, bool weight, bool active, string startName = "ana")
{
    var conds = new List<string>();
    if (temporal) conds.Add("e.valid_from <= $t AND (e.valid_to IS NULL OR e.valid_to > $t)");
    if (weight) conds.Add("e.weight >= $w");
    if (active) conds.Add("n.active = true");

    var comprehension = conds.Count > 0 ? $" (e, n | WHERE {string.Join(" AND ", conds)})" : "";
    return $"MATCH (p:Person {{name:'{startName}'}})-[:TRUSTS*1..4{comprehension}]->(q:Person) " +
           "RETURN DISTINCT q.name AS name ORDER BY q.name";
}

static Dictionary<string, object?> Params(long? t = null, double? w = null)
{
    var p = new Dictionary<string, object?>();
    if (t.HasValue) p["t"] = t.Value;
    if (w.HasValue) p["w"] = w.Value;
    return p;
}

// ── Data ─────────────────────────────────────────────────────────────────────
static void BuildNetwork(BogConnection conn)
{
    Exec(conn, "CREATE NODE TABLE Person(name STRING PRIMARY KEY, active BOOL)");
    Exec(conn, "CREATE REL TABLE TRUSTS(FROM Person TO Person, weight DOUBLE, valid_from INT64, valid_to INT64)");

    foreach (var name in new[] { "ana", "ben", "cara", "dan", "eve", "finn", "gus" })
        Exec(conn, $"CREATE (:Person {{name:'{name}', active:true}})");

    //     from    to      weight  valid_from  valid_to (null = open)
    Trust(conn, "ana", "ben", 0.9, 10, 100);
    Trust(conn, "ben", "cara", 0.8, 10, 50);   // dissolves on day 50
    Trust(conn, "ben", "dan", 0.9, 60, null);  // opens on day 60, still active
    Trust(conn, "ana", "eve", 0.4, 10, null);  // weak, but long-lived
    Trust(conn, "eve", "finn", 0.9, 10, null);
    Trust(conn, "cara", "gus", 0.9, 10, 100);
    Trust(conn, "dan", "gus", 0.9, 60, null);
}

static void Trust(BogConnection conn, string from, string to, double weight, long validFrom, long? validTo)
{
    var wc = weight.ToString(CultureInfo.InvariantCulture);
    var props = validTo is { } vt
        ? $"weight:{wc}, valid_from:{validFrom}, valid_to:{vt}"
        : $"weight:{wc}, valid_from:{validFrom}";
    Exec(conn, $"MATCH (a:Person {{name:'{from}'}}),(b:Person {{name:'{to}'}}) CREATE (a)-[:TRUSTS {{{props}}}]->(b)");
}

// ── Execution + output helpers ───────────────────────────────────────────────
static void Exec(BogConnection conn, string cypher)
{
    var r = conn.Query(cypher);
    if (!r.IsSuccess)
        throw new InvalidOperationException($"Query failed: {r.ErrorMessage}\n  {cypher}");
}

static void Show(BogConnection conn, string label, string cypher, Dictionary<string, object?>? parameters)
{
    Console.WriteLine($"  {label}");
    Console.WriteLine($"  cypher> {cypher}");
    var result = parameters is null ? conn.Query(cypher) : conn.Query(cypher, parameters);
    if (!result.IsSuccess)
    {
        Console.WriteLine($"  [error] {result.ErrorMessage}\n");
        return;
    }

    var names = new List<string>();
    while (result.HasNext())
        names.Add(result.GetNext().GetString(0));
    Console.WriteLine($"  result> {(names.Count == 0 ? "(none)" : string.Join(", ", names))}\n");
}

static void Section(string title)
{
    Console.WriteLine($"\n─── {title} " + new string('─', Math.Max(0, 60 - title.Length)));
    Console.WriteLine();
}

static long GetLong(string[] args, string flag, long fallback)
{
    var i = Array.IndexOf(args, flag);
    return i >= 0 && i + 1 < args.Length && long.TryParse(args[i + 1], out var v) ? v : fallback;
}

static double GetDouble(string[] args, string flag, double fallback)
{
    var i = Array.IndexOf(args, flag);
    return i >= 0 && i + 1 < args.Length &&
           double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
        ? v : fallback;
}
