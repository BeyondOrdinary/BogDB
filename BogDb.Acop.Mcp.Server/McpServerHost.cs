using System.Buffers;
using System.Text;
using System.Text.Json;
using BogDb.Acop.Mcp.Server.Adapters;

namespace BogDb.Acop.Mcp.Server;

/// <summary>
/// stdio JSON-RPC host for the ACOP write-side MCP server. Tool calls are
/// dispatched to an <see cref="IAcopBackend"/> implementation; the host
/// itself contains no coordination state and no policy.
/// </summary>
public sealed class McpServerHost
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IAcopBackend _backend;

    public McpServerHost(IAcopBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
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
            var message = await ReadMessageAsync(input, cancellationToken);
            if (message is null)
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
                // Notifications never receive responses.
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

        JsonElement toolResult = name switch
        {
            "acop_claim_work_item" => await _backend.ClaimWorkItemAsync(arguments, cancellationToken),
            "acop_update_claim" => await _backend.UpdateClaimAsync(arguments, cancellationToken),
            "acop_create_work_item" => await _backend.CreateWorkItemAsync(arguments, cancellationToken),
            "acop_accept_handoff" => await _backend.AcceptHandoffAsync(arguments, cancellationToken),
            "acop_post_blackboard" => await _backend.PostBlackboardAsync(arguments, cancellationToken),
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

    private static object CreateInitializeResult() => new
    {
        protocolVersion = "2025-03-26",
        capabilities = new
        {
            tools = new { }
        },
        serverInfo = new
        {
            name = "acop-mcp",
            version = "0.1.0"
        }
    };

    private static object CreateToolsListResult() => new
    {
        tools = new object[]
        {
            new
            {
                name = "acop_claim_work_item",
                description = "Submit an ACOP claim intent for a work item. Returns the granted claim on success or surfaces the backend's error body on conflict.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        work_item_id = new { type = "string" },
                        worker_agent_uid = new { type = "string" },
                        coordination_scope_id = new { type = "string" },
                        requested_ttl_seconds = new { type = "integer", minimum = 1 },
                        agent_capabilities = new { type = "array", items = new { type = "string" } },
                        owned_paths = new { type = "array", items = new { type = "string" } },
                        owned_symbols = new { type = "array", items = new { type = "string" } },
                        scope = new { type = "object" }
                    },
                    required = new[] { "work_item_id", "worker_agent_uid", "coordination_scope_id" }
                }
            },
            new
            {
                name = "acop_update_claim",
                description = "Renew, release, or complete an active ACOP claim. The 'action' field selects the lifecycle transition.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        claim_id = new { type = "string" },
                        action = new
                        {
                            type = "string",
                            @enum = new[] { "renew", "release", "complete" }
                        },
                        progress_summary = new { type = "string" },
                        completion_evidence = new { type = "object" }
                    },
                    required = new[] { "claim_id", "action" }
                }
            },
            new
            {
                name = "acop_create_work_item",
                description = "Upsert an ACOP work item. Lead and supervisor agents call this to seed coordination units; the 'work_item_id' MUST be stable so repeated calls are idempotent at the backend.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        work_item_id = new { type = "string" },
                        work_kind = new
                        {
                            type = "string",
                            description = "implement / refactor / review / validate / repair / handoff_followup / merge_prepare / conflict_resolution"
                        },
                        title = new { type = "string" },
                        summary = new { type = "string" },
                        coordination_scope_id = new { type = "string" },
                        priority = new
                        {
                            type = "string",
                            description = "low / medium / high / urgent"
                        },
                        actionability_score = new { type = "number", minimum = 0, maximum = 1 },
                        target_repo_id = new { type = "string" },
                        target_branch = new { type = "string" },
                        target_agent_uid = new { type = "string" },
                        stage_id = new { type = "string" },
                        correlation_id = new { type = "string" },
                        source_handoff_id = new { type = "string" },
                        required_capabilities = new { type = "array", items = new { type = "string" } },
                        owned_paths = new { type = "array", items = new { type = "string" } },
                        owned_symbols = new { type = "array", items = new { type = "string" } }
                    },
                    required = new[] { "work_item_id", "work_kind", "title", "coordination_scope_id" }
                }
            },
            new
            {
                name = "acop_accept_handoff",
                description = "Mark a work item handoff as accepted. Causes downstream readiness checks to evaluate against the configured release policies.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        work_item_id = new { type = "string" },
                        acceptance_kind = new
                        {
                            type = "string",
                            description = "reviewed / accepted / released"
                        },
                        implementation_claim_id = new { type = "string" },
                        notes = new { type = "string" },
                        acceptance_notes = new { type = "string" },
                        allow_self_acceptance = new { type = "boolean" }
                    },
                    required = new[] { "work_item_id" }
                }
            },
            new
            {
                name = "acop_post_blackboard",
                description = "Append a blackboard entry against a work item. Entries are immutable once posted.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        work_item_id = new { type = "string" },
                        coordination_scope_id = new { type = "string" },
                        entry_kind = new
                        {
                            type = "string",
                            description = "finding / hypothesis / partial_result / risk / decision / constraint / review_request / integration_note"
                        },
                        author_agent_uid = new { type = "string" },
                        summary = new { type = "string" },
                        details = new { type = "string" },
                        artifact_ids = new { type = "array", items = new { type = "string" } },
                        operation_ids = new { type = "array", items = new { type = "string" } },
                        supersedes_entry_ids = new { type = "array", items = new { type = "string" } }
                    },
                    required = new[] { "work_item_id", "coordination_scope_id", "entry_kind", "summary" }
                }
            }
        }
    };

    private enum McpMessageFraming
    {
        Header,
        Line
    }

    private sealed record McpMessage(string Payload, McpMessageFraming Framing);

    private static async Task<McpMessage?> ReadMessageAsync(Stream input, CancellationToken cancellationToken)
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
