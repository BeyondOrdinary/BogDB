using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace BogDb.Acop.Mcp.Server.Adapters;

/// <summary>
/// Backend adapter that calls an HTTP-bound ACOP coordination middleware.
///
/// Endpoint paths follow the conventions documented in the ACOP HTTP+JSON
/// binding (see docs/acop). They are intentionally relative so any
/// implementation rooted at a different base path can be wired up without
/// code changes — pass <c>--base-uri</c> at startup or set
/// <c>ACOP_BACKEND_BASE_URI</c>.
/// </summary>
public sealed class HttpAcopBackend : IAcopBackend, IDisposable
{
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
        => PostAsync("claims", arguments, cancellationToken);

    public async Task<JsonElement> UpdateClaimAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        // Routed by the "action" field on the request body to keep the
        // backend-facing surface stable across implementations:
        //   action = "renew" -> POST claims/{claim_id}/renew
        //   action = "release" -> POST claims/{claim_id}/release
        //   action = "complete" -> POST claims/{claim_id}/complete
        if (!arguments.TryGetProperty("claim_id", out var claimIdElement) || claimIdElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("update_claim requires a string 'claim_id'.");
        }

        var claimId = claimIdElement.GetString() ?? throw new InvalidOperationException("'claim_id' is empty.");
        var action = arguments.TryGetProperty("action", out var actionElement) && actionElement.ValueKind == JsonValueKind.String
            ? actionElement.GetString()
            : "renew";
        return action switch
        {
            "renew" => await PostAsync($"claims/{Uri.EscapeDataString(claimId)}/renew", arguments, cancellationToken),
            "release" => await PostAsync($"claims/{Uri.EscapeDataString(claimId)}/release", arguments, cancellationToken),
            "complete" => await PostAsync($"claims/{Uri.EscapeDataString(claimId)}/complete", arguments, cancellationToken),
            _ => throw new InvalidOperationException($"Unknown claim action '{action}'. Expected 'renew', 'release', or 'complete'.")
        };
    }

    public Task<JsonElement> CreateWorkItemAsync(JsonElement arguments, CancellationToken cancellationToken)
        => PutAsync("work-items", arguments, cancellationToken);

    public async Task<JsonElement> AcceptHandoffAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetProperty("work_item_id", out var workItemIdElement) || workItemIdElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("accept_handoff requires a string 'work_item_id'.");
        }

        var workItemId = workItemIdElement.GetString() ?? throw new InvalidOperationException("'work_item_id' is empty.");
        return await PostAsync($"work-items/{Uri.EscapeDataString(workItemId)}/accept", arguments, cancellationToken);
    }

    public Task<JsonElement> PostBlackboardAsync(JsonElement arguments, CancellationToken cancellationToken)
        => PostAsync("blackboard", arguments, cancellationToken);

    private async Task<JsonElement> PostAsync(string path, JsonElement body, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(path, body, cancellationToken);
        return await ReadResponseAsync(response, cancellationToken);
    }

    private async Task<JsonElement> PutAsync(string path, JsonElement body, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PutAsJsonAsync(path, body, cancellationToken);
        return await ReadResponseAsync(response, cancellationToken);
    }

    private static async Task<JsonElement> ReadResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Surface the backend's error body so the agent can react to
            // ACOP error codes (e.g. claim_conflict, work_item_superseded).
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

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}
