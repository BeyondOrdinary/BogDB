using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BogDb.Acop.Mcp.Server.Adapters;
using Xunit;

namespace BogDb.Tests.Mcp;

/// <summary>
/// The acop_update_claim payloads must bind to CLAiR's request records. A renew that sent
/// renewSeconds instead of the required requestedTtlSeconds failed model binding with a
/// bodiless 400 on every call (ASE Run B :001, 6 of 6 agent renews).
/// </summary>
public sealed class AcopHttpBackendClaimTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public JsonObject? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath;
            Body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }
    }

    private static async Task<CapturingHandler> UpdateAsync(string json)
    {
        var handler = new CapturingHandler();
        using var backend = new HttpAcopBackend(new Uri("http://clair.test/"), httpClient: new HttpClient(handler));
        using var arguments = JsonDocument.Parse(json);
        await backend.UpdateClaimAsync(arguments.RootElement, CancellationToken.None);
        return handler;
    }

    [Fact]
    public async Task Renew_SendsRequestedTtlSecondsAndProgress()
    {
        var sent = await UpdateAsync("""{"claim_id":"claim:a","action":"renew","progress_summary":"wave 0 done"}""");

        Assert.Equal("/claims/claim%3Aa/renew", sent.Path);
        Assert.Equal("claim:a", sent.Body!["claimId"]!.GetValue<string>());
        Assert.Equal(300, sent.Body["requestedTtlSeconds"]!.GetValue<int>());
        Assert.Equal("wave 0 done", sent.Body["progressSummary"]!.GetValue<string>());
        Assert.False(sent.Body.ContainsKey("renewSeconds"));
    }

    [Fact]
    public async Task Renew_HonoursExplicitTtlAndLegacyName()
    {
        var explicitTtl = await UpdateAsync("""{"claim_id":"c","action":"renew","requested_ttl_seconds":900}""");
        var legacy = await UpdateAsync("""{"claim_id":"c","action":"renew","renew_seconds":600}""");

        Assert.Equal(900, explicitTtl.Body!["requestedTtlSeconds"]!.GetValue<int>());
        Assert.Equal(600, legacy.Body!["requestedTtlSeconds"]!.GetValue<int>());
    }
}
