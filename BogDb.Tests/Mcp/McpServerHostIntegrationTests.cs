using System.Text;
using System.Text.Json;
using BogDb.Core.Common;
using BogDb.Core.Main;
using BogDb.Mcp.Server;
using Xunit;

namespace BogDb.Tests.Mcp;

public sealed class McpServerHostIntegrationTests
{
    [Fact]
    public async Task McpServerHost_HandlesInitializeToolsListAndQueryOverFramedStdio()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bogdb-mcp-host-{Guid.NewGuid():N}");
        try
        {
            using (var db = BogDatabase.Open(dbPath))
            using (var conn = new BogConnection(db))
            {
                conn.BeginWriteTransaction();
                conn.EnsureNodeTable("Person", new Dictionary<string, LogicalTypeID>
                {
                    ["id"] = LogicalTypeID.STRING,
                    ["name"] = LogicalTypeID.STRING
                });
                conn.UpsertNodeById("Person", "p1", new Dictionary<string, object>
                {
                    ["id"] = "p1",
                    ["name"] = "Ada"
                });
                conn.Commit();
            }

            var queryCallJson =
                "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"bogdb_query\",\"arguments\":{\"databasePath\":\"" +
                EscapeJson(dbPath) +
                "\",\"cypher\":\"MATCH (p:Person) RETURN p.id AS id, p.name AS name\"}}}";

            var inputPayload = string.Concat(
                Frame("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}"""),
                Frame("""{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}"""),
                Frame(queryCallJson));

            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(inputPayload));
            await using var output = new MemoryStream();

            var host = new McpServerHost();
            await host.RunAsync(input, output, CancellationToken.None);

            output.Position = 0;
            var responses = await ReadAllResponsesAsync(output);

            Assert.Equal(3, responses.Count);

            Assert.Equal("bogdb-ng", responses[0].RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());

            var tools = responses[1].RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
            Assert.Contains(tools, tool => tool.GetProperty("name").GetString() == "bogdb_query");
            Assert.Contains(tools, tool => tool.GetProperty("name").GetString() == "bogdb_schema");
            Assert.Contains(tools, tool => tool.GetProperty("name").GetString() == "ox_ax_ix_awaiting_local_vx");
            Assert.DoesNotContain(tools, tool => tool.GetProperty("name").GetString() == "orchestration_acceptance_ingests_awaiting_local_verification");
            foreach (var tool in tools)
            {
                var name = tool.GetProperty("name").GetString();
                Assert.NotNull(name);
                Assert.True($"bogdb__{name}".Length <= 64, $"Tool name '{name}' exceeds the prefixed MCP 64-char limit.");
            }

