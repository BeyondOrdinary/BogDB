using System.Text.Json;
using BogDb.Core.Common;
using BogDb.Core.Main;
using BogDb.Mcp.Server.Services;
using Xunit;

namespace BogDb.Tests.Mcp;

/// <summary>
/// Exercises the curated code-intelligence tools against a hand-seeded BO-shaped
/// graph (BoV01). Seeding the edges directly lets us verify code_dependencies
/// end to end without depending on the BO symbol-dependency extractor.
/// </summary>
public sealed class CodeIntelligenceQueryServiceTests
{
    private static CodeIntelligenceQueryToolService NewService() => new(new BogDbQueryToolService());

    private static JsonElement Args(object value)
        => JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone();

    private static void SeedGraph(string dbPath)
    {
        Directory.CreateDirectory(dbPath);
        using var db = BogDatabase.Open(dbPath);
        using var conn = new BogConnection(db);

        // Schema
        conn.BeginWriteTransaction();
        conn.EnsureNodeTable("File", new Dictionary<string, LogicalTypeID>
        {
            ["id"] = LogicalTypeID.STRING,
            ["normalized_path"] = LogicalTypeID.STRING
        });
        conn.EnsureNodeTable("Symbol", new Dictionary<string, LogicalTypeID>
        {
            ["id"] = LogicalTypeID.STRING,
            ["qualified_name"] = LogicalTypeID.STRING,
            ["display_name"] = LogicalTypeID.STRING,
            ["kind"] = LogicalTypeID.STRING,
            ["signature"] = LogicalTypeID.STRING,
            ["file_id"] = LogicalTypeID.STRING,
            ["declaration_line"] = LogicalTypeID.STRING,
            ["is_exported"] = LogicalTypeID.STRING
        });
        conn.EnsureNodeTable("RefactorPressureScore", new Dictionary<string, LogicalTypeID>
        {
            ["id"] = LogicalTypeID.STRING,
            ["target_id"] = LogicalTypeID.STRING,
            ["score"] = LogicalTypeID.STRING,
            ["recommendation"] = LogicalTypeID.STRING,
            ["drivers_json"] = LogicalTypeID.STRING,
            ["fired_gates_json"] = LogicalTypeID.STRING
        });
        conn.EnsureRelTable("DEFINES_SYMBOL", "File", "Symbol", new Dictionary<string, LogicalTypeID> { ["id"] = LogicalTypeID.STRING });
        conn.EnsureRelTable("CALLS", "Symbol", "Symbol", new Dictionary<string, LogicalTypeID>
        {
            ["id"] = LogicalTypeID.STRING,
            ["relation_type"] = LogicalTypeID.STRING,
            ["evidence"] = LogicalTypeID.STRING,
            ["confidence"] = LogicalTypeID.STRING
        });
        conn.EnsureRelTable("HAS_RPS", "File", "RefactorPressureScore", new Dictionary<string, LogicalTypeID> { ["id"] = LogicalTypeID.STRING });
        conn.Commit();

        // Data
        conn.BeginWriteTransaction();
        Node(conn, "File", "file:1", new() { ["normalized_path"] = "Orders/OrderService.cs" });
        Node(conn, "File", "file:2", new() { ["normalized_path"] = "Greeting/Greeter.cs" });
        Sym(conn, "sym:os", "Demo.OrderService", "OrderService", "class", "public sealed class OrderService", "file:1");
        Sym(conn, "sym:po", "Demo.OrderService.PlaceOrder", "PlaceOrder", "method", "public string PlaceOrder(string c)", "file:1");
        Sym(conn, "sym:gr", "Demo.Greeter.Greet", "Greet", "method", "public string Greet(string n)", "file:2");
        Sym(conn, "sym:wd", "Demo.Widget", "Widget", "class", "public class Widget", "file:2");
        Edge(conn, "DEFINES_SYMBOL", "file:1", "sym:os", new());
        Edge(conn, "DEFINES_SYMBOL", "file:1", "sym:po", new());
        Edge(conn, "DEFINES_SYMBOL", "file:2", "sym:gr", new());
        Edge(conn, "DEFINES_SYMBOL", "file:2", "sym:wd", new());
        Edge(conn, "CALLS", "sym:po", "sym:gr", new() { ["relation_type"] = "call", ["evidence"] = "g.Greet(c)", ["confidence"] = "0.9" });
        Node(conn, "RefactorPressureScore", "rps:1", new() { ["target_id"] = "file:1", ["score"] = "72.5", ["recommendation"] = "split", ["drivers_json"] = "high_complexity" });
        Node(conn, "RefactorPressureScore", "rps:2", new() { ["target_id"] = "file:2", ["score"] = "10.0", ["recommendation"] = "none", ["drivers_json"] = "" });
        Edge(conn, "HAS_RPS", "file:1", "rps:1", new());
        Edge(conn, "HAS_RPS", "file:2", "rps:2", new());
        conn.Commit();
    }

