using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BogDb.Acop.Mcp.Server.Adapters;

/// <summary>
/// Backend adapter that calls an HTTP-bound ACOP coordination middleware.
///
/// The MCP tool input contract uses ACOP snake_case field names; the
/// backend's HTTP DTOs use camelCase. This adapter performs the impedance
/// match per-tool, translating field names, normalizing enum casing, and
/// reshaping ACOP-style nested <c>scope</c> objects into the flat
/// <c>coordinationScopeId</c> / <c>repoId</c> / <c>ownedPaths</c> fields
/// most current implementations expect.
///
/// Endpoint paths follow the ACOP HTTP+JSON binding. Configure the base URI
/// via the <c>ACOP_BACKEND_BASE_URI</c> env var or <c>--base-uri</c> flag.
/// </summary>
public sealed class HttpAcopBackend : IAcopBackend, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public HttpAcopBackend(Uri baseUri, string? bearerToken = null, HttpClient? httpClient = null)
    {
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.BaseAddress = baseUri;
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }
    }

    public Task<JsonElement> ClaimWorkItemAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var input = ToObject(arguments);
        var payload = new JsonObject
        {
            ["workItemId"] = RequireString(input, "work_item_id"),
            ["workerAgentUid"] = RequireString(input, "worker_agent_uid"),
            ["requestedTtlSeconds"] = ReadInt(input, "requested_ttl_seconds") ?? 300,
            ["coordinationScopeId"] = RequireScopeId(input),
            ["repoId"] = ReadScopeField(input, "repo_id"),
            ["agentCapabilities"] = ReadStringArray(input, "agent_capabilities") ?? ReadScopeArray(input, "capabilities"),
            ["ownedPaths"] = ReadStringArray(input, "owned_paths") ?? ReadScopeArray(input, "owned_paths") ?? new JsonArray(),
            ["ownedSymbols"] = ReadStringArray(input, "owned_symbols") ?? ReadScopeArray(input, "owned_symbols") ?? new JsonArray()
        };
        return PostAsync("claims", payload, cancellationToken);
    }

    public Task<JsonElement> UpdateClaimAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var input = ToObject(arguments);
        var claimId = RequireString(input, "claim_id");
        var action = ReadString(input, "action") ?? "renew";
        var path = $"claims/{Uri.EscapeDataString(claimId)}/{action}";

        JsonObject payload = action switch
        {
            "renew" => new JsonObject
            {
                ["claimId"] = claimId,
                ["renewSeconds"] = ReadInt(input, "renew_seconds") ?? 300
            },
            "release" => new JsonObject
            {
                ["claimId"] = claimId,
                ["reason"] = ReadString(input, "reason")
            },
            "complete" => new JsonObject
            {
                ["claimId"] = claimId,
                ["summary"] = ReadString(input, "progress_summary") ?? ReadString(input, "summary") ?? "Completed.",
                ["changedPaths"] = ReadStringArray(input, "changed_paths") ?? new JsonArray()
            },
            _ => throw new InvalidOperationException(
                $"Unknown claim action '{action}'. Expected 'renew', 'release', or 'complete'.")
        };

        return PostAsync(path, payload, cancellationToken);
    }

    public Task<JsonElement> CreateWorkItemAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var input = ToObject(arguments);
        var payload = new JsonObject
        {
            ["workItemId"] = RequireString(input, "work_item_id"),
            ["workKind"] = NormalizeWorkKind(ReadString(input, "work_kind")),
            ["title"] = ReadString(input, "title") ?? RequireString(input, "work_item_id"),
            ["summary"] = ReadString(input, "summary"),
            ["priority"] = NormalizePriority(ReadString(input, "priority")),
            ["actionabilityScore"] = ReadDouble(input, "actionability_score") ?? 0.5,
            ["coordinationScopeId"] = RequireScopeId(input),
            ["targetRepoId"] = ReadString(input, "target_repo_id") ?? ReadScopeField(input, "repo_id"),
            ["targetBranch"] = ReadString(input, "target_branch") ?? ReadScopeField(input, "branch"),
            ["targetAgentUid"] = ReadString(input, "target_agent_uid"),
            ["correlationId"] = ReadString(input, "correlation_id"),
            ["sourceHandoffId"] = ReadString(input, "source_handoff_id"),
            ["stageId"] = ReadString(input, "stage_id"),
            ["requiredCapabilities"] = ReadStringArray(input, "required_capabilities") ?? new JsonArray(),
            ["ownedPaths"] = ReadStringArray(input, "owned_paths") ?? ReadScopeArray(input, "owned_paths") ?? new JsonArray(),
            ["ownedSymbols"] = ReadStringArray(input, "owned_symbols") ?? ReadScopeArray(input, "owned_symbols") ?? new JsonArray()
        };
        return PutAsync("work-items", payload, cancellationToken);
    }

    public Task<JsonElement> AcceptHandoffAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var input = ToObject(arguments);
        var workItemId = RequireString(input, "work_item_id");
        var payload = new JsonObject
        {
            ["workItemId"] = workItemId,
            ["acceptanceKind"] = NormalizeAcceptanceKind(ReadString(input, "acceptance_kind")),
            ["implementationClaimId"] = ReadString(input, "implementation_claim_id"),
            ["notes"] = ReadString(input, "acceptance_notes") ?? ReadString(input, "notes"),
            ["allowSelfAcceptance"] = ReadBool(input, "allow_self_acceptance") ?? false
        };
        return PostAsync($"work-items/{Uri.EscapeDataString(workItemId)}/accept", payload, cancellationToken);
    }

    public Task<JsonElement> PostBlackboardAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var input = ToObject(arguments);
        var payload = new JsonObject
        {
            ["workItemId"] = RequireString(input, "work_item_id"),
            ["coordinationScopeId"] = RequireScopeId(input),
            ["entryKind"] = NormalizeEntryKind(RequireString(input, "entry_kind")),
            ["summary"] = ReadString(input, "summary") ?? ReadString(input, "details") ?? "Update.",
            ["linkedArtifactIds"] = ReadStringArray(input, "artifact_ids") ?? new JsonArray(),
            ["supersedesEntryIds"] = ReadStringArray(input, "supersedes_entry_ids") ?? new JsonArray()
        };
        return PostAsync("blackboard", payload, cancellationToken);
    }

    private async Task<JsonElement> PostAsync(string path, JsonObject body, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(path, (JsonNode)body, JsonOptions, cancellationToken);
        return await ReadResponseAsync(response, cancellationToken);
    }

    private async Task<JsonElement> PutAsync(string path, JsonObject body, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PutAsJsonAsync(path, (JsonNode)body, JsonOptions, cancellationToken);
        return await ReadResponseAsync(response, cancellationToken);
    }

    private static async Task<JsonElement> ReadResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Surface the backend's error body verbatim so the agent can
            // react to ACOP error codes (claim_conflict, work_item_superseded, ...).
            throw new HttpRequestException(
                $"ACOP backend returned {(int)response.StatusCode} {response.StatusCode}. Body: {payload}",
                inner: null,
                statusCode: response.StatusCode);
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            using var emptyDoc = JsonDocument.Parse("{}");
            return emptyDoc.RootElement.Clone();
        }

        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }

    // ---- Helpers --------------------------------------------------------

    private static JsonObject ToObject(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return new JsonObject();
        }

        return JsonNode.Parse(arguments.GetRawText()) as JsonObject ?? new JsonObject();
    }

    private static string? ReadString(JsonObject input, string name)
    {
        if (!input.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        return node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
    }

    private static string RequireString(JsonObject input, string name)
    {
        var value = ReadString(input, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Required ACOP field '{name}' is missing or empty.");
        }

        return value;
    }

    private static int? ReadInt(JsonObject input, string name)
    {
        if (!input.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        return node.GetValueKind() == JsonValueKind.Number ? node.GetValue<int>() : null;
    }

    private static double? ReadDouble(JsonObject input, string name)
    {
        if (!input.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        return node.GetValueKind() == JsonValueKind.Number ? node.GetValue<double>() : null;
    }

    private static bool? ReadBool(JsonObject input, string name)
    {
        if (!input.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static JsonArray? ReadStringArray(JsonObject input, string name)
    {
        if (!input.TryGetPropertyValue(name, out var node) || node is not JsonArray array)
        {
            return null;
        }

        var copy = new JsonArray();
        foreach (var element in array)
        {
            if (element?.GetValueKind() == JsonValueKind.String)
            {
                copy.Add(element.GetValue<string>());
            }
        }

        return copy;
    }

    /// <summary>
    /// Pull a coordination-scope identifier from either a top-level
    /// <c>coordination_scope_id</c> / <c>scope_id</c> field or the
    /// <c>scope.scope_id</c> nested object that ACOP allows.
    /// </summary>
    private static string RequireScopeId(JsonObject input)
    {
        return ReadString(input, "coordination_scope_id")
            ?? ReadString(input, "scope_id")
            ?? ReadScopeField(input, "scope_id")
            ?? throw new InvalidOperationException("Required ACOP field 'coordination_scope_id' (or scope.scope_id) is missing.");
    }

    private static string? ReadScopeField(JsonObject input, string fieldName)
    {
        if (!input.TryGetPropertyValue("scope", out var node) || node is not JsonObject scope)
        {
            return null;
        }

        return scope.TryGetPropertyValue(fieldName, out var fieldNode)
            && fieldNode?.GetValueKind() == JsonValueKind.String
            ? fieldNode.GetValue<string>()
            : null;
    }

    private static JsonArray? ReadScopeArray(JsonObject input, string fieldName)
    {
        if (!input.TryGetPropertyValue("scope", out var node) || node is not JsonObject scope)
        {
            return null;
        }

        return scope.TryGetPropertyValue(fieldName, out var fieldNode) && fieldNode is JsonArray array
            ? new JsonArray(array.Select(item => item is null ? null : JsonValue.Create(item.ToJsonString())).Cast<JsonNode?>().ToArray())
            : null;
    }

    private static string NormalizeWorkKind(string? value) => string.IsNullOrWhiteSpace(value)
        ? "Implement"
        : value.Trim().ToLowerInvariant() switch
        {
            "implement" => "Implement",
            "refactor" => "Refactor",
            "review" => "Review",
            "validate" => "Validate",
            "repair" => "Repair",
            "handoff_followup" or "handofffollowup" => "HandoffFollowup",
            "merge_prepare" or "mergeprepare" => "MergePrepare",
            "conflict_resolution" or "conflictresolution" => "ConflictResolution",
            _ => value.Trim()
        };

    private static string NormalizePriority(string? value) => string.IsNullOrWhiteSpace(value)
        ? "Medium"
        : value.Trim().ToLowerInvariant() switch
        {
            "low" => "Low",
            "medium" => "Medium",
            "high" => "High",
            "urgent" => "Urgent",
            _ => value.Trim()
        };

    private static string NormalizeAcceptanceKind(string? value) => string.IsNullOrWhiteSpace(value)
        ? "Reviewed"
        : value.Trim().ToLowerInvariant() switch
        {
            "reviewed" => "Reviewed",
            "accepted" => "Accepted",
            "released" => "Released",
            _ => value.Trim()
        };

    private static string NormalizeEntryKind(string value) => value.Trim().ToLowerInvariant() switch
    {
        "finding" => "Finding",
        "hypothesis" => "Hypothesis",
        "partial_result" or "partialresult" => "PartialResult",
        "risk" => "Risk",
        "decision" => "Decision",
        "constraint" => "Constraint",
        "review_request" or "reviewrequest" => "ReviewRequest",
        "integration_note" or "integrationnote" => "IntegrationNote",
        _ => value.Trim()
    };

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}