            var structured = responses[2].RootElement.GetProperty("result").GetProperty("structuredContent");
            Assert.True(structured.GetProperty("success").GetBoolean());
            var rows = structured.GetProperty("rows").EnumerateArray().ToArray();
            Assert.Single(rows);
            Assert.Equal("p1", rows[0].GetProperty("id").GetString());
            Assert.Equal("Ada", rows[0].GetProperty("name").GetString());
        }
        finally
        {
            if (Directory.Exists(dbPath))
                Directory.Delete(dbPath, recursive: true);
        }
    }

    [Fact]
    public async Task McpServerHost_BogDbQueryReportsTruncationOverFramedStdio()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bogdb-mcp-host-{Guid.NewGuid():N}");
        try
        {
            using (var db = BogDatabase.Open(dbPath))
            using (var conn = new BogConnection(db))
            {
                conn.BeginWriteTransaction();
                conn.EnsureNodeTable("Person", new Dictionary<string, LogicalTypeID>
                {
                    ["id"] = LogicalTypeID.STRING,
                    ["name"] = LogicalTypeID.STRING
                });
                conn.UpsertNodeById("Person", "p1", new Dictionary<string, object> { ["id"] = "p1", ["name"] = "Ada" });
                conn.UpsertNodeById("Person", "p2", new Dictionary<string, object> { ["id"] = "p2", ["name"] = "Grace" });
                conn.UpsertNodeById("Person", "p3", new Dictionary<string, object> { ["id"] = "p3", ["name"] = "Linus" });
                conn.Commit();
            }

            var queryCallJson =
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"bogdb_query\",\"arguments\":{\"databasePath\":\"" +
                EscapeJson(dbPath) +
                "\",\"cypher\":\"MATCH (p:Person) RETURN p.id AS id, p.name AS name ORDER BY id\",\"rowLimit\":2}}}";

            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(Frame(queryCallJson)));
            await using var output = new MemoryStream();

            var host = new McpServerHost();
            await host.RunAsync(input, output, CancellationToken.None);

            output.Position = 0;
            var responses = await ReadAllResponsesAsync(output);

            Assert.Single(responses);

            var structured = responses[0].RootElement.GetProperty("result").GetProperty("structuredContent");
            Assert.True(structured.GetProperty("success").GetBoolean());
            Assert.True(structured.GetProperty("truncated").GetBoolean());

            var rows = structured.GetProperty("rows").EnumerateArray().ToArray();
            Assert.Equal(2, rows.Length);
            Assert.Equal("p1", rows[0].GetProperty("id").GetString());
            Assert.Equal("p2", rows[1].GetProperty("id").GetString());
        }
        finally
        {
            if (Directory.Exists(dbPath))
                Directory.Delete(dbPath, recursive: true);
        }
    }

    [Fact]
    public async Task McpServerHost_BogDbQueryReturnsStructuredErrorOverFramedStdio()
    {
        var queryCallJson =
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"bogdb_query\",\"arguments\":{\"databasePath\":\":memory:\",\"cypher\":\"CREATE (:Person {id:'p1'})\"}}}";

        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(Frame(queryCallJson)));
        await using var output = new MemoryStream();

        var host = new McpServerHost();
        await host.RunAsync(input, output, CancellationToken.None);

        output.Position = 0;
        var responses = await ReadAllResponsesAsync(output);

        Assert.Single(responses);

        var root = responses[0].RootElement;
        Assert.True(root.TryGetProperty("error", out var error));
        Assert.Equal(-32000, error.GetProperty("code").GetInt32());
        Assert.Contains("read-only", error.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task McpServerHost_BogDbTableInfoReturnsSchemaMetadataOverFramedStdio()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bogdb-mcp-host-{Guid.NewGuid():N}");
        try
        {
            using (var db = BogDatabase.Open(dbPath))
            using (var conn = new BogConnection(db))
            {
                conn.BeginWriteTransaction();
                conn.EnsureNodeTable("Person", new Dictionary<string, LogicalTypeID>
                {
                    ["id"] = LogicalTypeID.STRING,
                    ["name"] = LogicalTypeID.STRING,
                    ["age"] = LogicalTypeID.INT64
                });
                conn.Commit();
            }

            var tableInfoJson =
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"bogdb_table_info\",\"arguments\":{\"databasePath\":\"" +
                EscapeJson(dbPath) +
                "\",\"tableName\":\"Person\"}}}";

            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(Frame(tableInfoJson)));
            await using var output = new MemoryStream();

            var host = new McpServerHost();
            await host.RunAsync(input, output, CancellationToken.None);

            output.Position = 0;
            var responses = await ReadAllResponsesAsync(output);

            Assert.Single(responses);

            var structured = responses[0].RootElement.GetProperty("result").GetProperty("structuredContent");
            Assert.Equal("Person", structured.GetProperty("name").GetString());
            Assert.Equal("node", structured.GetProperty("kind").GetString());
            Assert.Equal("id", structured.GetProperty("primaryKey").GetString());

            var properties = structured.GetProperty("properties").EnumerateArray().ToArray();
            Assert.Contains(properties, property => property.GetProperty("name").GetString() == "id");
            Assert.Contains(properties, property => property.GetProperty("name").GetString() == "name");
            Assert.Contains(properties, property => property.GetProperty("name").GetString() == "age");
        }
        finally
        {
            if (Directory.Exists(dbPath))
                Directory.Delete(dbPath, recursive: true);
        }
    }

    [Fact]
    public async Task McpServerHost_ListsAndReadsBoHandoffResourcesOverFramedStdio()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"bogdb-mcp-handoff-{Guid.NewGuid():N}");
        try
        {
            var handoffDirectory = Path.Combine(workspaceRoot, ".bo", "handoffs");
            Directory.CreateDirectory(handoffDirectory);

            var envelopePath = Path.Combine(handoffDirectory, "aspect_extract.json");
            await File.WriteAllTextAsync(envelopePath, """
                {
                  "protocol_version": "bo.agent-handoff-artifact/0.1",
                  "artifact_id": "handoff:aspect_extract:demo",
                  "resource_uri": "bo://handoffs/aspect_extract/.bo/handoffs/aspect_extract.json",
                  "handoff_kind": "aspect_extract",
                  "generated_at_utc": "2026-04-17T12:00:00Z",
                  "source_command": "bo aspect extract demo --handoff-file .bo/handoffs/aspect_extract.json",
                  "workspace_root": "/tmp/demo",
                  "relative_path": ".bo/handoffs/aspect_extract.json",
                  "handoff": {
                    "recommended_workflow": "direct_extract"
                  }
                }
                """);

            var indexPath = Path.Combine(handoffDirectory, "index.json");
            await File.WriteAllTextAsync(indexPath, """
                {
                  "protocol_version": "bo.agent-handoff-index/0.1",
                  "generated_at_utc": "2026-04-17T12:00:00Z",
                  "resources": [
                    {
                      "artifact_id": "handoff:aspect_extract:demo",
                      "resource_uri": "bo://handoffs/aspect_extract/.bo/handoffs/aspect_extract.json",
                      "handoff_kind": "aspect_extract",
                      "generated_at_utc": "2026-04-17T12:00:00Z",
                      "source_command": "bo aspect extract demo --handoff-file .bo/handoffs/aspect_extract.json",
                      "relative_path": ".bo/handoffs/aspect_extract.json",
                      "producer": "bo"
                    }
                  ]
                }
                """);

            var inputPayload = string.Concat(
                Frame("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}"""),
                Frame("""{"jsonrpc":"2.0","id":2,"method":"resources/list","params":{}}"""),
                Frame("""{"jsonrpc":"2.0","id":3,"method":"resources/read","params":{"uri":"bo://handoffs/aspect_extract/.bo/handoffs/aspect_extract.json"}}"""));

            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(inputPayload));
            await using var output = new MemoryStream();

            var host = new McpServerHost(workspaceRoot);
            await host.RunAsync(input, output, CancellationToken.None);

            output.Position = 0;
            var responses = await ReadAllResponsesAsync(output);

            Assert.Equal(3, responses.Count);

            var capabilities = responses[0].RootElement.GetProperty("result").GetProperty("capabilities");
            Assert.True(capabilities.TryGetProperty("resources", out var resourcesCapability));
            Assert.False(resourcesCapability.GetProperty("subscribe").GetBoolean());

            var resources = responses[1].RootElement.GetProperty("result").GetProperty("resources").EnumerateArray().ToArray();
            Assert.Single(resources);
            Assert.Equal("bo://handoffs/aspect_extract/.bo/handoffs/aspect_extract.json", resources[0].GetProperty("uri").GetString());

            var contents = responses[2].RootElement.GetProperty("result").GetProperty("contents").EnumerateArray().ToArray();
            Assert.Single(contents);
            Assert.Equal("application/json", contents[0].GetProperty("mimeType").GetString());

            using var envelope = JsonDocument.Parse(contents[0].GetProperty("text").GetString()!);
            Assert.Equal("handoff:aspect_extract:demo", envelope.RootElement.GetProperty("artifact_id").GetString());
            Assert.Equal("direct_extract", envelope.RootElement.GetProperty("handoff").GetProperty("recommended_workflow").GetString());
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
                Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task McpServerHost_HandoffQueryCanReturnLatestForTargetAgent()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"bogdb-mcp-handoff-query-{Guid.NewGuid():N}");
        try
        {
            var handoffDirectory = Path.Combine(workspaceRoot, ".bo", "handoffs");
            Directory.CreateDirectory(handoffDirectory);

            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "index.json"), """
                {
                  "protocol_version": "bo.agent-handoff-index/0.1",
                  "generated_at_utc": "2026-04-17T12:00:00Z",
                  "resources": [
                    {
                      "artifact_id": "handoff:one",
                      "resource_uri": "bo://handoffs/aspect_extract/.bo/handoffs/one.json",
                      "handoff_kind": "aspect_extract",
                      "generated_at_utc": "2026-04-17T11:00:00Z",
                      "relative_path": ".bo/handoffs/one.json",
                      "producer": "bo",
                      "created_by_agent_uid": "alpha",
                      "target_agent_uid": "foo"
                    },
                    {
                      "artifact_id": "handoff:two",
                      "resource_uri": "bo://handoffs/aspect_extract/.bo/handoffs/two.json",
                      "handoff_kind": "aspect_extract",
                      "generated_at_utc": "2026-04-17T12:30:00Z",
                      "relative_path": ".bo/handoffs/two.json",
                      "producer": "bo",
                      "created_by_agent_uid": "beta",
                      "target_agent_uid": "foo"
                    },
                    {
                      "artifact_id": "handoff:three",
                      "resource_uri": "bo://handoffs/aspect_search/.bo/handoffs/three.json",
                      "handoff_kind": "aspect_search",
                      "generated_at_utc": "2026-04-17T12:15:00Z",
                      "relative_path": ".bo/handoffs/three.json",
                      "producer": "other",
                      "created_by_agent_uid": "gamma",
                      "target_agent_uid": "bar"
                    }
                  ]
                }
                """);

            var queryJson =
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"handoff_query","arguments":{"targetAgentUid":"foo","latestOnly":true}}}""";

            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(Frame(queryJson)));
            await using var output = new MemoryStream();

            var host = new McpServerHost(workspaceRoot);
            await host.RunAsync(input, output, CancellationToken.None);

            output.Position = 0;
            var responses = await ReadAllResponsesAsync(output);

            Assert.Single(responses);

            var structured = responses[0].RootElement.GetProperty("result").GetProperty("structuredContent");
            Assert.Equal(2, structured.GetProperty("totalMatches").GetInt32());
            Assert.Equal(1, structured.GetProperty("returnedCount").GetInt32());

            var entries = structured.GetProperty("entries").EnumerateArray().ToArray();
            Assert.Single(entries);
            Assert.Equal("handoff:two", entries[0].GetProperty("artifactId").GetString());
            Assert.Equal("foo", entries[0].GetProperty("targetAgentUid").GetString());
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
                Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task McpServerHost_HandoffQuerySupportsNeutralHandoffsIndexPath()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"bogdb-mcp-neutral-handoffs-{Guid.NewGuid():N}");
        try
        {
            var handoffDirectory = Path.Combine(workspaceRoot, ".handoffs");
            Directory.CreateDirectory(handoffDirectory);

            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "index.json"), """
                {
                  "protocol_version": "handoff-index/0.1",
                  "generated_at_utc": "2026-04-17T12:00:00Z",
                  "resources": [
                    {
                      "artifact_id": "handoff:neutral:one",
                      "resource_uri": "handoff://generic/one",
                      "handoff_kind": "generic",
                      "generated_at_utc": "2026-04-17T12:10:00Z",
                      "relative_path": ".handoffs/one.json",
                      "producer": "generic",
                      "created_by_agent_uid": "alpha",
                      "target_agent_uid": "bar"
                    }
                  ]
                }
                """);

            var queryJson =
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"handoff_query","arguments":{"targetAgentUid":"bar","latestOnly":true}}}""";

            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(Frame(queryJson)));
            await using var output = new MemoryStream();

            var host = new McpServerHost(workspaceRoot);
            await host.RunAsync(input, output, CancellationToken.None);

            output.Position = 0;
            var responses = await ReadAllResponsesAsync(output);

            Assert.Single(responses);

            var structured = responses[0].RootElement.GetProperty("result").GetProperty("structuredContent");
            Assert.Equal(1, structured.GetProperty("totalMatches").GetInt32());
            var entries = structured.GetProperty("entries").EnumerateArray().ToArray();
            Assert.Single(entries);
            Assert.Equal("handoff:neutral:one", entries[0].GetProperty("artifactId").GetString());
            Assert.Equal("generic", entries[0].GetProperty("producer").GetString());
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
                Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task McpServerHost_HandoffQuerySupportsLatestForTargetAgentShortcut()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"bogdb-mcp-latest-target-{Guid.NewGuid():N}");
        try
        {
            var handoffDirectory = Path.Combine(workspaceRoot, ".handoffs");
            Directory.CreateDirectory(handoffDirectory);

            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "index.json"), """
                {
                  "protocol_version": "handoff-index/0.1",
                  "generated_at_utc": "2026-04-17T12:00:00Z",
                  "resources": [
                    {
                      "artifact_id": "handoff:older",
                      "resource_uri": "handoff://generic/older",
                      "handoff_kind": "generic",
                      "generated_at_utc": "2026-04-17T11:00:00Z",
                      "relative_path": ".handoffs/older.json",
                      "producer": "generic",
                      "created_by_agent_uid": "alpha",
                      "target_agent_uid": "foo"
                    },
                    {
                      "artifact_id": "handoff:newer",
                      "resource_uri": "handoff://generic/newer",
                      "handoff_kind": "generic",
                      "generated_at_utc": "2026-04-17T12:45:00Z",
                      "relative_path": ".handoffs/newer.json",
                      "producer": "generic",
                      "created_by_agent_uid": "beta",
                      "target_agent_uid": "foo"
                    }
                  ]
                }
                """);

            var queryJson =
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"handoff_query","arguments":{"latestForTargetAgentUid":"foo"}}}""";

            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(Frame(queryJson)));
            await using var output = new MemoryStream();

            var host = new McpServerHost(workspaceRoot);
            await host.RunAsync(input, output, CancellationToken.None);

            output.Position = 0;
            var responses = await ReadAllResponsesAsync(output);

            Assert.Single(responses);

            var structured = responses[0].RootElement.GetProperty("result").GetProperty("structuredContent");
            Assert.Equal(2, structured.GetProperty("totalMatches").GetInt32());
            Assert.Equal(1, structured.GetProperty("returnedCount").GetInt32());
            var entry = structured.GetProperty("entries").EnumerateArray().Single();
            Assert.Equal("handoff:newer", entry.GetProperty("artifactId").GetString());
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
                Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task McpServerHost_HandoffQuerySupportsLatestBetweenAgentsShortcut()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"bogdb-mcp-latest-between-{Guid.NewGuid():N}");
        try
        {
            var handoffDirectory = Path.Combine(workspaceRoot, ".handoffs");
            Directory.CreateDirectory(handoffDirectory);

            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "index.json"), """
                {
                  "protocol_version": "handoff-index/0.1",
                  "generated_at_utc": "2026-04-17T12:00:00Z",
                  "resources": [
                    {
                      "artifact_id": "handoff:first",
                      "resource_uri": "handoff://generic/first",
                      "handoff_kind": "generic",
                      "generated_at_utc": "2026-04-17T11:10:00Z",
                      "relative_path": ".handoffs/first.json",
                      "producer": "generic",
                      "created_by_agent_uid": "alpha",
                      "target_agent_uid": "bar"
                    },
                    {
                      "artifact_id": "handoff:latest-between",
                      "resource_uri": "handoff://generic/latest-between",
                      "handoff_kind": "generic",
                      "generated_at_utc": "2026-04-17T12:50:00Z",
                      "relative_path": ".handoffs/latest-between.json",
                      "producer": "generic",
                      "created_by_agent_uid": "bar",
                      "target_agent_uid": "alpha"
                    },
                    {
                      "artifact_id": "handoff:other",
                      "resource_uri": "handoff://generic/other",
                      "handoff_kind": "generic",
                      "generated_at_utc": "2026-04-17T12:55:00Z",
                      "relative_path": ".handoffs/other.json",
                      "producer": "generic",
                      "created_by_agent_uid": "alpha",
                      "target_agent_uid": "charlie"
                    }
                  ]
                }
                """);

            var queryJson =
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"handoff_query","arguments":{"latestBetweenAgentAUid":"alpha","latestBetweenAgentBUid":"bar"}}}""";

            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(Frame(queryJson)));
            await using var output = new MemoryStream();

            var host = new McpServerHost(workspaceRoot);
            await host.RunAsync(input, output, CancellationToken.None);

            output.Position = 0;
            var responses = await ReadAllResponsesAsync(output);

            Assert.Single(responses);

            var structured = responses[0].RootElement.GetProperty("result").GetProperty("structuredContent");
            Assert.Equal(2, structured.GetProperty("totalMatches").GetInt32());
            Assert.Equal(1, structured.GetProperty("returnedCount").GetInt32());
            var entry = structured.GetProperty("entries").EnumerateArray().Single();
            Assert.Equal("handoff:latest-between", entry.GetProperty("artifactId").GetString());
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
                Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task McpServerHost_HandoffQuerySupportsLatestActionableForTargetAgentShortcut()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"bogdb-mcp-latest-actionable-{Guid.NewGuid():N}");
        try
        {
            var handoffDirectory = Path.Combine(workspaceRoot, ".handoffs");
            Directory.CreateDirectory(handoffDirectory);

            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "index.json"), """
                {
                  "protocol_version": "handoff-index/0.1",
                  "generated_at_utc": "2026-04-17T12:00:00Z",
                  "resources": [
                    {
                      "artifact_id": "handoff:blocked",
                      "resource_uri": "handoff://generic/blocked",
                      "handoff_kind": "generic",
                      "generated_at_utc": "2026-04-17T12:40:00Z",
                      "relative_path": ".handoffs/blocked.json",
                      "producer": "generic",
                      "created_by_agent_uid": "alpha",
                      "target_agent_uid": "foo",
                      "status": "blocked",
                      "blocker_codes": ["awaiting_contract"]
                    },
                    {
                      "artifact_id": "handoff:actionable",
                      "resource_uri": "handoff://generic/actionable",
                      "handoff_kind": "generic",
                      "generated_at_utc": "2026-04-17T12:50:00Z",
                      "relative_path": ".handoffs/actionable.json",
                      "producer": "generic",
                      "created_by_agent_uid": "beta",
                      "target_agent_uid": "foo",
                      "status": "ready",
                      "actionability_score": 0.9
                    },
                    {
                      "artifact_id": "handoff:completed",
                      "resource_uri": "handoff://generic/completed",
                      "handoff_kind": "generic",
                      "generated_at_utc": "2026-04-17T12:55:00Z",
                      "relative_path": ".handoffs/completed.json",
                      "producer": "generic",
                      "created_by_agent_uid": "gamma",
                      "target_agent_uid": "foo",
                      "status": "completed",
                      "actionability_score": 0.1
                    }
                  ]
                }
                """);

            var queryJson =
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"handoff_query","arguments":{"latestActionableForTargetAgentUid":"foo"}}}""";

            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(Frame(queryJson)));
            await using var output = new MemoryStream();

            var host = new McpServerHost(workspaceRoot);
            await host.RunAsync(input, output, CancellationToken.None);

            output.Position = 0;
            var responses = await ReadAllResponsesAsync(output);

            Assert.Single(responses);

            var structured = responses[0].RootElement.GetProperty("result").GetProperty("structuredContent");
            Assert.Equal(1, structured.GetProperty("returnedCount").GetInt32());
            var entry = structured.GetProperty("entries").EnumerateArray().Single();
            Assert.Equal("handoff:actionable", entry.GetProperty("artifactId").GetString());
            Assert.Equal("ready", entry.GetProperty("status").GetString());
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
                Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task McpServerHost_HandoffQuerySupportsLatestReadyForTargetAgentShortcut()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"bogdb-mcp-latest-ready-{Guid.NewGuid():N}");
        try
        {
            var handoffDirectory = Path.Combine(workspaceRoot, ".bo", "handoffs");
            Directory.CreateDirectory(handoffDirectory);

            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "index.json"), """
                {
                  "protocol_version": "bo.agent-handoff-index/0.1",
                  "generated_at_utc": "2026-04-18T17:00:00Z",
                  "resources": [
                    {
                      "artifact_id": "handoff:orchestration_acceptance_verification_followup:older",
                      "resource_uri": "bo://handoffs/orchestration_acceptance_verification_followup/.bo/handoffs/followup-older.json",
                      "handoff_kind": "orchestration_acceptance_verification_followup",
                      "generated_at_utc": "2026-04-18T17:10:00Z",
                      "relative_path": ".bo/handoffs/followup-older.json",
                      "producer": "bo",
                      "created_by_agent_uid": "lead-codex",
                      "target_agent_uid": "worker-verify",
                      "status": "ready",
                      "actionability_score": 1.0
                    },
                    {
                      "artifact_id": "handoff:orchestration_acceptance_verification_followup:newer",
                      "resource_uri": "bo://handoffs/orchestration_acceptance_verification_followup/.bo/handoffs/followup-newer.json",
                      "handoff_kind": "orchestration_acceptance_verification_followup",
                      "generated_at_utc": "2026-04-18T17:20:00Z",
                      "relative_path": ".bo/handoffs/followup-newer.json",
                      "producer": "bo",
                      "created_by_agent_uid": "lead-codex",
                      "target_agent_uid": "worker-verify",
                      "status": "ready",
                      "actionability_score": 1.0
                    },
                    {
                      "artifact_id": "handoff:orchestration_acceptance_verification_followup:blocked",
                      "resource_uri": "bo://handoffs/orchestration_acceptance_verification_followup/.bo/handoffs/followup-blocked.json",
                      "handoff_kind": "orchestration_acceptance_verification_followup",
                      "generated_at_utc": "2026-04-18T17:30:00Z",
                      "relative_path": ".bo/handoffs/followup-blocked.json",
                      "producer": "bo",
                      "created_by_agent_uid": "lead-codex",
                      "target_agent_uid": "worker-verify",
                      "status": "blocked",
                      "blocker_codes": ["awaiting_confirmation"]
                    }
                  ]
                }
                """);

            var queryJson =
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"handoff_query","arguments":{"handoffKind":"orchestration_acceptance_verification_followup","latestReadyForTargetAgentUid":"worker-verify"}}}""";

            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(Frame(queryJson)));
            await using var output = new MemoryStream();

            var host = new McpServerHost(workspaceRoot);
            await host.RunAsync(input, output, CancellationToken.None);

            output.Position = 0;
            var responses = await ReadAllResponsesAsync(output);

            Assert.Single(responses);

            var structured = responses[0].RootElement.GetProperty("result").GetProperty("structuredContent");
            Assert.Equal(1, structured.GetProperty("returnedCount").GetInt32());
            var entry = structured.GetProperty("entries").EnumerateArray().Single();
            Assert.Equal("handoff:orchestration_acceptance_verification_followup:newer", entry.GetProperty("artifactId").GetString());
            Assert.Equal("worker-verify", entry.GetProperty("targetAgentUid").GetString());
            Assert.Equal("ready", entry.GetProperty("status").GetString());
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
                Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task McpServerHost_HandoffQuerySupportsLatestReadyVerificationForTargetAgentShortcut()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"bogdb-mcp-latest-ready-verification-{Guid.NewGuid():N}");
        try
        {
            var handoffDirectory = Path.Combine(workspaceRoot, ".bo", "handoffs");
            Directory.CreateDirectory(handoffDirectory);

            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "verification-older.json"), """
                {
                  "handoff": {
                    "acceptance_id": "accept:gate:foundation",
                    "target_id": "gate:foundation:integration",
                    "target_kind": "gate"
                  }
                }
                """);
            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "verification-newer.json"), """
                {
                  "handoff": {
                    "acceptance_id": "accept:gate:foundation:review",
                    "target_id": "gate:foundation:review",
                    "target_kind": "gate"
                  }
                }
                """);

            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "index.json"), """
                {
                  "protocol_version": "bo.agent-handoff-index/0.1",
                  "generated_at_utc": "2026-04-18T18:00:00Z",
                  "resources": [
                    {
                      "artifact_id": "handoff:orchestration_acceptance_verification:older",
                      "resource_uri": "bo://handoffs/orchestration_acceptance_verification/.bo/handoffs/verification-older.json",
                      "handoff_kind": "orchestration_acceptance_verification",
                      "generated_at_utc": "2026-04-18T18:05:00Z",
                      "relative_path": ".bo/handoffs/verification-older.json",
                      "producer": "bo",
                      "created_by_agent_uid": "worker-verify",
                      "target_agent_uid": "worker-verify",
                      "status": "ready",
                      "actionability_score": 1.0
                    },
                    {
                      "artifact_id": "handoff:orchestration_acceptance_verification:newer",
                      "resource_uri": "bo://handoffs/orchestration_acceptance_verification/.bo/handoffs/verification-newer.json",
                      "handoff_kind": "orchestration_acceptance_verification",
                      "generated_at_utc": "2026-04-18T18:10:00Z",
                      "relative_path": ".bo/handoffs/verification-newer.json",
                      "producer": "bo",
                      "created_by_agent_uid": "worker-verify",
                      "target_agent_uid": "worker-verify",
                      "status": "ready",
                      "actionability_score": 1.0
                    }
                  ]
                }
                """);

            var queryJson =
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"handoff_query","arguments":{"latestReadyVerificationForTargetAgentUid":"worker-verify"}}}""";

            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(Frame(queryJson)));
            await using var output = new MemoryStream();

            var host = new McpServerHost(workspaceRoot);
            await host.RunAsync(input, output, CancellationToken.None);

            output.Position = 0;
            var responses = await ReadAllResponsesAsync(output);

            Assert.Single(responses);

            var structured = responses[0].RootElement.GetProperty("result").GetProperty("structuredContent");
            Assert.Equal(1, structured.GetProperty("returnedCount").GetInt32());
            var entry = structured.GetProperty("entries").EnumerateArray().Single();
            Assert.Equal("handoff:orchestration_acceptance_verification:newer", entry.GetProperty("artifactId").GetString());
            Assert.Equal("worker-verify", entry.GetProperty("targetAgentUid").GetString());
            Assert.Equal("ready", entry.GetProperty("status").GetString());
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
                Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task McpServerHost_HandoffQuerySupportsLatestReadyVerificationPickupForTargetAgentShortcut()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"bogdb-mcp-latest-ready-verification-pickup-{Guid.NewGuid():N}");
        try
        {
            var handoffDirectory = Path.Combine(workspaceRoot, ".bo", "handoffs");
            Directory.CreateDirectory(handoffDirectory);

            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "index.json"), """
                {
                  "protocol_version": "bo.agent-handoff-index/0.1",
                  "generated_at_utc": "2026-04-18T19:45:00Z",
                  "resources": [
                    {
                      "artifact_id": "handoff:orchestration_verification_pickup:older",
                      "resource_uri": "bo://handoffs/orchestration_verification_pickup/.bo/handoffs/verification-pickup-older.json",
                      "handoff_kind": "orchestration_verification_pickup",
                      "generated_at_utc": "2026-04-18T19:46:00Z",
                      "relative_path": ".bo/handoffs/verification-pickup-older.json",
                      "producer": "bo",
                      "target_agent_uid": "worker-verify",
                      "status": "ready",
                      "actionability_score": 1.0
                    },
                    {
                      "artifact_id": "handoff:orchestration_verification_pickup:newer",
                      "resource_uri": "bo://handoffs/orchestration_verification_pickup/.bo/handoffs/verification-pickup-newer.json",
                      "handoff_kind": "orchestration_verification_pickup",
                      "generated_at_utc": "2026-04-18T19:47:00Z",
                      "relative_path": ".bo/handoffs/verification-pickup-newer.json",
                      "producer": "bo",
                      "target_agent_uid": "worker-verify",
                      "status": "ready",
                      "actionability_score": 1.0
                    },
                    {
                      "artifact_id": "handoff:orchestration_verification_pickup:blocked",
                      "resource_uri": "bo://handoffs/orchestration_verification_pickup/.bo/handoffs/verification-pickup-blocked.json",
                      "handoff_kind": "orchestration_verification_pickup",
                      "generated_at_utc": "2026-04-18T19:48:00Z",
                      "relative_path": ".bo/handoffs/verification-pickup-blocked.json",
                      "producer": "bo",
                      "target_agent_uid": "worker-verify",
                      "status": "blocked",
                      "actionability_score": 0.0
                    }
                  ]
                }
                """);

            var queryJson =
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"handoff_query","arguments":{"latestReadyVerificationPickupForTargetAgentUid":"worker-verify"}}}""";

            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(Frame(queryJson)));
            await using var output = new MemoryStream();

            var host = new McpServerHost(workspaceRoot);
            await host.RunAsync(input, output, CancellationToken.None);

            output.Position = 0;
            var responses = await ReadAllResponsesAsync(output);

            Assert.Single(responses);

            var structured = responses[0].RootElement.GetProperty("result").GetProperty("structuredContent");
            Assert.Equal(1, structured.GetProperty("returnedCount").GetInt32());
            var entry = structured.GetProperty("entries").EnumerateArray().Single();
            Assert.Equal("handoff:orchestration_verification_pickup:newer", entry.GetProperty("artifactId").GetString());
            Assert.Equal("worker-verify", entry.GetProperty("targetAgentUid").GetString());
            Assert.Equal("ready", entry.GetProperty("status").GetString());
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
                Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task McpServerHost_HandoffQuerySupportsGroupedReadyVerificationPickupHandoffs()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"bogdb-mcp-grouped-verification-pickup-{Guid.NewGuid():N}");
        try
        {
            var handoffDirectory = Path.Combine(workspaceRoot, ".bo", "handoffs");
            Directory.CreateDirectory(handoffDirectory);

            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "pickup-foundation-1.json"), """
                {
                  "handoff": {
                    "selection_kind": "batch",
                    "dominant_pickup_pressure": "mostly relatedness",
                    "selection_reference": {
                      "best_group_key": "gate:foundation"
                    },
                    "pickup_factor_summary": {
                      "impact_score": 0.8,
                      "low_cost_score": 0.75,
                      "relatedness_score": 0.95,
                      "blocker_penalty_score": 0.9,
                      "age_urgency_score": 0.2
                    }
                  }
                }
                """);
            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "pickup-foundation-2.json"), """
                {
                  "handoff": {
                    "selection_kind": "batch",
                    "dominant_pickup_pressure": "mostly relatedness",
                    "selection_reference": {
                      "best_group_key": "gate:foundation"
                    },
                    "pickup_factor_summary": {
                      "impact_score": 0.78,
                      "low_cost_score": 0.72,
                      "relatedness_score": 0.94,
                      "blocker_penalty_score": 0.9,
                      "age_urgency_score": 0.25
                    }
                  }
                }
                """);
            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "pickup-backend-1.json"), """
                {
                  "handoff": {
                    "selection_kind": "single",
                    "dominant_pickup_pressure": "mostly low blocker pressure",
                    "selection_reference": {
                      "best_entry_artifact_id": "handoff:orchestration_acceptance_verification:backend-older"
                    },
                    "pickup_factor_summary": {
                      "impact_score": 0.4,
                      "low_cost_score": 1.0,
                      "relatedness_score": 0.4,
                      "blocker_penalty_score": 1.0,
                      "age_urgency_score": 0.2
                    }
                  }
                }
                """);

            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "index.json"), """
                {
                  "protocol_version": "bo.agent-handoff-index/0.1",
                  "generated_at_utc": "2026-04-18T19:45:00Z",
                  "resources": [
                    {
                      "artifact_id": "handoff:orchestration_verification_pickup:foundation-1",
                      "resource_uri": "bo://handoffs/orchestration_verification_pickup/.bo/handoffs/pickup-foundation-1.json",
                      "handoff_kind": "orchestration_verification_pickup",
                      "generated_at_utc": "2026-04-18T19:46:00Z",
                      "relative_path": ".bo/handoffs/pickup-foundation-1.json",
                      "producer": "bo",
                      "target_agent_uid": "worker-verify",
                      "status": "ready",
                      "actionability_score": 1.0
                    },
                    {
                      "artifact_id": "handoff:orchestration_verification_pickup:foundation-2",
                      "resource_uri": "bo://handoffs/orchestration_verification_pickup/.bo/handoffs/pickup-foundation-2.json",
                      "handoff_kind": "orchestration_verification_pickup",
                      "generated_at_utc": "2026-04-18T19:47:00Z",
                      "relative_path": ".bo/handoffs/pickup-foundation-2.json",
                      "producer": "bo",
                      "target_agent_uid": "worker-verify",
                      "status": "ready",
                      "actionability_score": 1.0
                    },
                    {
                      "artifact_id": "handoff:orchestration_verification_pickup:backend-1",
                      "resource_uri": "bo://handoffs/orchestration_verification_pickup/.bo/handoffs/pickup-backend-1.json",
                      "handoff_kind": "orchestration_verification_pickup",
                      "generated_at_utc": "2026-04-18T19:48:00Z",
                      "relative_path": ".bo/handoffs/pickup-backend-1.json",
                      "producer": "bo",
                      "target_agent_uid": "worker-verify",
                      "status": "ready",
                      "actionability_score": 1.0
                    }
                  ]
                }
                """);

            var queryJson =
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"handoff_query","arguments":{"groupReadyVerificationPickupHandoffsForTargetAgentUid":"worker-verify","limit":5}}}""";

            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(Frame(queryJson)));
            await using var output = new MemoryStream();

            var host = new McpServerHost(workspaceRoot);
            await host.RunAsync(input, output, CancellationToken.None);

            output.Position = 0;
            var responses = await ReadAllResponsesAsync(output);

            Assert.Single(responses);

            var structured = responses[0].RootElement.GetProperty("result").GetProperty("structuredContent");
            var groups = structured.GetProperty("groups").EnumerateArray().ToArray();
            Assert.Equal(2, groups.Length);
            Assert.Equal("gate:foundation", groups[0].GetProperty("groupKey").GetString());
            Assert.Equal(2, groups[0].GetProperty("memberCount").GetInt32());
            Assert.Equal("process_verification_pickup_group", groups[0].GetProperty("recommendedBatchAction").GetString());
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
                Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task McpServerHost_HandoffQuerySupportsBestReadyVerificationPickupWork()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"bogdb-mcp-best-verification-pickup-work-{Guid.NewGuid():N}");
        try
        {
            var handoffDirectory = Path.Combine(workspaceRoot, ".bo", "handoffs");
            Directory.CreateDirectory(handoffDirectory);

            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "pickup-foundation-1.json"), """
                {
                  "handoff": {
                    "selection_kind": "batch",
                    "dominant_pickup_pressure": "mostly relatedness",
                    "selection_reference": {
                      "best_group_key": "gate:foundation"
                    },
                    "pickup_factor_summary": {
                      "impact_score": 0.8,
                      "low_cost_score": 0.75,
                      "relatedness_score": 0.95,
                      "blocker_penalty_score": 0.9,
                      "age_urgency_score": 0.2
                    }
                  }
                }
                """);
            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "pickup-foundation-2.json"), """
                {
                  "handoff": {
                    "selection_kind": "batch",
                    "dominant_pickup_pressure": "mostly relatedness",
                    "selection_reference": {
                      "best_group_key": "gate:foundation"
                    },
                    "pickup_factor_summary": {
                      "impact_score": 0.78,
                      "low_cost_score": 0.72,
                      "relatedness_score": 0.94,
                      "blocker_penalty_score": 0.9,
                      "age_urgency_score": 0.25
                    }
                  }
                }
                """);
            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "pickup-backend-1.json"), """
                {
                  "handoff": {
                    "selection_kind": "single",
                    "dominant_pickup_pressure": "mostly low blocker pressure",
                    "selection_reference": {
                      "best_entry_artifact_id": "handoff:orchestration_acceptance_verification:backend-older"
                    },
                    "pickup_factor_summary": {
                      "impact_score": 0.4,
                      "low_cost_score": 1.0,
                      "relatedness_score": 0.4,
                      "blocker_penalty_score": 1.0,
                      "age_urgency_score": 0.2
                    }
                  }
                }
                """);

            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "index.json"), """
                {
                  "protocol_version": "bo.agent-handoff-index/0.1",
                  "generated_at_utc": "2026-04-18T19:45:00Z",
                  "resources": [
                    {
                      "artifact_id": "handoff:orchestration_verification_pickup:foundation-1",
                      "resource_uri": "bo://handoffs/orchestration_verification_pickup/.bo/handoffs/pickup-foundation-1.json",
                      "handoff_kind": "orchestration_verification_pickup",
                      "generated_at_utc": "2026-04-18T19:46:00Z",
                      "relative_path": ".bo/handoffs/pickup-foundation-1.json",
                      "producer": "bo",
                      "target_agent_uid": "worker-verify",
                      "status": "ready",
                      "actionability_score": 1.0
                    },
                    {
                      "artifact_id": "handoff:orchestration_verification_pickup:foundation-2",
                      "resource_uri": "bo://handoffs/orchestration_verification_pickup/.bo/handoffs/pickup-foundation-2.json",
                      "handoff_kind": "orchestration_verification_pickup",
                      "generated_at_utc": "2026-04-18T19:47:00Z",
                      "relative_path": ".bo/handoffs/pickup-foundation-2.json",
                      "producer": "bo",
                      "target_agent_uid": "worker-verify",
                      "status": "ready",
                      "actionability_score": 1.0
                    },
                    {
                      "artifact_id": "handoff:orchestration_verification_pickup:backend-1",
                      "resource_uri": "bo://handoffs/orchestration_verification_pickup/.bo/handoffs/pickup-backend-1.json",
                      "handoff_kind": "orchestration_verification_pickup",
                      "generated_at_utc": "2026-04-18T19:48:00Z",
                      "relative_path": ".bo/handoffs/pickup-backend-1.json",
                      "producer": "bo",
                      "target_agent_uid": "worker-verify",
                      "status": "ready",
                      "actionability_score": 1.0
                    }
                  ]
                }
                """);

            var queryJson =
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"handoff_query","arguments":{"bestReadyVerificationPickupWorkForTargetAgentUid":"worker-verify"}}}""";

            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(Frame(queryJson)));
            await using var output = new MemoryStream();

            var host = new McpServerHost(workspaceRoot);
            await host.RunAsync(input, output, CancellationToken.None);

            output.Position = 0;
            var responses = await ReadAllResponsesAsync(output);

            Assert.Single(responses);

            var structured = responses[0].RootElement.GetProperty("result").GetProperty("structuredContent");
            Assert.Equal("batch", structured.GetProperty("workSelectionKind").GetString());
            var bestGroup = structured.GetProperty("bestGroup");
            Assert.Equal("gate:foundation", bestGroup.GetProperty("groupKey").GetString());
            Assert.Equal("mostly relatedness", bestGroup.GetProperty("dominantPickupPressure").GetString());
            Assert.Equal(2, bestGroup.GetProperty("memberCount").GetInt32());
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
                Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task McpServerHost_HandoffQuerySupportsGroupedReadyVerificationHandoffs()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"bogdb-mcp-grouped-verification-{Guid.NewGuid():N}");
        try
        {
            var handoffDirectory = Path.Combine(workspaceRoot, ".bo", "handoffs");
            Directory.CreateDirectory(handoffDirectory);

            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "verification-foundation-1.json"), """
                {
                  "handoff": {
                    "acceptance_id": "accept:gate:foundation",
                    "target_id": "gate:foundation:integration",
                    "target_kind": "gate"
                  }
                }
                """);
            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "verification-foundation-2.json"), """
                {
                  "handoff": {
                    "acceptance_id": "accept:gate:foundation:review",
                    "target_id": "gate:foundation:review",
                    "target_kind": "gate"
                  }
                }
                """);
            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "verification-backend-1.json"), """
                {
                  "handoff": {
                    "acceptance_id": "accept:lane:backend",
                    "target_id": "lane:backend",
                    "target_kind": "lane"
                  }
                }
                """);

            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "index.json"), """
                {
                  "protocol_version": "bo.agent-handoff-index/0.1",
                  "generated_at_utc": "2026-04-18T18:00:00Z",
                  "resources": [
                    {
                      "artifact_id": "handoff:orchestration_acceptance_verification:foundation-1",
                      "resource_uri": "bo://handoffs/orchestration_acceptance_verification/.bo/handoffs/verification-foundation-1.json",
                      "handoff_kind": "orchestration_acceptance_verification",
                      "generated_at_utc": "2026-04-18T18:05:00Z",
                      "relative_path": ".bo/handoffs/verification-foundation-1.json",
                      "producer": "bo",
                      "target_agent_uid": "worker-verify",
                      "status": "ready",
                      "actionability_score": 1.0
                    },
                    {
                      "artifact_id": "handoff:orchestration_acceptance_verification:foundation-2",
                      "resource_uri": "bo://handoffs/orchestration_acceptance_verification/.bo/handoffs/verification-foundation-2.json",
                      "handoff_kind": "orchestration_acceptance_verification",
                      "generated_at_utc": "2026-04-18T18:06:00Z",
                      "relative_path": ".bo/handoffs/verification-foundation-2.json",
                      "producer": "bo",
                      "target_agent_uid": "worker-verify",
                      "status": "ready",
                      "actionability_score": 1.0
                    },
                    {
                      "artifact_id": "handoff:orchestration_acceptance_verification:backend-1",
                      "resource_uri": "bo://handoffs/orchestration_acceptance_verification/.bo/handoffs/verification-backend-1.json",
                      "handoff_kind": "orchestration_acceptance_verification",
                      "generated_at_utc": "2026-04-18T18:04:00Z",
                      "relative_path": ".bo/handoffs/verification-backend-1.json",
                      "producer": "bo",
                      "target_agent_uid": "worker-verify",
                      "status": "ready",
                      "actionability_score": 1.0
                    }
                  ]
                }
                """);

            var queryJson =
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"handoff_query","arguments":{"groupReadyVerificationHandoffsForTargetAgentUid":"worker-verify","limit":5}}}""";

            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(Frame(queryJson)));
            await using var output = new MemoryStream();

            var host = new McpServerHost(workspaceRoot);
            await host.RunAsync(input, output, CancellationToken.None);

            output.Position = 0;
            var responses = await ReadAllResponsesAsync(output);

            Assert.Single(responses);

            var structured = responses[0].RootElement.GetProperty("result").GetProperty("structuredContent");
            var groups = structured.GetProperty("groups").EnumerateArray().ToArray();
            Assert.Equal(2, groups.Length);
            Assert.Equal("gate:foundation", groups[0].GetProperty("groupKey").GetString());
            Assert.Equal(2, groups[0].GetProperty("memberCount").GetInt32());
            Assert.Equal("ingest_acceptance_verification_batch", groups[0].GetProperty("recommendedBatchAction").GetString());
            Assert.Equal("lane:backend", groups[1].GetProperty("groupKey").GetString());
            Assert.Equal(1, groups[1].GetProperty("memberCount").GetInt32());
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
                Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task McpServerHost_HandoffQuerySupportsBestReadyVerificationBatch()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"bogdb-mcp-best-verification-batch-{Guid.NewGuid():N}");
        try
        {
            var handoffDirectory = Path.Combine(workspaceRoot, ".bo", "handoffs");
            Directory.CreateDirectory(handoffDirectory);

            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "verification-foundation-1.json"), """
                {
                  "handoff": {
                    "acceptance_id": "accept:gate:foundation",
                    "target_id": "gate:foundation:integration",
                    "target_kind": "gate"
                  }
                }
                """);
            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "verification-foundation-2.json"), """
                {
                  "handoff": {
                    "acceptance_id": "accept:gate:foundation:review",
                    "target_id": "gate:foundation:review",
                    "target_kind": "gate"
                  }
                }
                """);
            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "verification-backend-1.json"), """
                {
                  "handoff": {
                    "acceptance_id": "accept:lane:backend",
                    "target_id": "lane:backend",
                    "target_kind": "lane"
                  }
                }
                """);

            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "index.json"), """
                {
                  "protocol_version": "bo.agent-handoff-index/0.1",
                  "generated_at_utc": "2026-04-18T18:00:00Z",
                  "resources": [
                    {
                      "artifact_id": "handoff:orchestration_acceptance_verification:foundation-1",
                      "resource_uri": "bo://handoffs/orchestration_acceptance_verification/.bo/handoffs/verification-foundation-1.json",
                      "handoff_kind": "orchestration_acceptance_verification",
                      "generated_at_utc": "2026-04-18T18:05:00Z",
                      "relative_path": ".bo/handoffs/verification-foundation-1.json",
                      "producer": "bo",
                      "target_agent_uid": "worker-verify",
                      "status": "ready",
                      "actionability_score": 1.0
                    },
                    {
                      "artifact_id": "handoff:orchestration_acceptance_verification:foundation-2",
                      "resource_uri": "bo://handoffs/orchestration_acceptance_verification/.bo/handoffs/verification-foundation-2.json",
                      "handoff_kind": "orchestration_acceptance_verification",
                      "generated_at_utc": "2026-04-18T18:06:00Z",
                      "relative_path": ".bo/handoffs/verification-foundation-2.json",
                      "producer": "bo",
                      "target_agent_uid": "worker-verify",
                      "status": "ready",
                      "actionability_score": 1.0
                    },
                    {
                      "artifact_id": "handoff:orchestration_acceptance_verification:backend-1",
                      "resource_uri": "bo://handoffs/orchestration_acceptance_verification/.bo/handoffs/verification-backend-1.json",
                      "handoff_kind": "orchestration_acceptance_verification",
                      "generated_at_utc": "2026-04-18T18:04:00Z",
                      "relative_path": ".bo/handoffs/verification-backend-1.json",
                      "producer": "bo",
                      "target_agent_uid": "worker-verify",
                      "status": "ready",
                      "actionability_score": 1.0
                    }
                  ]
                }
                """);

            var queryJson =
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"handoff_query","arguments":{"bestReadyVerificationBatchForTargetAgentUid":"worker-verify"}}}""";

            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(Frame(queryJson)));
            await using var output = new MemoryStream();

            var host = new McpServerHost(workspaceRoot);
            await host.RunAsync(input, output, CancellationToken.None);

            output.Position = 0;
            var responses = await ReadAllResponsesAsync(output);

            Assert.Single(responses);

            var structured = responses[0].RootElement.GetProperty("result").GetProperty("structuredContent");
            Assert.Equal(3, structured.GetProperty("totalMatches").GetInt32());
            Assert.Equal(1, structured.GetProperty("returnedGroupCount").GetInt32());
            var bestGroup = structured.GetProperty("bestGroup");
            Assert.Equal("gate:foundation", bestGroup.GetProperty("groupKey").GetString());
            Assert.Equal(2, bestGroup.GetProperty("memberCount").GetInt32());
            Assert.Equal("ingest_acceptance_verification_batch", bestGroup.GetProperty("recommendedBatchAction").GetString());
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
                Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task McpServerHost_HandoffQuerySupportsBestReadyVerificationWorkBatchSelection()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"bogdb-mcp-best-verification-work-batch-{Guid.NewGuid():N}");
        try
        {
            var handoffDirectory = Path.Combine(workspaceRoot, ".bo", "handoffs");
            Directory.CreateDirectory(handoffDirectory);

            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "verification-foundation-1.json"), """
                {
                  "handoff": {
                    "acceptance_id": "accept:gate:foundation",
                    "target_id": "gate:foundation:integration",
                    "target_kind": "gate"
                  }
                }
                """);
            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "verification-foundation-2.json"), """
                {
                  "handoff": {
                    "acceptance_id": "accept:gate:foundation:review",
                    "target_id": "gate:foundation:review",
                    "target_kind": "gate"
                  }
                }
                """);
            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "verification-backend-1.json"), """
                {
                  "handoff": {
                    "acceptance_id": "accept:lane:backend",
                    "target_id": "lane:backend",
                    "target_kind": "lane"
                  }
                }
                """);

            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "index.json"), """
                {
                  "protocol_version": "bo.agent-handoff-index/0.1",
                  "generated_at_utc": "2026-04-18T18:00:00Z",
                  "resources": [
                    {
                      "artifact_id": "handoff:orchestration_acceptance_verification:foundation-1",
                      "resource_uri": "bo://handoffs/orchestration_acceptance_verification/.bo/handoffs/verification-foundation-1.json",
                      "handoff_kind": "orchestration_acceptance_verification",
                      "generated_at_utc": "2026-04-18T18:05:00Z",
                      "relative_path": ".bo/handoffs/verification-foundation-1.json",
                      "producer": "bo",
                      "target_agent_uid": "worker-verify",
                      "status": "ready",
                      "actionability_score": 1.0
                    },
                    {
                      "artifact_id": "handoff:orchestration_acceptance_verification:foundation-2",
                      "resource_uri": "bo://handoffs/orchestration_acceptance_verification/.bo/handoffs/verification-foundation-2.json",
                      "handoff_kind": "orchestration_acceptance_verification",
                      "generated_at_utc": "2026-04-18T18:06:00Z",
                      "relative_path": ".bo/handoffs/verification-foundation-2.json",
                      "producer": "bo",
                      "target_agent_uid": "worker-verify",
                      "status": "ready",
                      "actionability_score": 1.0
                    },
                    {
                      "artifact_id": "handoff:orchestration_acceptance_verification:backend-1",
                      "resource_uri": "bo://handoffs/orchestration_acceptance_verification/.bo/handoffs/verification-backend-1.json",
                      "handoff_kind": "orchestration_acceptance_verification",
                      "generated_at_utc": "2026-04-18T18:04:00Z",
                      "relative_path": ".bo/handoffs/verification-backend-1.json",
                      "producer": "bo",
                      "target_agent_uid": "worker-verify",
                      "status": "ready",
                      "actionability_score": 1.0
                    }
                  ]
                }
                """);

            var queryJson =
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"handoff_query","arguments":{"bestReadyVerificationWorkForTargetAgentUid":"worker-verify"}}}""";

            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(Frame(queryJson)));
            await using var output = new MemoryStream();

            var host = new McpServerHost(workspaceRoot);
            await host.RunAsync(input, output, CancellationToken.None);

            output.Position = 0;
            var responses = await ReadAllResponsesAsync(output);

            Assert.Single(responses);

            var structured = responses[0].RootElement.GetProperty("result").GetProperty("structuredContent");
            Assert.Equal("batch", structured.GetProperty("workSelectionKind").GetString());
            var bestGroup = structured.GetProperty("bestGroup");
            Assert.Equal("gate:foundation", bestGroup.GetProperty("groupKey").GetString());
            Assert.Equal(2, bestGroup.GetProperty("memberCount").GetInt32());
            Assert.True(bestGroup.GetProperty("ageUrgencyScore").GetDouble() > 0d);
            Assert.Equal(0d, bestGroup.GetProperty("averageBlockerCount").GetDouble());
            Assert.Equal("mostly low blocker pressure", bestGroup.GetProperty("dominantPickupPressure").GetString());
            var factorSummary = bestGroup.GetProperty("pickupFactorSummary");
            Assert.Equal(0.9d, factorSummary.GetProperty("relatednessScore").GetDouble());
            Assert.True(factorSummary.GetProperty("lowCostScore").GetDouble() > 0.7d);
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
                Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task McpServerHost_HandoffQuerySupportsBestReadyVerificationWorkSingleSelection()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"bogdb-mcp-best-verification-work-single-{Guid.NewGuid():N}");
        try
        {
            var handoffDirectory = Path.Combine(workspaceRoot, ".bo", "handoffs");
            Directory.CreateDirectory(handoffDirectory);

            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "verification-foundation-older.json"), """
                {
                  "handoff": {
                    "acceptance_id": "accept:gate:foundation:integration",
                    "target_id": "gate:foundation:integration",
                    "target_kind": "gate"
                  }
                }
                """);
            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "verification-backend-1.json"), """
                {
                  "handoff": {
                    "acceptance_id": "accept:gate:review",
                    "target_id": "gate:review",
                    "target_kind": "gate"
                  }
                }
                """);

            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "index.json"), """
                {
                  "protocol_version": "bo.agent-handoff-index/0.1",
                  "generated_at_utc": "2026-04-18T18:00:00Z",
                  "resources": [
                    {
                      "artifact_id": "handoff:orchestration_acceptance_verification:foundation-older",
                      "resource_uri": "bo://handoffs/orchestration_acceptance_verification/.bo/handoffs/verification-foundation-older.json",
                      "handoff_kind": "orchestration_acceptance_verification",
                      "generated_at_utc": "2026-04-18T16:00:00Z",
                      "relative_path": ".bo/handoffs/verification-foundation-older.json",
                      "producer": "bo",
                      "target_agent_uid": "worker-verify",
                      "status": "ready",
                      "actionability_score": 1.0
                    },
                    {
                      "artifact_id": "handoff:orchestration_acceptance_verification:review-newer",
                      "resource_uri": "bo://handoffs/orchestration_acceptance_verification/.bo/handoffs/verification-backend-1.json",
                      "handoff_kind": "orchestration_acceptance_verification",
                      "generated_at_utc": "2026-04-18T18:04:00Z",
                      "relative_path": ".bo/handoffs/verification-backend-1.json",
                      "producer": "bo",
                      "target_agent_uid": "worker-verify",
                      "status": "ready",
                      "actionability_score": 1.0
                    }
                  ]
                }
                """);

            var queryJson =
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"handoff_query","arguments":{"bestReadyVerificationWorkForTargetAgentUid":"worker-verify"}}}""";

            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(Frame(queryJson)));
            await using var output = new MemoryStream();

            var host = new McpServerHost(workspaceRoot);
            await host.RunAsync(input, output, CancellationToken.None);

            output.Position = 0;
            var responses = await ReadAllResponsesAsync(output);

            Assert.Single(responses);

            var structured = responses[0].RootElement.GetProperty("result").GetProperty("structuredContent");
            Assert.Equal("single", structured.GetProperty("workSelectionKind").GetString());
            Assert.Equal("mostly low blocker pressure", structured.GetProperty("dominantPickupPressure").GetString());
            Assert.Equal(1d, structured.GetProperty("pickupFactorSummary").GetProperty("lowCostScore").GetDouble());
            var bestEntry = structured.GetProperty("bestEntry");
            Assert.Equal("handoff:orchestration_acceptance_verification:foundation-older", bestEntry.GetProperty("artifactId").GetString());
            Assert.Equal("worker-verify", bestEntry.GetProperty("targetAgentUid").GetString());
            Assert.Equal(0, bestEntry.GetProperty("blockerCount").GetInt32());
            Assert.Equal("mostly low blocker pressure", bestEntry.GetProperty("dominantPickupPressure").GetString());
            Assert.Equal(1d, bestEntry.GetProperty("pickupFactorSummary").GetProperty("lowCostScore").GetDouble());
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
                Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task McpServerHost_HandoffQueryBestReadyVerificationWorkPrefersLowerBlockerPressure()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"bogdb-mcp-best-verification-work-blockers-{Guid.NewGuid():N}");
        try
        {
            var handoffDirectory = Path.Combine(workspaceRoot, ".bo", "handoffs");
            Directory.CreateDirectory(handoffDirectory);

            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "verification-foundation-1.json"), """
                {
                  "handoff": {
                    "acceptance_id": "accept:gate:foundation:integration",
                    "target_id": "gate:foundation:integration",
                    "target_kind": "gate"
                  }
                }
                """);
            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "verification-foundation-2.json"), """
                {
                  "handoff": {
                    "acceptance_id": "accept:gate:foundation:review",
                    "target_id": "gate:foundation:review",
                    "target_kind": "gate"
                  }
                }
                """);
            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "verification-backend-1.json"), """
                {
                  "handoff": {
                    "acceptance_id": "accept:gate:backend:integration",
                    "target_id": "gate:backend:integration",
                    "target_kind": "gate"
                  }
                }
                """);
            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "verification-backend-2.json"), """
                {
                  "handoff": {
                    "acceptance_id": "accept:gate:backend:review",
                    "target_id": "gate:backend:review",
                    "target_kind": "gate"
                  }
                }
                """);

            await File.WriteAllTextAsync(Path.Combine(handoffDirectory, "index.json"), """
                {
                  "protocol_version": "bo.agent-handoff-index/0.1",
                  "generated_at_utc": "2026-04-18T18:00:00Z",
                  "resources": [
                    {
                      "artifact_id": "handoff:orchestration_acceptance_verification:foundation-1",
                      "resource_uri": "bo://handoffs/orchestration_acceptance_verification/.bo/handoffs/verification-foundation-1.json",
                      "handoff_kind": "orchestration_acceptance_verification",
                      "generated_at_utc": "2026-04-18T18:05:00Z",
                      "relative_path": ".bo/handoffs/verification-foundation-1.json",
                      "producer": "bo",
                      "target_agent_uid": "worker-verify",
                      "status": "ready",
                      "actionability_score": 1.0
                    },
                    {
                      "artifact_id": "handoff:orchestration_acceptance_verification:foundation-2",
                      "resource_uri": "bo://handoffs/orchestration_acceptance_verification/.bo/handoffs/verification-foundation-2.json",
                      "handoff_kind": "orchestration_acceptance_verification",
                      "generated_at_utc": "2026-04-18T18:04:00Z",
                      "relative_path": ".bo/handoffs/verification-foundation-2.json",
                      "producer": "bo",
                      "target_agent_uid": "worker-verify",
                      "status": "ready",
                      "actionability_score": 1.0
                    },
                    {
                      "artifact_id": "handoff:orchestration_acceptance_verification:backend-1",
                      "resource_uri": "bo://handoffs/orchestration_acceptance_verification/.bo/handoffs/verification-backend-1.json",
                      "handoff_kind": "orchestration_acceptance_verification",
                      "generated_at_utc": "2026-04-18T18:06:00Z",
                      "relative_path": ".bo/handoffs/verification-backend-1.json",
                      "producer": "bo",
                      "target_agent_uid": "worker-verify",
                      "status": "ready",
                      "blocker_codes": ["awaiting_contract","awaiting_review"],
                      "actionability_score": 1.0
                    },
                    {
                      "artifact_id": "handoff:orchestration_acceptance_verification:backend-2",
                      "resource_uri": "bo://handoffs/orchestration_acceptance_verification/.bo/handoffs/verification-backend-2.json",
                      "handoff_kind": "orchestration_acceptance_verification",
                      "generated_at_utc": "2026-04-18T18:03:00Z",
                      "relative_path": ".bo/handoffs/verification-backend-2.json",
                      "producer": "bo",
                      "target_agent_uid": "worker-verify",
                      "status": "ready",
                      "blocker_codes": ["awaiting_contract","awaiting_review"],
                      "actionability_score": 1.0
                    }
                  ]
                }
                """);

            var queryJson =
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"handoff_query","arguments":{"bestReadyVerificationWorkForTargetAgentUid":"worker-verify"}}}""";

            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(Frame(queryJson)));
            await using var output = new MemoryStream();

            var host = new McpServerHost(workspaceRoot);
            await host.RunAsync(input, output, CancellationToken.None);

            output.Position = 0;
            var responses = await ReadAllResponsesAsync(output);

            Assert.Single(responses);

            var structured = responses[0].RootElement.GetProperty("result").GetProperty("structuredContent");
            Assert.Equal("batch", structured.GetProperty("workSelectionKind").GetString());
            var bestGroup = structured.GetProperty("bestGroup");
            Assert.Equal("gate:foundation", bestGroup.GetProperty("groupKey").GetString());
            Assert.Equal(0d, bestGroup.GetProperty("averageBlockerCount").GetDouble());
            Assert.True(bestGroup.GetProperty("blockerPenaltyScore").GetDouble() > 0.9d);
            Assert.Equal("mostly low blocker pressure", bestGroup.GetProperty("dominantPickupPressure").GetString());
            Assert.True(bestGroup.GetProperty("pickupFactorSummary").GetProperty("blockerPenaltyScore").GetDouble() > 0.9d);
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
                Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task McpServerHost_OrchestrationPendingGatesReturnsUnacceptedGate()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bogdb-mcp-orchestration-{Guid.NewGuid():N}");
        try
        {
            SeedOrchestrationGraph(dbPath);

            var queryJson =
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"orchestration_pending_gates\",\"arguments\":{\"databasePath\":\"" +
                EscapeJson(dbPath) +
                "\",\"flowId\":\"flow:visualmls:phase-delivery\"}}}";

            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(Frame(queryJson)));
            await using var output = new MemoryStream();

            var host = new McpServerHost();
            await host.RunAsync(input, output, CancellationToken.None);

            output.Position = 0;
            var responses = await ReadAllResponsesAsync(output);

            Assert.Single(responses);

            var structured = responses[0].RootElement.GetProperty("result").GetProperty("structuredContent");
            Assert.True(structured.GetProperty("success").GetBoolean());
            var rows = structured.GetProperty("rows").EnumerateArray().ToArray();
            Assert.Single(rows);
            Assert.Equal("gate:foundation:integration", rows[0].GetProperty("gate_id").GetString());
        }
        finally
        {
            if (Directory.Exists(dbPath))
                Directory.Delete(dbPath, recursive: true);
        }
    }

    [Fact]
    public async Task McpServerHost_OrchestrationFixtureIsQueryableViaBogDbQuery()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bogdb-mcp-orchestration-{Guid.NewGuid():N}");
        try
        {
            SeedOrchestrationGraph(dbPath);

            var queryJson =
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"bogdb_query\",\"arguments\":{\"databasePath\":\"" +
                EscapeJson(dbPath) +
                "\",\"cypher\":\"MATCH (f:OrchestrationFlow)-[:HAS_STAGE]->(s:OrchestrationStage) RETURN f.flow_id AS flow_id, s.stage_id AS stage_id ORDER BY stage_id\"}}}";

            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(Frame(queryJson)));
            await using var output = new MemoryStream();

            var host = new McpServerHost();
            await host.RunAsync(input, output, CancellationToken.None);

            output.Position = 0;
            var responses = await ReadAllResponsesAsync(output);

            Assert.Single(responses);

            var structured = responses[0].RootElement.GetProperty("result").GetProperty("structuredContent");
            Assert.True(structured.GetProperty("success").GetBoolean());
            var rows = structured.GetProperty("rows").EnumerateArray().ToArray();
            Assert.Equal(2, rows.Length);
        }
        finally
        {
            if (Directory.Exists(dbPath))
                Directory.Delete(dbPath, recursive: true);
        }
    }

    [Fact]
    public async Task McpServerHost_OrchestrationReleaseReadyGatesReturnsGateWithAcceptedUpstreamLanes()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bogdb-mcp-orchestration-{Guid.NewGuid():N}");
        try
        {
            SeedOrchestrationGraph(dbPath);

            var queryJson =
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"orchestration_release_ready_gates\",\"arguments\":{\"databasePath\":\"" +
                EscapeJson(dbPath) +
                "\",\"flowId\":\"flow:visualmls:phase-delivery\"}}}";

            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(Frame(queryJson)));
            await using var output = new MemoryStream();

            var host = new McpServerHost();
            await host.RunAsync(input, output, CancellationToken.None);

            output.Position = 0;
            var responses = await ReadAllResponsesAsync(output);

            Assert.Single(responses);

            var structured = responses[0].RootElement.GetProperty("result").GetProperty("structuredContent");
            Assert.True(structured.GetProperty("success").GetBoolean());
            var rows = structured.GetProperty("rows").EnumerateArray().ToArray();
            Assert.Single(rows);
            Assert.Equal("gate:foundation:integration", rows[0].GetProperty("gate_id").GetString());
            Assert.Equal("integration_agent", rows[0].GetProperty("owner_role").GetString());
        }
        finally
        {
            if (Directory.Exists(dbPath))
                Directory.Delete(dbPath, recursive: true);
        }
    }

    [Fact]
    public async Task McpServerHost_OrchestrationBlockedWorkReturnsWorkBlockedByGateState()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bogdb-mcp-orchestration-{Guid.NewGuid():N}");
        try
        {
            SeedOrchestrationGraph(dbPath);

            var queryJson =
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"orchestration_blocked_work\",\"arguments\":{\"databasePath\":\"" +
                EscapeJson(dbPath) +
                "\",\"flowId\":\"flow:visualmls:phase-delivery\",\"targetAgentUid\":\"worker-property\"}}}";

            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(Frame(queryJson)));
            await using var output = new MemoryStream();

            var host = new McpServerHost();
            await host.RunAsync(input, output, CancellationToken.None);

            output.Position = 0;
            var responses = await ReadAllResponsesAsync(output);

            Assert.Single(responses);

            var structured = responses[0].RootElement.GetProperty("result").GetProperty("structuredContent");
            Assert.True(structured.GetProperty("success").GetBoolean());
            var rows = structured.GetProperty("rows").EnumerateArray().ToArray();
            Assert.Single(rows);
            Assert.Equal("work:property:intelligence", rows[0].GetProperty("work_item_id").GetString());
            Assert.Equal("upstream_stage_unreleased", rows[0].GetProperty("blocker_code").GetString());
        }
        finally
        {
            if (Directory.Exists(dbPath))
                Directory.Delete(dbPath, recursive: true);
        }
    }

    [Fact]
    public async Task McpServerHost_OrchestrationRecordAcceptancePersistsAndClearsPendingGate()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bogdb-mcp-orchestration-{Guid.NewGuid():N}");
        try
        {
            SeedOrchestrationGraph(dbPath);

            var inputPayload = string.Concat(
                Frame("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"orchestration_record_acceptance\",\"arguments\":{\"databasePath\":\"" +
                      EscapeJson(dbPath) +
                      "\",\"acceptanceId\":\"accept:gate:foundation\",\"targetId\":\"gate:foundation:integration\",\"targetKind\":\"gate\",\"acceptanceStatus\":\"accepted\",\"recordedByAgentUid\":\"integration-agent\"}}}"),
                Frame("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"orchestration_pending_gates\",\"arguments\":{\"databasePath\":\"" +
                      EscapeJson(dbPath) +
                      "\",\"flowId\":\"flow:visualmls:phase-delivery\"}}}"));

            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(inputPayload));
            await using var output = new MemoryStream();

            var host = new McpServerHost();
            await host.RunAsync(input, output, CancellationToken.None);

            output.Position = 0;
            var responses = await ReadAllResponsesAsync(output);

            Assert.Equal(2, responses.Count);

            var writeStructured = responses[0].RootElement.GetProperty("result").GetProperty("structuredContent");
            Assert.True(writeStructured.GetProperty("success").GetBoolean());
            Assert.Equal("gate", writeStructured.GetProperty("targetKind").GetString());

            var queryStructured = responses[1].RootElement.GetProperty("result").GetProperty("structuredContent");
            Assert.True(queryStructured.GetProperty("success").GetBoolean());
            Assert.Empty(queryStructured.GetProperty("rows").EnumerateArray());
        }
        finally
        {
            if (Directory.Exists(dbPath))
                Directory.Delete(dbPath, recursive: true);
        }
    }

    [Fact]
    public async Task McpServerHost_OrchestrationLaneAcceptanceGapsReturnsCompletedUnacceptedLane()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bogdb-mcp-orchestration-{Guid.NewGuid():N}");
        try
        {
            SeedLaneAcceptanceGapGraph(dbPath);

            var queryJson =
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"orchestration_lane_acceptance_gaps\",\"arguments\":{\"databasePath\":\"" +
                EscapeJson(dbPath) +
                "\",\"flowId\":\"flow:visualmls:phase-delivery\"}}}";

            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(Frame(queryJson)));
            await using var output = new MemoryStream();

            var host = new McpServerHost();
            await host.RunAsync(input, output, CancellationToken.None);

            output.Position = 0;
            var responses = await ReadAllResponsesAsync(output);

            Assert.Single(responses);

            var structured = responses[0].RootElement.GetProperty("result").GetProperty("structuredContent");
            Assert.True(structured.GetProperty("success").GetBoolean());
            var rows = structured.GetProperty("rows").EnumerateArray().ToArray();
            Assert.Single(rows);
            Assert.Equal("lane:foundation:backend", rows[0].GetProperty("lane_id").GetString());
            Assert.Contains(
                rows[0].GetProperty("work_item_ids").EnumerateArray().Select(item => item.GetString()),
                item => item == "work:foundation:backend");
        }
        finally
        {
            if (Directory.Exists(dbPath))
                Directory.Delete(dbPath, recursive: true);
        }
    }

    [Fact]
    public async Task McpServerHost_OrchestrationIngestAcceptanceIndexClearsPendingGate()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bogdb-mcp-orchestration-{Guid.NewGuid():N}");
        var publishRoot = Path.Combine(Path.GetTempPath(), $"bogdb-mcp-acceptance-{Guid.NewGuid():N}");
        try
        {
            SeedOrchestrationGraph(dbPath);
            var workspaceSegment = "workspace_demo";
            var acceptanceDirectory = Path.Combine(publishRoot, workspaceSegment, "acceptance", "gate");
            Directory.CreateDirectory(acceptanceDirectory);

            var artifactRelativePath = $"{workspaceSegment}/acceptance/gate/accept_gate_foundation_integration.json";
            var artifactPath = Path.Combine(publishRoot, artifactRelativePath.Replace('/', Path.DirectorySeparatorChar));
            await File.WriteAllTextAsync(artifactPath, """
                {
                  "protocol_version": "acop.acceptance-artifact/0.1",
                  "artifact_id": "acceptance:workspace_demo:gate:accept_gate_foundation_integration",
                  "resource_uri": "bo://acceptance/workspace:demo/gate/accept_gate_foundation_integration.json",
                  "generated_at_utc": "2026-04-18T16:22:11Z",
                  "source_command": "bo orchestration accept gate gate:foundation:integration",
                  "workspace_root": "/tmp/demo",
                  "relative_path": "workspace_demo/acceptance/gate/accept_gate_foundation_integration.json",
                  "acceptance": {
                    "acceptance_id": "accept:gate:foundation",
                    "workspace_id": "workspace:demo",
                    "target_id": "gate:foundation:integration",
                    "target_kind": "gate",
                    "acceptance_status": "accepted",
                    "recorded_at_utc": "2026-04-18T16:22:11Z",
                    "recorded_by_agent_uid": "lead-codex",
                    "source_work_item_id": "work:foundation:integration",
                    "notes": "Gate accepted through BO ACOP publication."
                  }
                }
                """);

            var indexPath = Path.Combine(publishRoot, workspaceSegment, "acceptance", "index.json");
            Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
            await File.WriteAllTextAsync(indexPath, """
                {
                  "protocol_version": "acop.acceptance-index/0.1",
                  "generated_at_utc": "2026-04-18T16:22:11Z",
                  "resources": [
                    {
                      "artifact_id": "acceptance:workspace_demo:gate:accept_gate_foundation_integration",
                      "resource_uri": "bo://acceptance/workspace:demo/gate/accept_gate_foundation_integration.json",
                      "generated_at_utc": "2026-04-18T16:22:11Z",
                      "relative_path": "workspace_demo/acceptance/gate/accept_gate_foundation_integration.json",
                      "workspace_id": "workspace:demo",
                      "target_id": "gate:foundation:integration",
                      "target_kind": "gate",
                      "acceptance_status": "accepted",
                      "recorded_at_utc": "2026-04-18T16:22:11Z",
                      "recorded_by_agent_uid": "lead-codex",
                      "source_work_item_id": "work:foundation:integration"
                    }
                  ]
                }
                """);

            var inputPayload = string.Concat(
                Frame("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"orchestration_ingest_acceptance_artifacts\",\"arguments\":{\"databasePath\":\"" +
                      EscapeJson(dbPath) +
                      "\",\"indexPath\":\"" +
                      EscapeJson(indexPath) +
                      "\"}}}"),
                Frame("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"orchestration_pending_gates\",\"arguments\":{\"databasePath\":\"" +
                      EscapeJson(dbPath) +
                      "\",\"flowId\":\"flow:visualmls:phase-delivery\"}}}"));

            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(inputPayload));
            await using var output = new MemoryStream();

            var host = new McpServerHost();
            await host.RunAsync(input, output, CancellationToken.None);

            output.Position = 0;
            var responses = await ReadAllResponsesAsync(output);

            Assert.Equal(2, responses.Count);

            var ingestStructured = responses[0].RootElement.GetProperty("result").GetProperty("structuredContent");
            Assert.True(ingestStructured.GetProperty("success").GetBoolean());
            Assert.Equal("index", ingestStructured.GetProperty("source").GetString());
            Assert.Equal(1, ingestStructured.GetProperty("ingestedCount").GetInt32());

            var queryStructured = responses[1].RootElement.GetProperty("result").GetProperty("structuredContent");
            Assert.True(queryStructured.GetProperty("success").GetBoolean());
            Assert.Empty(queryStructured.GetProperty("rows").EnumerateArray());
        }
        finally
        {
            if (Directory.Exists(dbPath))
                Directory.Delete(dbPath, recursive: true);
            if (Directory.Exists(publishRoot))
                Directory.Delete(publishRoot, recursive: true);
        }
    }

    [Fact]
    public async Task McpServerHost_OrchestrationAcceptanceIngestStatusReturnsLifecycleRecord()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bogdb-mcp-orchestration-{Guid.NewGuid():N}");
        var publishRoot = Path.Combine(Path.GetTempPath(), $"bogdb-mcp-acceptance-{Guid.NewGuid():N}");
        try
        {
            SeedOrchestrationGraph(dbPath);
            var workspaceSegment = "workspace_demo";
            var acceptanceDirectory = Path.Combine(publishRoot, workspaceSegment, "acceptance", "gate");
            Directory.CreateDirectory(acceptanceDirectory);

            var artifactPath = Path.Combine(acceptanceDirectory, "accept_gate_foundation_integration.json");
            await File.WriteAllTextAsync(artifactPath, """
                {
                  "protocol_version": "acop.acceptance-artifact/0.1",
                  "artifact_id": "acceptance:workspace_demo:gate:accept_gate_foundation_integration",
                  "resource_uri": "bo://acceptance/workspace:demo/gate/accept_gate_foundation_integration.json",
                  "generated_at_utc": "2026-04-18T16:22:11Z",
                  "source_command": "bo orchestration accept gate gate:foundation:integration",
                  "workspace_root": "/tmp/demo",
                  "relative_path": "workspace_demo/acceptance/gate/accept_gate_foundation_integration.json",
                  "acceptance": {
                    "acceptance_id": "accept:gate:foundation",
                    "workspace_id": "workspace:demo",
                    "target_id": "gate:foundation:integration",
                    "target_kind": "gate",
                    "acceptance_status": "accepted",
                    "recorded_at_utc": "2026-04-18T16:22:11Z",
                    "recorded_by_agent_uid": "lead-codex",
                    "source_work_item_id": "work:foundation:integration",
                    "notes": "Gate accepted through BO ACOP publication."
                  }
                }
                """);

            var indexPath = Path.Combine(publishRoot, workspaceSegment, "acceptance", "index.json");
            Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
            await File.WriteAllTextAsync(indexPath, """
                {
                  "protocol_version": "acop.acceptance-index/0.1",
                  "generated_at_utc": "2026-04-18T16:22:11Z",
                  "resources": [
                    {
                      "artifact_id": "acceptance:workspace_demo:gate:accept_gate_foundation_integration",
                      "resource_uri": "bo://acceptance/workspace:demo/gate/accept_gate_foundation_integration.json",
                      "generated_at_utc": "2026-04-18T16:22:11Z",
                      "relative_path": "workspace_demo/acceptance/gate/accept_gate_foundation_integration.json",
                      "workspace_id": "workspace:demo",
                      "target_id": "gate:foundation:integration",
                      "target_kind": "gate",
                      "acceptance_status": "accepted",
                      "recorded_at_utc": "2026-04-18T16:22:11Z"
                    }
                  ]
                }
                """);

            var inputPayload = string.Concat(
                Frame("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"orchestration_ingest_acceptance_artifacts\",\"arguments\":{\"databasePath\":\"" +
                      EscapeJson(dbPath) +
                      "\",\"indexPath\":\"" +
                      EscapeJson(indexPath) +
                      "\"}}}"),
                Frame("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"orchestration_acceptance_ingest_status\",\"arguments\":{\"databasePath\":\"" +
                      EscapeJson(dbPath) +
                      "\",\"acceptanceId\":\"accept:gate:foundation\"}}}"));

            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(inputPayload));
            await using var output = new MemoryStream();

            var host = new McpServerHost();
            await host.RunAsync(input, output, CancellationToken.None);

            output.Position = 0;
            var responses = await ReadAllResponsesAsync(output);

            Assert.Equal(2, responses.Count);

            var statusStructured = responses[1].RootElement.GetProperty("result").GetProperty("structuredContent");
            Assert.True(statusStructured.GetProperty("success").GetBoolean());
            var rows = statusStructured.GetProperty("rows").EnumerateArray().ToArray();
            Assert.Single(rows);
            Assert.Equal("accept:gate:foundation", rows[0].GetProperty("acceptance_id").GetString());
            Assert.Equal("gate:foundation:integration", rows[0].GetProperty("target_id").GetString());
            Assert.Equal("index", rows[0].GetProperty("source").GetString());
        }
        finally
        {
            if (Directory.Exists(dbPath))
                Directory.Delete(dbPath, recursive: true);
            if (Directory.Exists(publishRoot))
                Directory.Delete(publishRoot, recursive: true);
        }
    }

    [Fact]
    public async Task McpServerHost_OrchestrationAcceptanceIngestsAwaitingLocalVerification_ReturnsOnlyUnverifiedIngests()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bogdb-orchestration-{Guid.NewGuid():N}");
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"bogdb-workspace-{Guid.NewGuid():N}");
        var publishRoot = Path.Combine(workspaceRoot, "publish");

        Directory.CreateDirectory(workspaceRoot);
        Directory.CreateDirectory(publishRoot);

        try
        {
            SeedOrchestrationGraph(dbPath);
            var workspaceSegment = "workspace_demo";
            var gateAcceptanceDirectory = Path.Combine(publishRoot, workspaceSegment, "acceptance", "gate");
            var laneAcceptanceDirectory = Path.Combine(publishRoot, workspaceSegment, "acceptance", "lane");
            Directory.CreateDirectory(gateAcceptanceDirectory);
            Directory.CreateDirectory(laneAcceptanceDirectory);

            var gateArtifactPath = Path.Combine(gateAcceptanceDirectory, "accept_gate_foundation_integration.json");
            await File.WriteAllTextAsync(gateArtifactPath, """
                {
                  "protocol_version": "acop.acceptance-artifact/0.1",
                  "artifact_id": "acceptance:workspace_demo:gate:accept_gate_foundation_integration",
                  "resource_uri": "bo://acceptance/workspace:demo/gate/accept_gate_foundation_integration.json",
                  "generated_at_utc": "2026-04-18T16:22:11Z",
                  "source_command": "bo orchestration accept gate gate:foundation:integration",
                  "workspace_root": "/tmp/demo",
                  "relative_path": "workspace_demo/acceptance/gate/accept_gate_foundation_integration.json",
                  "acceptance": {
                    "acceptance_id": "accept:gate:foundation",
                    "workspace_id": "workspace:demo",
                    "target_id": "gate:foundation:integration",
                    "target_kind": "gate",
                    "acceptance_status": "accepted",
                    "recorded_at_utc": "2026-04-18T16:22:11Z"
                  }
                }
                """);

            var laneArtifactPath = Path.Combine(laneAcceptanceDirectory, "accept_lane_backend.json");
            await File.WriteAllTextAsync(laneArtifactPath, """
                {
                  "protocol_version": "acop.acceptance-artifact/0.1",
                  "artifact_id": "acceptance:workspace_demo:lane:accept_lane_backend",
                  "resource_uri": "bo://acceptance/workspace:demo/lane/accept_lane_backend.json",
                  "generated_at_utc": "2026-04-18T16:24:11Z",
                  "source_command": "bo orchestration accept lane lane:backend",
                  "workspace_root": "/tmp/demo",
                  "relative_path": "workspace_demo/acceptance/lane/accept_lane_backend.json",
                  "acceptance": {
                    "acceptance_id": "accept:lane:backend",
                    "workspace_id": "workspace:demo",
                    "target_id": "lane:backend",
                    "target_kind": "lane",
                    "acceptance_status": "accepted",
                    "recorded_at_utc": "2026-04-18T16:24:11Z"
                  }
                }
                """);

            var acceptanceIndexPath = Path.Combine(publishRoot, workspaceSegment, "acceptance", "index.json");
            Directory.CreateDirectory(Path.GetDirectoryName(acceptanceIndexPath)!);
            await File.WriteAllTextAsync(acceptanceIndexPath, """
                {
                  "protocol_version": "acop.acceptance-index/0.1",
                  "generated_at_utc": "2026-04-18T16:24:11Z",
                  "resources": [
                    {
                      "artifact_id": "acceptance:workspace_demo:gate:accept_gate_foundation_integration",
                      "resource_uri": "bo://acceptance/workspace:demo/gate/accept_gate_foundation_integration.json",
                      "generated_at_utc": "2026-04-18T16:22:11Z",
                      "relative_path": "workspace_demo/acceptance/gate/accept_gate_foundation_integration.json",
                      "workspace_id": "workspace:demo",
                      "target_id": "gate:foundation:integration",
                      "target_kind": "gate",
                      "acceptance_status": "accepted",
                      "recorded_at_utc": "2026-04-18T16:22:11Z"
                    },
                    {
                      "artifact_id": "acceptance:workspace_demo:lane:accept_lane_backend",
                      "resource_uri": "bo://acceptance/workspace:demo/lane/accept_lane_backend.json",
                      "generated_at_utc": "2026-04-18T16:24:11Z",
                      "relative_path": "workspace_demo/acceptance/lane/accept_lane_backend.json",
                      "workspace_id": "workspace:demo",
                      "target_id": "lane:backend",
                      "target_kind": "lane",
                      "acceptance_status": "accepted",
                      "recorded_at_utc": "2026-04-18T16:24:11Z"
                    }
                  ]
                }
                """);

            var verificationIndexPath = Path.Combine(workspaceRoot, ".bo", "orchestration", "acceptance-verifications", "index.json");
            Directory.CreateDirectory(Path.GetDirectoryName(verificationIndexPath)!);
            await File.WriteAllTextAsync(verificationIndexPath, """
                {
                  "protocol_version": "acop.acceptance-ingest-verification-index/0.1",
                  "generated_at_utc": "2026-04-18T16:40:00Z",
                  "resources": [
                    {
                      "artifact_id": "acceptance_ingest_verification:accept_gate_foundation",
                      "generated_at_utc": "2026-04-18T16:40:00Z",
                      "relative_path": ".bo/orchestration/acceptance-verifications/gate/accept_gate_foundation.json",
                      "acceptance_id": "accept:gate:foundation",
                      "target_id": "gate:foundation:integration",
                      "target_kind": "gate",
                      "ingest_status": "ingested",
                      "observed_at_utc": "2026-04-18T16:40:00Z"
                    }
                  ]
                }
                """);

            var verificationArtifactPath = Path.Combine(workspaceRoot, ".bo", "orchestration", "acceptance-verifications", "gate", "accept_gate_foundation.json");
            Directory.CreateDirectory(Path.GetDirectoryName(verificationArtifactPath)!);
            await File.WriteAllTextAsync(verificationArtifactPath, """
                {
                  "protocol_version": "acop.acceptance-ingest-verification/0.1",
                  "artifact_id": "acceptance_ingest_verification:accept_gate_foundation",
                  "generated_at_utc": "2026-04-18T16:40:00Z",
                  "source_command": "bo orchestration verify-acceptance-ingest accept:gate:foundation gate gate:foundation:integration",
                  "workspace_root": "/tmp/demo",
                  "relative_path": ".bo/orchestration/acceptance-verifications/gate/accept_gate_foundation.json",
                  "verification": {
                    "acceptance_id": "accept:gate:foundation",
                    "target_id": "gate:foundation:integration",
                    "target_kind": "gate",
                    "ingest_status": "ingested",
                    "observed_at_utc": "2026-04-18T16:40:00Z",
                    "ingested_at_utc": "2026-04-18T16:30:00Z",
                    "source": "query"
                  }
                }
                """);

            var inputPayload = string.Concat(
                Frame("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"orchestration_ingest_acceptance_artifacts\",\"arguments\":{\"databasePath\":\"" +
                      EscapeJson(dbPath) +
                      "\",\"indexPath\":\"" +
                      EscapeJson(acceptanceIndexPath) +
                      "\"}}}"),
                Frame("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"orchestration_ingest_acceptance_verification_artifacts\",\"arguments\":{\"databasePath\":\"" +
                      EscapeJson(dbPath) +
                      "\",\"indexPath\":\"" +
                      EscapeJson(verificationIndexPath) +
                      "\"}}}"),
                Frame("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"orchestration_acceptance_verification_status\",\"arguments\":{\"databasePath\":\"" +
                      EscapeJson(dbPath) +
                      "\",\"acceptanceId\":\"accept:gate:foundation\"}}}"),
                Frame("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"orchestration_acceptance_ingests_awaiting_local_verification\",\"arguments\":{\"databasePath\":\"" +
                      EscapeJson(dbPath) +
                      "\"}}}"));

            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(inputPayload));
            await using var output = new MemoryStream();

            var host = new McpServerHost(workspaceRoot);
            await host.RunAsync(input, output, CancellationToken.None);

            output.Position = 0;
            var responses = await ReadAllResponsesAsync(output);

            Assert.Equal(4, responses.Count);

            var verificationStructured = responses[2].RootElement.GetProperty("result").GetProperty("structuredContent");
            Assert.True(verificationStructured.GetProperty("success").GetBoolean());
            var verificationRows = verificationStructured.GetProperty("rows").EnumerateArray().ToArray();
            Assert.Single(verificationRows);
            Assert.Equal("accept:gate:foundation", verificationRows[0].GetProperty("acceptance_id").GetString());
            Assert.Equal("ingested", verificationRows[0].GetProperty("ingest_status").GetString());

            var awaitingStructured = responses[3].RootElement.GetProperty("result").GetProperty("structuredContent");
            Assert.True(awaitingStructured.GetProperty("success").GetBoolean());
            var rows = awaitingStructured.GetProperty("rows").EnumerateArray().ToArray();
            Assert.Single(rows);
            Assert.Equal("accept:lane:backend", rows[0].GetProperty("acceptance_id").GetString());
            Assert.Equal("lane:backend", rows[0].GetProperty("target_id").GetString());
        }
        finally
        {
            if (Directory.Exists(dbPath))
                Directory.Delete(dbPath, recursive: true);
            if (Directory.Exists(workspaceRoot))
                Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    private static string Frame(string json)
    {
        var bytes = Encoding.UTF8.GetByteCount(json);
        return $"Content-Length: {bytes}\r\n\r\n{json}";
    }

    private static string EscapeJson(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static async Task<List<JsonDocument>> ReadAllResponsesAsync(Stream stream)
    {
        var documents = new List<JsonDocument>();
        while (true)
        {
            var payload = await McpServerHost.ReadMessageAsync(stream, CancellationToken.None);
            if (payload == null)
                break;
            documents.Add(JsonDocument.Parse(payload));
        }
        return documents;
    }

    private static void SeedOrchestrationGraph(string dbPath)
    {
        using var db = BogDatabase.Open(dbPath);
        using var conn = new BogConnection(db);

        conn.BeginWriteTransaction();

        conn.EnsureNodeTable("OrchestrationFlow", new Dictionary<string, LogicalTypeID>
        {
            ["id"] = LogicalTypeID.STRING,
            ["flow_id"] = LogicalTypeID.STRING,
            ["title"] = LogicalTypeID.STRING,
            ["target_repo_id"] = LogicalTypeID.STRING
        });
        conn.EnsureNodeTable("OrchestrationStage", new Dictionary<string, LogicalTypeID>
        {
            ["id"] = LogicalTypeID.STRING,
            ["stage_id"] = LogicalTypeID.STRING,
            ["title"] = LogicalTypeID.STRING
        });
        conn.EnsureNodeTable("OrchestrationLane", new Dictionary<string, LogicalTypeID>
        {
            ["id"] = LogicalTypeID.STRING,
            ["lane_id"] = LogicalTypeID.STRING,
            ["title"] = LogicalTypeID.STRING
        });
        conn.EnsureNodeTable("OrchestrationGate", new Dictionary<string, LogicalTypeID>
        {
            ["id"] = LogicalTypeID.STRING,
            ["gate_id"] = LogicalTypeID.STRING,
            ["title"] = LogicalTypeID.STRING,
            ["gate_kind"] = LogicalTypeID.STRING,
            ["owner_role"] = LogicalTypeID.STRING
        });
        conn.EnsureNodeTable("AcceptanceRecord", new Dictionary<string, LogicalTypeID>
        {
            ["id"] = LogicalTypeID.STRING,
            ["acceptance_id"] = LogicalTypeID.STRING,
            ["target_id"] = LogicalTypeID.STRING,
            ["target_kind"] = LogicalTypeID.STRING,
            ["acceptance_status"] = LogicalTypeID.STRING,
            ["recorded_at_utc"] = LogicalTypeID.STRING,
            ["recorded_by_agent_uid"] = LogicalTypeID.STRING,
            ["source_work_item_id"] = LogicalTypeID.STRING,
            ["notes"] = LogicalTypeID.STRING
        });
        conn.EnsureNodeTable("WorkItem", new Dictionary<string, LogicalTypeID>
        {
            ["id"] = LogicalTypeID.STRING,
            ["work_item_id"] = LogicalTypeID.STRING,
            ["title"] = LogicalTypeID.STRING,
            ["summary"] = LogicalTypeID.STRING,
            ["status"] = LogicalTypeID.STRING,
            ["blocker_code"] = LogicalTypeID.STRING,
            ["target_agent_uid"] = LogicalTypeID.STRING
        });

        conn.EnsureRelTable("HAS_STAGE", "OrchestrationFlow", "OrchestrationStage", new());
        conn.EnsureRelTable("HAS_LANE", "OrchestrationStage", "OrchestrationLane", new());
        conn.EnsureRelTable("HAS_GATE", "OrchestrationStage", "OrchestrationGate", new());
        conn.EnsureRelTable("FLOWS_TO", "OrchestrationLane", "OrchestrationGate", new());
        conn.EnsureRelTable("RELEASES", "OrchestrationGate", "OrchestrationStage", new());
        conn.EnsureRelTable("ACCEPTS", "AcceptanceRecord", "OrchestrationLane", new());
        conn.EnsureRelTable("IMPLEMENTS_FLOW_UNIT", "WorkItem", "OrchestrationLane", new());

        conn.UpsertNodeById("OrchestrationFlow", "flow1", new Dictionary<string, object>
        {
            ["flow_id"] = "flow:visualmls:phase-delivery",
            ["title"] = "VisualMLS phased delivery",
            ["target_repo_id"] = "repo:visualmls"
        });
        conn.UpsertNodeById("OrchestrationStage", "stage1", new Dictionary<string, object>
        {
            ["stage_id"] = "stage:foundation",
            ["title"] = "Foundation"
        });
        conn.UpsertNodeById("OrchestrationStage", "stage2", new Dictionary<string, object>
        {
            ["stage_id"] = "stage:property",
            ["title"] = "Property Intelligence"
        });
        conn.UpsertNodeById("OrchestrationLane", "lane1", new Dictionary<string, object>
        {
            ["lane_id"] = "lane:foundation:backend",
            ["title"] = "Backend skeleton"
        });
        conn.UpsertNodeById("OrchestrationLane", "lane2", new Dictionary<string, object>
        {
            ["lane_id"] = "lane:foundation:ui",
            ["title"] = "UI skeleton"
        });
        conn.UpsertNodeById("OrchestrationGate", "gate1", new Dictionary<string, object>
        {
            ["gate_id"] = "gate:foundation:integration",
            ["title"] = "Foundation integration gate",
            ["gate_kind"] = "integration_gate",
            ["owner_role"] = "integration_agent"
        });
        conn.UpsertNodeById("AcceptanceRecord", "accept1", new Dictionary<string, object>
        {
            ["acceptance_id"] = "accept:backend",
            ["target_id"] = "lane:foundation:backend",
            ["target_kind"] = "lane",
            ["acceptance_status"] = "accepted",
            ["recorded_at_utc"] = "2026-04-18T00:00:00Z",
            ["recorded_by_agent_uid"] = "integration-agent"
        });
        conn.UpsertNodeById("AcceptanceRecord", "accept2", new Dictionary<string, object>
        {
            ["acceptance_id"] = "accept:ui",
            ["target_id"] = "lane:foundation:ui",
            ["target_kind"] = "lane",
            ["acceptance_status"] = "accepted",
            ["recorded_at_utc"] = "2026-04-18T00:00:00Z",
            ["recorded_by_agent_uid"] = "integration-agent"
        });
        conn.UpsertNodeById("WorkItem", "work1", new Dictionary<string, object>
        {
            ["work_item_id"] = "work:property:intelligence",
            ["title"] = "Property intelligence implementation",
            ["summary"] = "Blocked until foundation integration gate releases downstream stage.",
            ["status"] = "blocked",
            ["blocker_code"] = "upstream_stage_unreleased",
            ["target_agent_uid"] = "worker-property"
        });

        conn.UpsertRelationshipById("HAS_STAGE", "flow1", "stage1", new Dictionary<string, object>());
        conn.UpsertRelationshipById("HAS_STAGE", "flow1", "stage2", new Dictionary<string, object>());
        conn.UpsertRelationshipById("HAS_LANE", "stage1", "lane1", new Dictionary<string, object>());
        conn.UpsertRelationshipById("HAS_LANE", "stage1", "lane2", new Dictionary<string, object>());
        conn.UpsertRelationshipById("HAS_GATE", "stage1", "gate1", new Dictionary<string, object>());
        conn.UpsertRelationshipById("FLOWS_TO", "lane1", "gate1", new Dictionary<string, object>());
        conn.UpsertRelationshipById("FLOWS_TO", "lane2", "gate1", new Dictionary<string, object>());
        conn.UpsertRelationshipById("RELEASES", "gate1", "stage2", new Dictionary<string, object>());
        conn.UpsertRelationshipById("ACCEPTS", "accept1", "lane1", new Dictionary<string, object>());
        conn.UpsertRelationshipById("ACCEPTS", "accept2", "lane2", new Dictionary<string, object>());
        conn.UpsertRelationshipById("IMPLEMENTS_FLOW_UNIT", "work1", "lane1", new Dictionary<string, object>());

        conn.Commit();
    }

    private static void SeedLaneAcceptanceGapGraph(string dbPath)
    {
        using var db = BogDatabase.Open(dbPath);
        using var conn = new BogConnection(db);

        conn.BeginWriteTransaction();

        conn.EnsureNodeTable("OrchestrationFlow", new Dictionary<string, LogicalTypeID>
        {
            ["id"] = LogicalTypeID.STRING,
            ["flow_id"] = LogicalTypeID.STRING,
            ["title"] = LogicalTypeID.STRING,
            ["target_repo_id"] = LogicalTypeID.STRING
        });
        conn.EnsureNodeTable("OrchestrationStage", new Dictionary<string, LogicalTypeID>
        {
            ["id"] = LogicalTypeID.STRING,
            ["stage_id"] = LogicalTypeID.STRING,
            ["title"] = LogicalTypeID.STRING
        });
        conn.EnsureNodeTable("OrchestrationLane", new Dictionary<string, LogicalTypeID>
        {
            ["id"] = LogicalTypeID.STRING,
            ["lane_id"] = LogicalTypeID.STRING,
            ["title"] = LogicalTypeID.STRING
        });
        conn.EnsureNodeTable("AcceptanceRecord", new Dictionary<string, LogicalTypeID>
        {
            ["id"] = LogicalTypeID.STRING,
            ["acceptance_id"] = LogicalTypeID.STRING,
            ["target_id"] = LogicalTypeID.STRING,
            ["target_kind"] = LogicalTypeID.STRING,
            ["acceptance_status"] = LogicalTypeID.STRING,
            ["recorded_at_utc"] = LogicalTypeID.STRING,
            ["recorded_by_agent_uid"] = LogicalTypeID.STRING,
            ["source_work_item_id"] = LogicalTypeID.STRING,
            ["notes"] = LogicalTypeID.STRING
        });
        conn.EnsureNodeTable("WorkItem", new Dictionary<string, LogicalTypeID>
        {
            ["id"] = LogicalTypeID.STRING,
            ["work_item_id"] = LogicalTypeID.STRING,
            ["title"] = LogicalTypeID.STRING,
            ["summary"] = LogicalTypeID.STRING,
            ["status"] = LogicalTypeID.STRING,
            ["blocker_code"] = LogicalTypeID.STRING,
            ["target_agent_uid"] = LogicalTypeID.STRING
        });

        conn.EnsureRelTable("HAS_STAGE", "OrchestrationFlow", "OrchestrationStage", new());
        conn.EnsureRelTable("HAS_LANE", "OrchestrationStage", "OrchestrationLane", new());
        conn.EnsureRelTable("ACCEPTS", "AcceptanceRecord", "OrchestrationLane", new());
        conn.EnsureRelTable("IMPLEMENTS_FLOW_UNIT", "WorkItem", "OrchestrationLane", new());

        conn.UpsertNodeById("OrchestrationFlow", "flow1", new Dictionary<string, object>
        {
            ["flow_id"] = "flow:visualmls:phase-delivery",
            ["title"] = "VisualMLS phased delivery",
            ["target_repo_id"] = "repo:visualmls"
        });
        conn.UpsertNodeById("OrchestrationStage", "stage1", new Dictionary<string, object>
        {
            ["stage_id"] = "stage:foundation",
            ["title"] = "Foundation"
        });
        conn.UpsertNodeById("OrchestrationLane", "lane1", new Dictionary<string, object>
        {
            ["lane_id"] = "lane:foundation:backend",
            ["title"] = "Backend skeleton"
        });
        conn.UpsertNodeById("WorkItem", "work1", new Dictionary<string, object>
        {
            ["work_item_id"] = "work:foundation:backend",
            ["title"] = "Foundation backend implementation",
            ["summary"] = "Ready for review and acceptance.",
            ["status"] = "completed",
            ["blocker_code"] = string.Empty,
            ["target_agent_uid"] = "worker-backend"
        });

        conn.UpsertRelationshipById("HAS_STAGE", "flow1", "stage1", new Dictionary<string, object>());
        conn.UpsertRelationshipById("HAS_LANE", "stage1", "lane1", new Dictionary<string, object>());
        conn.UpsertRelationshipById("IMPLEMENTS_FLOW_UNIT", "work1", "lane1", new Dictionary<string, object>());

        conn.Commit();
    }

    private static void ExecuteOrThrow(BogConnection conn, string cypher)
    {
        var result = conn.Query(cypher);
        Assert.True(result.IsSuccess, $"Cypher failed: {result.ErrorMessage}\n{cypher}");
    }
}
