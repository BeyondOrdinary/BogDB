using System;
using System.Collections.Generic;
using System.Linq;
using BogDb.Core.Common;
using BogDb.Core.Main;

namespace BogDb.Samples.SocialGraph.Blazor.Services;

// ── Domain POCOs ──────────────────────────────────────────────────────────────

public record SocialPerson(string Id, string Name, string Title, string Department,
    string Team, bool IsBridge);

public record SocialEdge(string From, string To, string FromName, string ToName,
    int Since, double Strength);

public record GraphNode(string Id, string Name, string Group, double X, double Y);
public record GraphEdge(string From, string To, double Strength);
public record GraphData(List<GraphNode> Nodes, List<GraphEdge> Edges);

public record PathResult(List<HopNode> Nodes);
public record HopNode(string Id, string Name, string Title, string Team, string Department = "");

public record InfluenceRow(int Rank, string PersonId, string Name, string Title,
    string Department, double PageRank, int Degree, double Delta);

public record OrgNode(string Id, string Name, string Title, string Department,
    List<OrgNode> Reports);

public record DashboardStats(int People, int Teams, int Departments,
    int KnowsEdges, int ReportsEdges, int BridgeNodes, double AvgDegree);

public record SocialQueryResponse(bool IsSuccess, string Error,
    List<string> Columns, List<Dictionary<string, object?>> Rows, long ElapsedMs);

public record ShowcaseQuery(string Title, string Description, string Tag, string Cypher);
public record ChartData(List<string> Labels, List<double> Values);

// ── Singleton Service ─────────────────────────────────────────────────────────

/// <summary>
/// Singleton that owns the BogDB in-memory social-network graph.
///
/// Demonstrates:
///   1. Schema: EnsureNodeTable / EnsureRelTable
///   2. Seed:   UpsertNodeById / UpsertRelationshipById
///   3. Query:  Execute() helper wrapping conn.Query()
///   4. Graph algorithms via CALL pagerank() / CALL wcc()
/// </summary>
public sealed class SocialGraphService
{
    private readonly BogDatabase _db;
    private readonly BogConnection _conn;

    // Cached expensive computations
    private List<InfluenceRow>? _cachedPageRank;

    public SocialGraphService()
    {
        _db   = BogDatabase.CreateInMemory();
        _conn = new BogConnection(_db);
        SetupSchema();
        SeedData();
    }

    // ── Public query API ─────────────────────────────────────────────────────

    public SocialQueryResponse Execute(string cypher)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r  = _conn.Query(cypher);
        sw.Stop();
        if (!r.IsSuccess)
            return new SocialQueryResponse(false, r.ErrorMessage ?? "Query failed", [], [], sw.ElapsedMilliseconds);