    private static void Node(BogConnection conn, string table, string id, Dictionary<string, object> props)
    {
        props["id"] = id;
        conn.UpsertNodeById(table, id, props);
    }

    private static void Sym(BogConnection conn, string id, string qn, string display, string kind, string sig, string fileId)
        => Node(conn, "Symbol", id, new()
        {
            ["qualified_name"] = qn,
            ["display_name"] = display,
            ["kind"] = kind,
            ["signature"] = sig,
            ["file_id"] = fileId,
            ["declaration_line"] = "1",
            ["is_exported"] = "true"
        });

    private static void Edge(BogConnection conn, string table, string from, string to, Dictionary<string, object> props)
    {
        props["id"] = $"edge:{from}:{table}:{to}";
        conn.UpsertRelationshipById(table, from, to, props);
    }

    private static async Task WithGraphAsync(Func<CodeIntelligenceQueryToolService, string, Task> body)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bogdb-code-{Guid.NewGuid():N}");
        try
        {
            SeedGraph(dbPath);
            await body(NewService(), dbPath);
        }
        finally
        {
            if (Directory.Exists(dbPath))
                Directory.Delete(dbPath, recursive: true);
        }
    }

    private static IReadOnlyList<string> Column(QueryToolResult result, string key)
        => result.Rows.Select(r => r.TryGetValue(key, out var v) ? v?.ToString() ?? string.Empty : string.Empty).ToList();

    [Fact]
    public Task SymbolSearch_FindsByNameSubstring() => WithGraphAsync(async (svc, db) =>
    {
        var result = await svc.SearchSymbolsAsync(Args(new { databasePath = db, query = "order" }), CancellationToken.None);
        Assert.True(result.Success);
        var names = Column(result, "qualified_name");
        Assert.Contains("Demo.OrderService", names);
        Assert.Contains("Demo.OrderService.PlaceOrder", names);
        Assert.DoesNotContain("Demo.Widget", names);
    });

    [Fact]
    public async Task SymbolSearch_DefaultsDatabasePathToWorkspaceGraph()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"bogdb-ws-{Guid.NewGuid():N}");
        try
        {
            SeedGraph(Path.Combine(ws, ".bo", "graph"));
            var svc = new CodeIntelligenceQueryToolService(new BogDbQueryToolService(), ws);
            // No databasePath argument — resolves to <ws>/.bo/graph.
            var result = await svc.SearchSymbolsAsync(Args(new { query = "order" }), CancellationToken.None);
            Assert.True(result.Success);
            Assert.Contains("Demo.OrderService", Column(result, "qualified_name"));
        }
        finally
        {
            if (Directory.Exists(ws))
                Directory.Delete(ws, recursive: true);
        }
    }

    [Fact]
    public Task SymbolSearch_KindFilter() => WithGraphAsync(async (svc, db) =>
    {
        var result = await svc.SearchSymbolsAsync(Args(new { databasePath = db, query = "order", kind = "class" }), CancellationToken.None);
        var names = Column(result, "qualified_name");
        Assert.Equal(new[] { "Demo.OrderService" }, names);
    });

    [Fact]
    public Task SymbolSearch_IncludesFileAndSignature() => WithGraphAsync(async (svc, db) =>
    {
        var result = await svc.SearchSymbolsAsync(Args(new { databasePath = db, query = "PlaceOrder" }), CancellationToken.None);
        var row = Assert.Single(result.Rows);
        Assert.Equal("public string PlaceOrder(string c)", row["signature"]?.ToString());
        Assert.Equal("Orders/OrderService.cs", row["file"]?.ToString());
    });

    [Fact]
    public Task Dependencies_OutFromSymbol() => WithGraphAsync(async (svc, db) =>
    {
        var result = await svc.QueryDependenciesAsync(Args(new { databasePath = db, symbol = "PlaceOrder", direction = "out" }), CancellationToken.None);
        var row = Assert.Single(result.Rows);
        Assert.Equal("Demo.OrderService.PlaceOrder", row["from"]?.ToString());
        Assert.Equal("Demo.Greeter.Greet", row["to"]?.ToString());
        Assert.Equal("call", row["relation"]?.ToString());
        Assert.Equal("out", row["direction"]?.ToString());
    });

    [Fact]
    public Task Dependencies_InToSymbol() => WithGraphAsync(async (svc, db) =>
    {
        var result = await svc.QueryDependenciesAsync(Args(new { databasePath = db, symbol = "Greet", direction = "in" }), CancellationToken.None);
        var row = Assert.Single(result.Rows);
        Assert.Equal("Demo.OrderService.PlaceOrder", row["from"]?.ToString());
        Assert.Equal("in", row["direction"]?.ToString());
    });

    [Fact]
    public Task Dependencies_ByFile() => WithGraphAsync(async (svc, db) =>
    {
        var result = await svc.QueryDependenciesAsync(Args(new { databasePath = db, file = "OrderService.cs", direction = "out" }), CancellationToken.None);
        var row = Assert.Single(result.Rows);
        Assert.Equal("Demo.Greeter.Greet", row["to"]?.ToString());
    });

    [Fact]
    public Task Dependencies_RequiresSymbolOrFile() => WithGraphAsync(async (svc, db) =>
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.QueryDependenciesAsync(Args(new { databasePath = db, direction = "both" }), CancellationToken.None));
    });

    [Fact]
    public Task Dependencies_InvalidDirection() => WithGraphAsync(async (svc, db) =>
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.QueryDependenciesAsync(Args(new { databasePath = db, symbol = "PlaceOrder", direction = "sideways" }), CancellationToken.None));
    });

    [Fact]
    public Task RefactorHotspots_RanksByScoreDescending() => WithGraphAsync(async (svc, db) =>
    {
        var result = await svc.QueryRefactorHotspotsAsync(Args(new { databasePath = db }), CancellationToken.None);
        var files = Column(result, "file");
        Assert.Equal(new[] { "Orders/OrderService.cs", "Greeting/Greeter.cs" }, files); // 72.5 before 10.0
    });

    [Fact]
    public Task RefactorHotspots_MinScoreFilter() => WithGraphAsync(async (svc, db) =>
    {
        var result = await svc.QueryRefactorHotspotsAsync(Args(new { databasePath = db, minScore = 50.0 }), CancellationToken.None);
        var files = Column(result, "file");
        Assert.Equal(new[] { "Orders/OrderService.cs" }, files);
    });

    [Fact]
    public Task RefactorHotspots_RecommendationFilter() => WithGraphAsync(async (svc, db) =>
    {
        var result = await svc.QueryRefactorHotspotsAsync(Args(new { databasePath = db, recommendation = "split" }), CancellationToken.None);
        var files = Column(result, "file");
        Assert.Equal(new[] { "Orders/OrderService.cs" }, files);
    });
}
