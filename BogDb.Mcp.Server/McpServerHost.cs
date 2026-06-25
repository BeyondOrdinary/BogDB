using System.Buffers;
using System.Text;
using System.Text.Json;
using BogDb.Mcp.Server.Services;

namespace BogDb.Mcp.Server;

public sealed class McpServerHost
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly BogDbQueryToolService _queryService = new();
    private readonly BogDbSchemaToolService _schemaService = new();
    private readonly HandoffResourceService _handoffResourceService;
    private readonly CodeIntelligenceQueryToolService _codeQueryService;
    private readonly OrchestrationQueryToolService _orchestrationQueryService;
    private readonly OrchestrationAcceptanceToolService _orchestrationAcceptanceService;
    private readonly OrchestrationAcceptanceIngestToolService _orchestrationAcceptanceIngestService;
    private readonly OrchestrationAcceptanceVerificationIngestToolService _orchestrationAcceptanceVerificationIngestService;

    public McpServerHost(string? workspaceRoot = null)
    {
        _handoffResourceService = new HandoffResourceService(workspaceRoot);
        _codeQueryService = new CodeIntelligenceQueryToolService(_queryService, workspaceRoot);
        _orchestrationQueryService = new OrchestrationQueryToolService(_queryService, workspaceRoot);
        _orchestrationAcceptanceService = new OrchestrationAcceptanceToolService();
        _orchestrationAcceptanceIngestService = new OrchestrationAcceptanceIngestToolService();
        _orchestrationAcceptanceVerificationIngestService = new OrchestrationAcceptanceVerificationIngestToolService();
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        await RunAsync(input, output, cancellationToken);
    }

    public async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await ReadMessageEnvelopeAsync(input, cancellationToken);
            if (message == null)
                break;

            using var document = JsonDocument.Parse(message.Payload);
            var root = document.RootElement;
            if (!root.TryGetProperty("method", out var methodElement))
                continue;

            var method = methodElement.GetString();
            if (string.IsNullOrWhiteSpace(method))
                continue;

            if (!root.TryGetProperty("id", out var idElement))
            {
                if (string.Equals(method, "notifications/initialized", StringComparison.Ordinal))
                    continue;
                continue;
            }

            var id = idElement.Clone();

            try
            {
                object result = method switch
                {
                    "initialize" => CreateInitializeResult(),
                    "ping" => new { },
                    "tools/list" => CreateToolsListResult(),
                    "tools/call" => await HandleToolsCallAsync(root, cancellationToken),
                    "resources/list" => _handoffResourceService.ListResources(),
                    "resources/read" => HandleResourcesRead(root),
                    _ => throw new InvalidOperationException($"Unsupported MCP method '{method}'.")
                };

                await WriteResponseAsync(output, new
                {
                    jsonrpc = "2.0",
                    id,
                    result
                }, message.Framing, cancellationToken);
            }
            catch (Exception ex)
            {
                await WriteResponseAsync(output, new
                {
                    jsonrpc = "2.0",
                    id,
                    error = new
                    {
                        code = -32000,
                        message = ex.Message
                    }
                }, message.Framing, cancellationToken);
            }
        }
    }

    private async Task<object> HandleToolsCallAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("params", out var @params))
            throw new InvalidOperationException("tools/call requires params.");

        var name = @params.GetProperty("name").GetString();
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("tools/call requires a tool name.");

        var arguments = @params.TryGetProperty("arguments", out var argsElement)
            ? argsElement
            : default;

        //
        // Nomenclature:
        // ox : orchestration
        // ax : acceptance
        // vx : verification
        // ix : ingest
        // deps : dependencies
        // ats : artifacts
        // gts : gates
        // sts : status
        //
        object toolResult = name switch
        {
            "bogdb_query" => await _queryService.ExecuteAsync(arguments, cancellationToken),
            "bogdb_schema" => _schemaService.GetSchema(arguments),
            "bogdb_tables" => _schemaService.GetTables(arguments),
            "bogdb_table_info" => _schemaService.GetTableInfo(arguments),
            "code_symbol_search" => await _codeQueryService.SearchSymbolsAsync(arguments, cancellationToken),
            "code_deps" or "code_dependencies" => await _codeQueryService.QueryDependenciesAsync(arguments, cancellationToken),
            "code_refactor_hotspots" => await _codeQueryService.QueryRefactorHotspotsAsync(arguments, cancellationToken),
            "handoff_query" => _handoffResourceService.QueryHandoffs(arguments),
            "ox_pending_gts" or "orchestration_pending_gates" => await _orchestrationQueryService.QueryPendingGatesAsync(arguments, cancellationToken),
            "ox_release_ready_gts" or "orchestration_release_ready_gates" => await _orchestrationQueryService.QueryReleaseReadyGatesAsync(arguments, cancellationToken),
            "ox_blocked_work" or "orchestration_blocked_work" => await _orchestrationQueryService.QueryBlockedWorkAsync(arguments, cancellationToken),
            "ox_lane_ax_gaps" or "orchestration_lane_acceptance_gaps" => await _orchestrationQueryService.QueryLaneAcceptanceGapsAsync(arguments, cancellationToken),
            "ox_ax_ix_sts" or "orchestration_acceptance_ingest_status" => await _orchestrationQueryService.QueryAcceptanceIngestStatusAsync(arguments, cancellationToken),
            "ox_ax_vx_sts" or "orchestration_acceptance_verification_status" => await _orchestrationQueryService.QueryAcceptanceVerificationStatusAsync(arguments, cancellationToken),
            "ox_ax_ix_awaiting_local_vx" or "orchestration_acceptance_ingests_awaiting_local_verification" => await _orchestrationQueryService.QueryAcceptanceIngestsAwaitingLocalVerificationAsync(arguments, cancellationToken),
            "ox_record_ax" or "orchestration_record_acceptance" => await _orchestrationAcceptanceService.RecordAcceptanceAsync(arguments, cancellationToken),
            "ox_ix_ax_ats" or "orchestration_ingest_acceptance_artifacts" => await _orchestrationAcceptanceIngestService.IngestAsync(arguments, cancellationToken),
            "ox_ix_ax_vx_ats" or "orchestration_ingest_acceptance_verification_artifacts" => await _orchestrationAcceptanceVerificationIngestService.IngestAsync(arguments, cancellationToken),
            _ => throw new InvalidOperationException($"Unknown tool '{name}'.")
        };

        return new
        {
            content = new[]
            {
                new
                {
                    type = "text",
                    text = JsonSerializer.Serialize(toolResult, JsonOptions)
                }
            },
            structuredContent = toolResult,
            isError = false
        };
    }

    private object HandleResourcesRead(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var @params))
            throw new InvalidOperationException("resources/read requires params.");

        return _handoffResourceService.ReadResource(@params);
    }

    private static object CreateInitializeResult()
    {
        return new
        {
            protocolVersion = "2025-03-26",
            capabilities = new
            {
                tools = new { },
                resources = new
                {
                    subscribe = false,
                    listChanged = false
                }
            },
            serverInfo = new
            {
                name = "bogdb-ng",
                version = "0.1.0"
            }
        };
    }

    private static object CreateToolsListResult()
    {
        return new
        {
            tools = new object[]
            {
                new
                {
                    name = "bogdb_query",
                    description = "Execute a read-only Cypher query against a BogDB database.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            databasePath = new { type = "string" },
                            cypher = new { type = "string" },
                            parameters = new { type = "object" },
                            rowLimit = new { type = "integer", minimum = 1, maximum = 1000 },
                            timeoutMs = new { type = "integer", minimum = 1 }
                        },
                        required = new[] { "databasePath", "cypher" }
                    }
                },
                new
                {
                    name = "bogdb_schema",
                    description = "Return a compact schema summary for a BogDB database.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            databasePath = new { type = "string" }
                        },
                        required = Array.Empty<string>()
                    }
                },
                new
                {
                    name = "bogdb_tables",
                    description = "List node and relationship tables in a BogDB database.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            databasePath = new { type = "string" }
                        },
                        required = Array.Empty<string>()
                    }
                },
                new
                {
                    name = "bogdb_table_info",
                    description = "Return detailed metadata for one table in a BogDB database.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            databasePath = new { type = "string" },
                            tableName = new { type = "string" }
                        },
                        required = new[] { "databasePath", "tableName" }
                    }
                },
                new
                {
                    name = "code_symbol_search",
                    description = "Find code symbols (classes, methods, functions, interfaces, records) in a BO code graph by case-insensitive name substring, with their kind, signature, and declaring file.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            databasePath = new { type = "string", description = "Optional; defaults to the agent worktree's .bo/graph. Path to a BO code graph (BogDB database)." },
                            query = new { type = "string", description = "Substring matched (case-insensitive) against symbol qualified and display names." },
                            kind = new { type = "string", description = "Optional exact kind filter (e.g. class, method, function, interface, record)." },
                            rowLimit = new { type = "integer", minimum = 1, maximum = 1000 },
                            timeoutMs = new { type = "integer", minimum = 1 }
                        },
                        required = new[] { "query" }
                    }
                },
                new
                {
                    name = "code_deps",
                    description = "Return symbol-to-symbol dependency edges (calls / uses-type / instantiates) for a symbol and/or the symbols declared in a file, in the requested direction.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            databasePath = new { type = "string", description = "Optional; defaults to the agent worktree's .bo/graph. Path to a BO code graph (BogDB database)." },
                            symbol = new { type = "string", description = "Symbol name substring to anchor on (case-insensitive)." },
                            file = new { type = "string", description = "File path substring; anchors on symbols declared in matching files." },
                            direction = new { type = "string", @enum = new[] { "in", "out", "both" }, description = "Edge direction to return (default both)." },
                            rowLimit = new { type = "integer", minimum = 1, maximum = 1000 },
                            timeoutMs = new { type = "integer", minimum = 1 }
                        },
                        required = Array.Empty<string>()
                    }
                },
                new
                {
                    name = "code_refactor_hotspots",
                    description = "Return files ranked by refactor pressure score (descending) from a BO code graph, with optional minimum-score and recommendation filters.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            databasePath = new { type = "string", description = "Optional; defaults to the agent worktree's .bo/graph. Path to a BO code graph (BogDB database)." },
                            minScore = new { type = "number", description = "Only return files whose refactor pressure score is >= this value." },
                            recommendation = new { type = "string", description = "Optional exact recommendation filter (e.g. split, extract, none)." },
                            rowLimit = new { type = "integer", minimum = 1, maximum = 1000 },
                            timeoutMs = new { type = "integer", minimum = 1 }
                        },
                        required = Array.Empty<string>()
                    }
                },
                new
                {
                    name = "handoff_query",
                    description = "Query workspace-scoped handoff index entries with optional kind and agent filters.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            handoffKind = new { type = "string" },
                            createdByAgentUid = new { type = "string" },
                            targetAgentUid = new { type = "string" },
                            participantAgentUid = new { type = "string" },
                            latestForTargetAgentUid = new { type = "string" },
                            latestBetweenAgentAUid = new { type = "string" },
                            latestBetweenAgentBUid = new { type = "string" },
                            latestReadyForTargetAgentUid = new { type = "string" },
                            latestReadyVerificationForTargetAgentUid = new { type = "string" },
                            latestReadyVerificationPickupForTargetAgentUid = new { type = "string" },
                            groupReadyVerificationHandoffsForTargetAgentUid = new { type = "string" },
                            groupReadyVerificationPickupHandoffsForTargetAgentUid = new { type = "string" },
                            bestReadyVerificationBatchForTargetAgentUid = new { type = "string" },
                            bestReadyVerificationWorkForTargetAgentUid = new { type = "string" },
                            bestReadyVerificationPickupWorkForTargetAgentUid = new { type = "string" },
                            latestActionableForTargetAgentUid = new { type = "string" },
                            status = new { type = "string" },
                            latestOnly = new { type = "boolean" },
                            limit = new { type = "integer", minimum = 1, maximum = 100 }
                        }
                    }
                },
                new
                {
                    name = "ox_pending_gts",
                    description = "Query ACOP orchestration gates that are still pending acceptance in a BogDB database.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            databasePath = new { type = "string" },
                            flowId = new { type = "string" },
                            repoId = new { type = "string" },
                            stageId = new { type = "string" },
                            rowLimit = new { type = "integer", minimum = 1, maximum = 1000 },
                            timeoutMs = new { type = "integer", minimum = 1 }
                        },
                        required = Array.Empty<string>()
                    }
                },
                new
                {
                    name = "ox_release_ready_gts",
                    description = "Query ACOP orchestration gates whose upstream lanes are accepted and ready for owner action.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            databasePath = new { type = "string" },
                            flowId = new { type = "string" },
                            repoId = new { type = "string" },
                            ownerRole = new { type = "string" },
                            rowLimit = new { type = "integer", minimum = 1, maximum = 1000 },
                            timeoutMs = new { type = "integer", minimum = 1 }
                        },
                        required = Array.Empty<string>()
                    }
                },
                new
                {
                    name = "ox_blocked_work",
                    description = "Query work items blocked specifically by orchestration state in a BogDB database.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            databasePath = new { type = "string" },
                            flowId = new { type = "string" },
                            repoId = new { type = "string" },
                            targetAgentUid = new { type = "string" },
                            rowLimit = new { type = "integer", minimum = 1, maximum = 1000 },
                            timeoutMs = new { type = "integer", minimum = 1 }
                        },
                        required = Array.Empty<string>()
                    }
                },
                new
                {
                    name = "ox_lane_ax_gaps",
                    description = "Query completed orchestration lanes that still need acceptance before downstream release.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            databasePath = new { type = "string" },
                            flowId = new { type = "string" },
                            repoId = new { type = "string" },
                            stageId = new { type = "string" },
                            rowLimit = new { type = "integer", minimum = 1, maximum = 1000 },
                            timeoutMs = new { type = "integer", minimum = 1 }
                        },
                        required = Array.Empty<string>()
                    }
                },
                new
                {
                    name = "ox_ax_ix_sts",
                    description = "Query durable acceptance-ingest lifecycle records in a BogDb orchestration database.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            databasePath = new { type = "string" },
                            acceptanceId = new { type = "string" },
                            targetId = new { type = "string" },
                            targetKind = new { type = "string", @enum = new[] { "lane", "gate", "stage" } },
                            rowLimit = new { type = "integer", minimum = 1, maximum = 1000 },
                            timeoutMs = new { type = "integer", minimum = 1 }
                        },
                        required = Array.Empty<string>()
                    }
                },
                new
                {
                    name = "ox_ax_vx_sts",
                    description = "Return durable acceptance verification lifecycle records from BogDB.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            databasePath = new { type = "string" },
                            acceptanceId = new { type = "string" },
                            targetId = new { type = "string" },
                            targetKind = new { type = "string" },
                            rowLimit = new { type = "integer", minimum = 1, maximum = 1000 },
                            timeoutMs = new { type = "integer", minimum = 1 }
                        },
                        required = Array.Empty<string>()
                    }
                },
                new
                {
                    name = "ox_ax_ix_awaiting_local_vx",
                    description = "Query acceptance ingests in BogDb that do not yet have a local BO verification receipt in the current workspace.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            databasePath = new { type = "string" },
                            acceptanceId = new { type = "string" },
                            targetId = new { type = "string" },
                            targetKind = new { type = "string", @enum = new[] { "lane", "gate", "stage" } },
                            rowLimit = new { type = "integer", minimum = 1, maximum = 1000 },
                            timeoutMs = new { type = "integer", minimum = 1 }
                        },
                        required = Array.Empty<string>()
                    }
                },
                new
                {
                    name = "ox_ix_ax_vx_ats",
                    description = "Ingest BO-style local acceptance verification artifacts or a verification index into durable BogDb verification records.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            databasePath = new { type = "string" },
                            artifactPath = new { type = "string" },
                            indexPath = new { type = "string" }
                        },
                        required = Array.Empty<string>()
                    }
                },
                new
                {
                    name = "ox_record_ax",
                    description = "Persist a durable orchestration acceptance record for a lane, gate, or stage.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            databasePath = new { type = "string" },
                            acceptanceId = new { type = "string" },
                            targetId = new { type = "string" },
                            targetKind = new { type = "string", @enum = new[] { "lane", "gate", "stage" } },
                            acceptanceStatus = new { type = "string", @enum = new[] { "reviewed", "accepted", "released", "rejected" } },
                            recordedAtUtc = new { type = "string" },
                            recordedByAgentUid = new { type = "string" },
                            sourceWorkItemId = new { type = "string" },
                            notes = new { type = "string" }
                        },
                        required = new[] { "databasePath", "acceptanceId", "targetId", "targetKind", "acceptanceStatus" }
                    }
                },
                new
                {
                    name = "ox_ix_ax_ats",
                    description = "Ingest BO-style acceptance artifacts or an acceptance index into durable BogDb orchestration state.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            databasePath = new { type = "string" },
                            artifactPath = new { type = "string" },
                            indexPath = new { type = "string" }
                        },
                        required = Array.Empty<string>()
                    }
                }
            }
        };
    }

    public static async Task<string?> ReadMessageAsync(Stream input, CancellationToken cancellationToken)
    {
        var message = await ReadMessageEnvelopeAsync(input, cancellationToken);
        return message?.Payload;
    }

    private enum McpMessageFraming
    {
        Header,
        Line
    }

    private sealed record McpMessage(string Payload, McpMessageFraming Framing);

    private static async Task<McpMessage?> ReadMessageEnvelopeAsync(Stream input, CancellationToken cancellationToken)
    {
        var headerBytes = new List<byte>();
        var firstByte = await ReadByteAsync(input, cancellationToken);
        if (firstByte is null)
            return null;

        headerBytes.Add(firstByte.Value);
        if (firstByte.Value == (byte)'{')
        {
            while (true)
            {
                var nextByte = await ReadByteAsync(input, cancellationToken);
                if (nextByte is null)
                    break;

                headerBytes.Add(nextByte.Value);
                if (nextByte.Value == (byte)'\n')
                    break;
            }

            var line = Encoding.UTF8.GetString(headerBytes.ToArray()).TrimEnd('\r', '\n');
            return new McpMessage(line, McpMessageFraming.Line);
        }

        while (true)
        {
            var nextByte = await ReadByteAsync(input, cancellationToken);
            if (nextByte is null)
                throw new EndOfStreamException("Unexpected EOF while reading MCP headers.");

            headerBytes.Add(nextByte.Value);
            if (HasHeaderTerminator(headerBytes))
                break;
        }

        var headers = Encoding.ASCII.GetString(headerBytes.ToArray());
        var contentLength = ParseContentLength(headers);
        var payload = new byte[contentLength];
        var offset = 0;
        while (offset < contentLength)
        {
            var read = await input.ReadAsync(payload.AsMemory(offset, contentLength - offset), cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("Unexpected EOF while reading MCP payload.");
            offset += read;
        }

        return new McpMessage(Encoding.UTF8.GetString(payload), McpMessageFraming.Header);
    }

    private static async Task<byte?> ReadByteAsync(Stream input, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1);
        try
        {
            var bytesRead = await input.ReadAsync(buffer.AsMemory(0, 1), cancellationToken);
            return bytesRead == 0 ? null : buffer[0];
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool HasHeaderTerminator(IReadOnlyList<byte> bytes)
    {
        if (bytes.Count >= 4
            && bytes[^4] == (byte)'\r'
            && bytes[^3] == (byte)'\n'
            && bytes[^2] == (byte)'\r'
            && bytes[^1] == (byte)'\n')
        {
            return true;
        }

        return bytes.Count >= 2
            && bytes[^2] == (byte)'\n'
            && bytes[^1] == (byte)'\n';
    }

    private static int ParseContentLength(string headers)
    {
        foreach (var line in headers.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = line["Content-Length:".Length..].Trim();
            if (int.TryParse(value, out var contentLength) && contentLength >= 0)
                return contentLength;
        }

        throw new InvalidOperationException("Missing Content-Length header.");
    }

    public static async Task WriteResponseAsync(Stream output, object payload, CancellationToken cancellationToken)
    {
        await WriteResponseAsync(output, payload, McpMessageFraming.Header, cancellationToken);
    }

    private static async Task WriteResponseAsync(Stream output, object payload, McpMessageFraming framing, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(payload, JsonOptions);
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        if (framing == McpMessageFraming.Line)
        {
            await output.WriteAsync(bodyBytes, cancellationToken);
            await output.WriteAsync(new[] { (byte)'\n' }, cancellationToken);
            await output.FlushAsync(cancellationToken);
            return;
        }

        var headerBytes = Encoding.ASCII.GetBytes($"Content-Length: {bodyBytes.Length}\r\n\r\n");

        await output.WriteAsync(headerBytes, cancellationToken);
        await output.WriteAsync(bodyBytes, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }
}