        var cols = r.ColumnNames.ToList();
        var rows = new List<Dictionary<string, object?>>();
        while (r.HasNext())
        {
            var row = r.GetNext();
            rows.Add(row.GetAsDictionary().ToDictionary(kv => kv.Key, kv => (object?)kv.Value));
        }
        return new SocialQueryResponse(true, string.Empty, cols, rows, sw.ElapsedMilliseconds);
    }

    // ── Dashboard ────────────────────────────────────────────────────────────

    public DashboardStats GetDashboardStats()
    {
        long people  = Scalar("MATCH (p:Person) RETURN COUNT(p) AS c");
        long teams   = Scalar("MATCH (t:Team)   RETURN COUNT(t) AS c");
        long depts   = Scalar("MATCH (d:Department) RETURN COUNT(d) AS c");
        long knows   = Scalar("MATCH ()-[:KNOWS]->() RETURN COUNT(*) AS c");
        long reports = Scalar("MATCH ()-[:REPORTS_TO]->() RETURN COUNT(*) AS c");
        long bridges = Scalar("MATCH (p:Person) WHERE p.is_bridge = true RETURN COUNT(p) AS c");

        // Average degree
        var degR = Execute(
            "MATCH (p:Person)-[:KNOWS]-() WITH p, COUNT(*) AS deg RETURN AVG(deg) AS avg_deg");
        double avgDeg = degR.Rows.Count > 0 ? Convert.ToDouble(degR.Rows[0]["avg_deg"] ?? 0.0) : 0;

        return new DashboardStats((int)people, (int)teams, (int)depts,
            (int)knows, (int)reports, (int)bridges, Math.Round(avgDeg, 1));
    }

    public ChartData GetPeopleByDept()
    {
        var r = Execute("MATCH (p:Person) WITH p.department AS dept, COUNT(*) AS cnt RETURN dept, cnt ORDER BY cnt DESC");
        return ToChart(r, "dept", "cnt");
    }

    public ChartData GetPeopleByTeam()
    {
        var r = Execute("MATCH (p:Person) WITH p.team AS team, COUNT(*) AS cnt RETURN team, cnt ORDER BY cnt DESC");
        return ToChart(r, "team", "cnt");
    }

    public ChartData GetTopConnected()
    {
        var r = Execute("""
            MATCH (p:Person)-[:KNOWS]-()
            WITH p.name AS name, COUNT(*) AS connections
            RETURN name, connections ORDER BY connections DESC LIMIT 10
            """);
        return ToChart(r, "name", "connections");
    }

    // ── Six Degrees Explorer ─────────────────────────────────────────────────

    public List<string> GetAllPersonNames()
    {
        var r = Execute("MATCH (p:Person) RETURN p.name AS name ORDER BY p.name");
        return r.Rows.Select(row => row["name"]?.ToString() ?? "").ToList();
    }

    public PathResult? FindShortestPath(string fromName, string toName)
    {
        // BogDb requires shortestPath to be in a single MATCH clause alongside the
        // anchor node bindings — splitting them across two MATCH clauses means the
        // second MATCH won't propagate the bound variables correctly.
        var from  = fromName.Replace("'", "\\'");
        var to    = toName.Replace("'", "\\'");
        var cypher =
            $"MATCH path = shortestPath(" +
            $"(a:Person {{name: '{from}'}})-[:KNOWS*1..10]-(b:Person {{name: '{to}'}})" +
            $") " +
            $"UNWIND nodes(path) AS n " +
            $"RETURN n.id AS id, n.name AS name, n.title AS title, n.team AS team, n.department AS department";

        var r = Execute(cypher);
        if (!r.IsSuccess || r.Rows.Count == 0) return null;

        return new PathResult(r.Rows.Select(row => new HopNode(
            row["id"]?.ToString() ?? "",
            row["name"]?.ToString() ?? "",
            row["title"]?.ToString() ?? "",
            row["team"]?.ToString() ?? "",
            row["department"]?.ToString() ?? "")).ToList());
    }

    /// <summary>Returns the top N most connected people with full details.</summary>
    public List<SocialPerson> GetTopConnectedPeople(int limit = 10)
    {
        var r = Execute($"""
            MATCH (p:Person)-[:KNOWS]-()
            WITH p, COUNT(*) AS connections
            ORDER BY connections DESC LIMIT {limit}
            RETURN p.id AS id, p.name AS name, p.title AS title,
                   p.department AS department, p.team AS team,
                   p.is_bridge AS is_bridge, connections
            """);
        return r.Rows.Select(row => new SocialPerson(
            row["id"]?.ToString() ?? "",
            row["name"]?.ToString() ?? "",
            row["title"]?.ToString() ?? "",
            row["department"]?.ToString() ?? "",
            row["team"]?.ToString() ?? "",
            row["is_bridge"] is bool b && b)).ToList();
    }

    /// <summary>Returns all bridge nodes (people who cross department boundaries).</summary>
    public List<(SocialPerson Person, int DeptReach, List<string> Depts)> GetBridgePeople()
    {
        var r = Execute("""
            MATCH (p:Person)-[:KNOWS]-(other:Person)
            WITH p, COLLECT(DISTINCT other.department) AS depts
            WHERE SIZE(depts) >= 2
            RETURN p.id AS id, p.name AS name, p.title AS title,
                   p.department AS department, p.team AS team, p.is_bridge AS is_bridge,
                   SIZE(depts) AS dept_reach, depts AS connected_depts
            ORDER BY dept_reach DESC
            """);
        return r.Rows.Select(row =>
        {
            var person = new SocialPerson(
                row["id"]?.ToString() ?? "",
                row["name"]?.ToString() ?? "",
                row["title"]?.ToString() ?? "",
                row["department"]?.ToString() ?? "",
                row["team"]?.ToString() ?? "",
                row["is_bridge"] is bool b && b);
            int reach = Convert.ToInt32(row["dept_reach"] ?? 0);
            var depts = row["connected_depts"] is System.Collections.IEnumerable en
                ? en.Cast<object>().Select(o => o.ToString() ?? "").ToList()
                : new List<string>();
            return (person, reach, depts);
        }).ToList();
    }

    /// <summary>Returns mutual connections between two named people.</summary>
    public List<SocialPerson> GetMutualConnections(string nameA, string nameB)
    {
        var a = nameA.Replace("'", "\\'");
        var b = nameB.Replace("'", "\\'");
        var r = Execute(
            $"MATCH (a:Person {{name: '{a}'}})-[:KNOWS]-(mutual:Person)-[:KNOWS]-(b:Person {{name: '{b}'}}) " +
            $"WHERE mutual <> a AND mutual <> b " +
            $"RETURN DISTINCT mutual.id AS id, mutual.name AS name, mutual.title AS title, " +
            $"mutual.department AS department, mutual.team AS team, mutual.is_bridge AS is_bridge " +
            $"ORDER BY mutual.name");
        return r.Rows.Select(row => new SocialPerson(
            row["id"]?.ToString() ?? "",
            row["name"]?.ToString() ?? "",
            row["title"]?.ToString() ?? "",
            row["department"]?.ToString() ?? "",
            row["team"]?.ToString() ?? "",
            row["is_bridge"] is bool b2 && b2)).ToList();
    }

    // ── Community Map ────────────────────────────────────────────────────────

    public GraphData GetGraphData()
    {
        var people = Execute(
            "MATCH (p:Person) RETURN p.id AS id, p.name AS name, p.department AS dept ORDER BY p.id LIMIT 80");

        var edges = Execute(
            "MATCH (a:Person)-[k:KNOWS]->(b:Person) " +
            "RETURN a.id AS from, b.id AS to, k.strength AS strength LIMIT 200");

        // Assign department-based group for colouring
        var nodes = people.Rows.Select((row, i) =>
        {
            var dept = row["dept"]?.ToString() ?? "";
            var group = dept.Contains("Engineering") ? "Engineering" :
                        dept.Contains("Product") ? "Product" : "Operations";
            return new GraphNode(
                row["id"]?.ToString() ?? "",
                row["name"]?.ToString() ?? "",
                group, 0, 0);
        }).ToList();

        var graphEdges = edges.Rows.Select(row => new GraphEdge(
            row["from"]?.ToString() ?? "",
            row["to"]?.ToString() ?? "",
            Convert.ToDouble(row["strength"] ?? 0.5)
        )).ToList();

        return new GraphData(nodes, graphEdges);
    }

    // ── Influence (PageRank) ─────────────────────────────────────────────────

    public List<InfluenceRow> GetInfluenceLeaderboard()
    {
        if (_cachedPageRank is not null) return _cachedPageRank;

        // Degree counts for comparison
        var degR = Execute(
            "MATCH (p:Person)-[:KNOWS]-() WITH p.id AS pid, COUNT(*) AS deg RETURN pid, deg");
        var degMap = degR.Rows.ToDictionary(
            r => r["pid"]?.ToString() ?? "",
            r => Convert.ToInt32(r["deg"] ?? 0));

        // PageRank via CALL
        var prR = Execute("CALL pagerank('KNOWS') YIELD node, rank");
        if (!prR.IsSuccess || prR.Rows.Count == 0)
        {
            // Fallback: degree-based ranking
            var fallback = Execute(
                "MATCH (p:Person)-[:KNOWS]-() WITH p, COUNT(*) AS deg RETURN p.id AS id, p.name AS name, p.title AS title, p.department AS dept, deg ORDER BY deg DESC LIMIT 30");

            _cachedPageRank = fallback.Rows.Select((row, i) =>
            {
                var id   = row["id"]?.ToString() ?? "";
                var deg  = Convert.ToInt32(row["deg"] ?? 0);
                return new InfluenceRow(i + 1, id,
                    row["name"]?.ToString() ?? "",
                    row["title"]?.ToString() ?? "",
                    row["dept"]?.ToString() ?? "",
                    Math.Round(deg * 0.01, 4), deg, 0);
            }).ToList();
            return _cachedPageRank;
        }

        // Pull person details
        var persons = Execute(
            "MATCH (p:Person) RETURN p.id AS id, p.name AS name, p.title AS title, p.department AS dept");
        var personMap = persons.Rows.ToDictionary(
            r => r["id"]?.ToString() ?? "",
            r => (Name:  r["name"]?.ToString() ?? "",
                  Title: r["title"]?.ToString() ?? "",
                  Dept:  r["dept"]?.ToString() ?? ""));

        var ranked = prR.Rows
            .Select(row =>
            {
                // node may be the internal ID string or the offset — get via p.id
                var nodeId = row["node"]?.ToString() ?? "";
                var pr = Convert.ToDouble(row["rank"] ?? 0);
                return (NodeId: nodeId, Pr: pr);
            })
            .OrderByDescending(x => x.Pr)
            .Take(20)
            .Select((x, i) =>
            {
                personMap.TryGetValue(x.NodeId, out var p);
                degMap.TryGetValue(x.NodeId, out var deg);
                return new InfluenceRow(i + 1, x.NodeId,
                    p.Name, p.Title, p.Dept,
                    Math.Round(x.Pr, 6), deg,
                    Math.Round(x.Pr * 100 - deg, 2));
            }).ToList();

        _cachedPageRank = ranked;
        return _cachedPageRank;
    }

    // ── Org Chart ────────────────────────────────────────────────────────────

    public List<OrgNode> GetOrgChart()
    {
        // Get all REPORTS_TO relationships
        var rels = Execute(
            "MATCH (emp:Person)-[:REPORTS_TO]->(mgr:Person) " +
            "RETURN emp.id AS emp_id, mgr.id AS mgr_id, emp.name AS emp_name, " +
            "emp.title AS emp_title, emp.department AS emp_dept ORDER BY emp.name");

        // Get all people
        var people = Execute(
            "MATCH (p:Person) RETURN p.id AS id, p.name AS name, p.title AS title, p.department AS dept");
        var personMap = people.Rows.ToDictionary(
            r => r["id"]?.ToString() ?? "",
            r => new OrgNode(
                r["id"]?.ToString() ?? "",
                r["name"]?.ToString() ?? "",
                r["title"]?.ToString() ?? "",
                r["dept"]?.ToString() ?? "",
                new()));

        // Build the tree
        var managedBy = new HashSet<string>();
        foreach (var row in rels.Rows)
        {
            var empId = row["emp_id"]?.ToString() ?? "";
            var mgrId = row["mgr_id"]?.ToString() ?? "";
            if (personMap.TryGetValue(mgrId, out var mgr) &&
                personMap.TryGetValue(empId, out var emp))
            {
                mgr.Reports.Add(emp);
                managedBy.Add(empId);
            }
        }

        // Return roots (people not managed by anyone, up to 3 levels)
        return personMap.Values
            .Where(n => !managedBy.Contains(n.Id) && n.Reports.Count > 0)
            .OrderBy(n => n.Name)
            .Take(6)
            .ToList();
    }

    // ── Showcase queries ─────────────────────────────────────────────────────

    public static List<ShowcaseQuery> GetShowcaseQueries() =>
    [
        new("Q1 · Six Degrees — Shortest Path",
            "undirected KNOWS* — find the hops between any two people",
            "path",
            """
            MATCH p = shortestPath(
              (a:Person {name: 'Alice Chen'})-[:KNOWS*]-(b:Person {name: 'Zara Khan'})
            )
            RETURN [n IN nodes(p) | n.name + ' (' + n.title + ')'] AS chain, length(p) AS hops
            """),

        new("Q2 · Mutual Connections",
            "shared neighbours — find people both Alice and Bob know",
            "traversal",
            """
            MATCH (a:Person {name: 'Alice Chen'})-[:KNOWS]-(mutual)-[:KNOWS]-(b:Person {name: 'Bob Patel'})
            WHERE mutual <> a AND mutual <> b
            RETURN DISTINCT mutual.name AS mutual_friend, mutual.title AS title, mutual.team AS team
            ORDER BY mutual_friend
            """),

        new("Q3 · K-Hop Reachability Ring",
            "KNOWS*1..3 — everyone within 3 introductions of a given person",
            "traversal",
            """
            MATCH (src:Person {name: 'Alice Chen'})-[:KNOWS*1..3]-(reachable:Person)
            WHERE reachable <> src
            WITH DISTINCT reachable, 1 AS hop
            RETURN reachable.name AS name, reachable.department AS dept, reachable.team AS team
            ORDER BY name LIMIT 30
            """),

        new("Q4 · Cross-Department Bridges",
            "People with KNOWS edges to ≥ 2 different departments — structural bridge nodes",
            "aggregation",
            """
            MATCH (p:Person)-[:KNOWS]-(other:Person)
            WITH p, COLLECT(DISTINCT other.department) AS depts
            WHERE SIZE(depts) >= 2
            RETURN p.name AS bridge, p.department AS own_dept, p.title AS title,
                   SIZE(depts) AS dept_reach, depts AS connected_depts
            ORDER BY dept_reach DESC
            """),

        new("Q5 · Influence via PageRank",
            "CALL pagerank() — rank people by structural importance in the KNOWS network",
            "algorithm",
            """
            CALL pagerank('KNOWS') YIELD node, rank
            MATCH (p:Person {id: node})
            RETURN p.name AS name, p.title AS title, p.department AS dept, rank
            ORDER BY rank DESC LIMIT 15
            """),

        new("Q6 · Community Detection (WCC)",
            "CALL wcc() — assign each person to their connected component",
            "algorithm",
            """
            CALL wcc('KNOWS') YIELD node, componentId
            MATCH (p:Person {id: node})
            WITH componentId, COLLECT(p.name) AS members, COUNT(*) AS size
            RETURN componentId, size, members ORDER BY size DESC LIMIT 10
            """),

        new("Q7 · Org Reporting Chain",
            "REPORTS_TO*1..5 — full management chain from an individual to the top",
            "traversal",
            """
            MATCH path = (emp:Person {name: 'Alice Chen'})-[:REPORTS_TO*1..5]->(top:Person)
            WHERE NOT (top)-[:REPORTS_TO]->()
            RETURN [n IN nodes(path) | n.name + ' — ' + n.title] AS chain, length(path) AS levels
            """),

        new("Q8 · Team Collaboration Score",
            "Count KNOWS edges between people on different teams — cross-team connection density",
            "aggregation",
            """
            MATCH (a:Person)-[:KNOWS]-(b:Person)
            WHERE a.team <> b.team
            WITH a.team AS team1, b.team AS team2, COUNT(*) AS cross_edges
            WHERE team1 < team2
            RETURN team1, team2, cross_edges ORDER BY cross_edges DESC LIMIT 15
            """),

        new("Q9 · Average Path Length Between Departments",
            "Sample shortest paths between random Engineering ↔ Operations pairs",
            "path",
            """
            MATCH (a:Person {department: 'Engineering'}), (b:Person {department: 'Operations'})
            WITH a, b LIMIT 5
            MATCH path = shortestPath((a)-[:KNOWS*]-(b))
            RETURN a.name AS from_eng, b.name AS to_ops, length(path) AS path_len
            ORDER BY path_len
            """),

        new("Q10 · High-Strength Relationships",
            "Filter KNOWS edges by strength ≥ 0.8 — close working relationships",
            "filter",
            """
            MATCH (a:Person)-[k:KNOWS]->(b:Person)
            WHERE k.strength >= 0.8
            RETURN a.name AS person, b.name AS close_contact, k.strength AS strength,
                   a.team AS team1, b.team AS team2
            ORDER BY strength DESC LIMIT 20
            """),
    ];

    // ── Private helpers ──────────────────────────────────────────────────────

    private long Scalar(string cypher)
    {
        var r = Execute(cypher);
        if (r.Rows.Count == 0) return 0;
        return Convert.ToInt64(r.Rows[0]["c"] ?? 0L);
    }

    private static ChartData ToChart(SocialQueryResponse r, string labelCol, string valueCol) => new(
        r.Rows.Select(row => row[labelCol]?.ToString() ?? "").ToList(),
        r.Rows.Select(row => Convert.ToDouble(row[valueCol] ?? 0)).ToList()
    );

    // ── Schema ───────────────────────────────────────────────────────────────

    private void SetupSchema()
    {
        _conn.BeginWriteTransaction();

        _conn.EnsureNodeTable("Person", new()
        {
            ["id"]         = LogicalTypeID.STRING,
            ["name"]       = LogicalTypeID.STRING,
            ["title"]      = LogicalTypeID.STRING,
            ["department"] = LogicalTypeID.STRING,
            ["team"]       = LogicalTypeID.STRING,
            ["is_bridge"]  = LogicalTypeID.BOOL,
        });

        _conn.EnsureNodeTable("Team", new()
        {
            ["id"]         = LogicalTypeID.STRING,
            ["name"]       = LogicalTypeID.STRING,
            ["department"] = LogicalTypeID.STRING,
        });

        _conn.EnsureNodeTable("Department", new()
        {
            ["id"]   = LogicalTypeID.STRING,
            ["name"] = LogicalTypeID.STRING,
        });

        _conn.EnsureRelTable("KNOWS",      "Person", "Person", new()
        {
            ["since"]    = LogicalTypeID.INT64,
            ["strength"] = LogicalTypeID.DOUBLE,
        });
        _conn.EnsureRelTable("MEMBER_OF",  "Person", "Team",   new());
        _conn.EnsureRelTable("PART_OF",    "Team",   "Department", new());
        _conn.EnsureRelTable("REPORTS_TO", "Person", "Person", new());

        _conn.Commit();
    }

    // ── Seed data ────────────────────────────────────────────────────────────

    private void SeedData()
    {
        _conn.BeginWriteTransaction();
        SeedDepartments();
        SeedTeams();
        SeedPeople();
        SeedKnows();
        SeedOrg();
        _conn.Commit();
    }

    private void SeedDepartments()
    {
        void D(string id, string name) =>
            _conn.UpsertNodeById("Department", id, new() { ["id"]=id, ["name"]=name });
        D("dept-eng",  "Engineering");
        D("dept-prod", "Product");
        D("dept-ops",  "Operations");
    }

    private void SeedTeams()
    {
        void T(string id, string name, string dept) =>
            _conn.UpsertNodeById("Team", id, new() { ["id"]=id, ["name"]=name, ["department"]=dept });
        T("team-backend",   "Backend",           "dept-eng");
        T("team-frontend",  "Frontend",          "dept-eng");
        T("team-platform",  "Platform",          "dept-eng");
        T("team-ml",        "Machine Learning",  "dept-eng");
        T("team-design",    "Design",            "dept-prod");
        T("team-pm",        "Product Management","dept-prod");
        T("team-growth",    "Growth",            "dept-prod");
        T("team-devops",    "DevOps",            "dept-ops");
        T("team-security",  "Security",          "dept-ops");
        T("team-analytics", "Analytics",         "dept-ops");
    }

    // Titles per team
    private static readonly string[] BackendTitles   = ["Sr. Backend Engineer","Backend Engineer","Staff Engineer","Principal Engineer"];
    private static readonly string[] FrontendTitles  = ["Sr. Frontend Engineer","Frontend Engineer","UI Engineer","Staff Engineer"];
    private static readonly string[] PlatformTitles  = ["Platform Engineer","Sr. Platform Engineer","Staff Engineer"];
    private static readonly string[] MlTitles        = ["ML Engineer","Sr. ML Engineer","Research Engineer","Applied Scientist"];
    private static readonly string[] DesignTitles    = ["Product Designer","Sr. UX Designer","Design Lead"];
    private static readonly string[] PmTitles        = ["Product Manager","Sr. Product Manager","Group PM","Director of Product"];
    private static readonly string[] GrowthTitles    = ["Growth PM","Analytics Engineer","Growth Engineer"];
    private static readonly string[] DevOpsTitles    = ["DevOps Engineer","Sr. DevOps Engineer","SRE","Platform SRE"];
    private static readonly string[] SecurityTitles  = ["Security Engineer","Sr. Security Engineer","Appsec Engineer"];
    private static readonly string[] AnalyticsTitles = ["Data Analyst","Sr. Data Analyst","Data Engineer","Analytics Engineer"];

    private static readonly (string Id, string DeptId, string[] Titles, bool Bridge, string Human)[] _teamDefs =
    [
        ("team-backend",   "dept-eng",  BackendTitles,   false, "Backend"),
        ("team-frontend",  "dept-eng",  FrontendTitles,  false, "Frontend"),
        ("team-platform",  "dept-eng",  PlatformTitles,  true,  "Platform"),
        ("team-ml",        "dept-eng",  MlTitles,        false, "Machine Learning"),
        ("team-design",    "dept-prod", DesignTitles,    false, "Design"),
        ("team-pm",        "dept-prod", PmTitles,        true,  "Product Management"),
        ("team-growth",    "dept-prod", GrowthTitles,    false, "Growth"),
        ("team-devops",    "dept-ops",  DevOpsTitles,    true,  "DevOps"),
        ("team-security",  "dept-ops",  SecurityTitles,  false, "Security"),
        ("team-analytics", "dept-ops",  AnalyticsTitles, false, "Analytics"),
    ];

    private static readonly string[] _firstNames = [
        "Alice","Bob","Carol","David","Eve","Frank","Grace","Hiro","Iris","James",
        "Kate","Liam","Maya","Noah","Olivia","Pedro","Quinn","Rosa","Sam","Tara",
        "Uma","Victor","Wendy","Xander","Yara","Zara","Ahmed","Bella","Carlos","Diana",
        "Ethan","Fiona","George","Hannah","Ivan","Julia","Kevin","Laura","Marcus","Nina",
        "Oscar","Priya","Rafael","Sophia","Thomas","Ursula","Vivian","Walter","Ximena","Yusuf",
        "Zoe","Alex","Brenda","Cole","Dara","Eli","Faith","Gabe","Hana","Igor","Jade"
    ];
    private static readonly string[] _lastNames = [
        "Chen","Patel","Smith","Johnson","Williams","Brown","Jones","Garcia","Miller","Davis",
        "Wilson","Moore","Taylor","Anderson","Thomas","Jackson","White","Harris","Martin","Thompson",
        "Rivera","Kumar","Zhang","Müller","Santos","Okafor","Kim","Nguyen","Johansson","Cohen",
        "Khan","Nakamura","Rossi","O'Brien","Dupont","Nielsen","Ahmed","Alves","Ferreira","Costa",
        "Romero","Reyes","Torres","Flores","Perez","Cruz","Morales","Ortiz","Gutierrez","Jimenez"
    ];

    // 200 person IDs
    private readonly List<string> _personIds = [];

    private void SeedPeople()
    {
        var rng = new Random(42);
        int counter = 0;

        var teamSizes = new Dictionary<string, int>
        {
            ["team-backend"]=25, ["team-frontend"]=22, ["team-platform"]=18, ["team-ml"]=15,
            ["team-design"]=16,  ["team-pm"]=20,        ["team-growth"]=14,
            ["team-devops"]=20,  ["team-security"]=18,  ["team-analytics"]=22,
        };

        foreach (var (teamId, deptId, titles, bridge, humanName) in _teamDefs)
        {
            var size = teamSizes[teamId];
            var dept = deptId == "dept-eng" ? "Engineering" :
                       deptId == "dept-prod" ? "Product" : "Operations";

            for (int i = 0; i < size; i++)
            {
                var fn  = _firstNames[counter % _firstNames.Length];
                var ln  = _lastNames[(counter * 7 + i * 3) % _lastNames.Length];
                var pid = $"p-{counter:D3}";
                var name = $"{fn} {ln}";
                var isBridge = bridge && i < 2;
                var title = titles[i % titles.Length];

                _conn.UpsertNodeById("Person", pid, new()
                {
                    ["id"]         = pid,
                    ["name"]       = name,
                    ["title"]      = title,
                    ["department"] = dept,
                    ["team"]       = humanName,
                    ["is_bridge"]  = isBridge,
                });

                _conn.UpsertRelationshipById("MEMBER_OF", pid, teamId, new());
                _personIds.Add(pid);
                counter++;
            }
        }

        // PART_OF for teams
        foreach (var (teamId, deptId, _, _, _) in _teamDefs)
            _conn.UpsertRelationshipById("PART_OF", teamId, deptId, new());
    }

    private void SeedKnows()
    {
        var rng = new Random(99);
        var added = new HashSet<(string, string)>();

        void K(string a, string b, double strength)
        {
            if (a == b) return;
            var key = string.Compare(a, b, StringComparison.Ordinal) < 0 ? (a, b) : (b, a);
            if (!added.Add(key)) return;
            int since = 2018 + rng.Next(6);
            _conn.UpsertRelationshipById("KNOWS", a, b, new() { ["since"]=(long)since, ["strength"]=strength });
            _conn.UpsertRelationshipById("KNOWS", b, a, new() { ["since"]=(long)since, ["strength"]=strength });
        }

        // Dense within-team clusters then cross-team bridges
        // Group by team via query at seed time
        var teamGroups = new Dictionary<string, List<string>>();
        {
            var r = Execute("MATCH (p:Person) RETURN p.id AS id, p.team AS team ORDER BY p.team, p.id");
            foreach (var row in r.Rows)
            {
                var pid  = row["id"]?.ToString() ?? "";
                var team = row["team"]?.ToString() ?? "";
                if (!teamGroups.ContainsKey(team)) teamGroups[team] = [];
                teamGroups[team].Add(pid);
            }
        }

        // Within-team: ring + random extra edges
        foreach (var (team, members) in teamGroups)
        {
            // Ring
            for (int i = 0; i < members.Count; i++)
                K(members[i], members[(i + 1) % members.Count], 0.6 + rng.NextDouble() * 0.35);

            // Extra random pairs
            int extras = members.Count / 2;
            for (int j = 0; j < extras; j++)
            {
                var a = members[rng.Next(members.Count)];
                var b = members[rng.Next(members.Count)];
                K(a, b, 0.4 + rng.NextDouble() * 0.4);
            }
        }

        // Cross-team bridges: platform + pm + devops people connect across departments
        var teamKeys = teamGroups.Keys.ToList();
        for (int t1 = 0; t1 < teamKeys.Count; t1++)
        {
            for (int t2 = t1 + 1; t2 < teamKeys.Count; t2++)
            {
                var g1 = teamGroups[teamKeys[t1]];
                var g2 = teamGroups[teamKeys[t2]];
                int bridges = rng.Next(3, 7);
                for (int b = 0; b < bridges; b++)
                {
                    var a = g1[rng.Next(g1.Count)];
                    var bk = g2[rng.Next(g2.Count)];
                    K(a, bk, 0.3 + rng.NextDouble() * 0.45);
                }
            }
        }

        // Add a few very high-strength edges (close collaborators)
        for (int h = 0; h < 30; h++)
        {
            var a = _personIds[rng.Next(_personIds.Count)];
            var b = _personIds[rng.Next(_personIds.Count)];
            K(a, b, 0.85 + rng.NextDouble() * 0.15);
        }
    }

    private void SeedOrg()
    {
        // Create a simple 3-level org: 3 VPs, each with 3-4 directors, each with 3-5 ICs
        // Use first few people from each team as managers
        var r = Execute("MATCH (p:Person) RETURN p.id AS id, p.team AS team ORDER BY p.team, p.id");
        var byTeam = new Dictionary<string, List<string>>();
        foreach (var row in r.Rows)
        {
            var pid  = row["id"]?.ToString() ?? "";
            var team = row["team"]?.ToString() ?? "";
            if (!byTeam.ContainsKey(team)) byTeam[team] = [];
            byTeam[team].Add(pid);
        }

        void RT(string emp, string mgr) =>
            _conn.UpsertRelationshipById("REPORTS_TO", emp, mgr, new());

        // VP-level (1st person per large team)
        var vpBackend   = byTeam.GetValueOrDefault("Backend",   [])?[0];
        var vpProduct   = byTeam.GetValueOrDefault("Product Management", [])?[0];
        var vpDevops    = byTeam.GetValueOrDefault("Dev Ops",   byTeam.GetValueOrDefault("DevOps", []))?[0];

        // Pick representable teams
        var allTeams = byTeam.Keys.ToList();
        foreach (var team in allTeams)
        {
            var members = byTeam[team];
            if (members.Count < 4) continue;
            // Lead (index 0) manages indexes 1-3
            for (int i = 1; i <= Math.Min(4, members.Count - 1); i++)
                RT(members[i], members[0]);
            // Sub-leads (indexes 1-2) each manage 2-3 ICs
            if (members.Count > 7)
            {
                for (int i = 5; i <= Math.Min(7, members.Count - 1); i++)
                    RT(members[i], members[1]);
            }
        }
    }
}
