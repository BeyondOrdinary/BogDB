using BogDb.Acop.Mcp.Server;
using BogDb.Acop.Mcp.Server.Adapters;

// Configuration is intentionally minimal: the MCP server speaks the ACOP
// write surface; everything else (auth, backend URL) comes from env vars or
// command-line flags so the binary can be wired up as a `dotnet tool` without
// shipping config files.
var baseUriString = Environment.GetEnvironmentVariable("ACOP_BACKEND_BASE_URI");
var bearerToken = Environment.GetEnvironmentVariable("ACOP_BACKEND_BEARER_TOKEN");
var tokenEndpoint = Environment.GetEnvironmentVariable("ACOP_BACKEND_TOKEN_ENDPOINT");
var clientId = Environment.GetEnvironmentVariable("ACOP_BACKEND_CLIENT_ID");
var clientSecret = Environment.GetEnvironmentVariable("ACOP_BACKEND_CLIENT_SECRET");
var audience = Environment.GetEnvironmentVariable("ACOP_BACKEND_AUDIENCE");

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--base-uri":
            baseUriString = i + 1 < args.Length ? args[++i] : null;
            break;
        case "--bearer-token":
            bearerToken = i + 1 < args.Length ? args[++i] : null;
            break;
        case "--token-endpoint":
            tokenEndpoint = i + 1 < args.Length ? args[++i] : null;
            break;
        case "--client-id":
            clientId = i + 1 < args.Length ? args[++i] : null;
            break;
        case "--client-secret":
            clientSecret = i + 1 < args.Length ? args[++i] : null;
            break;
        case "--audience":
            audience = i + 1 < args.Length ? args[++i] : null;
            break;
        case "--help":
        case "-h":
            Console.Error.WriteLine("Usage: acop-mcp [--base-uri <url>] [--bearer-token <token>] [--token-endpoint <url> --client-id <id> --client-secret <secret> [--audience <aud>]]");
            Console.Error.WriteLine("Required env / flags:");
            Console.Error.WriteLine("  ACOP_BACKEND_BASE_URI  HTTP base URI of the ACOP coordination middleware.");
            Console.Error.WriteLine("  ACOP_BACKEND_BEARER_TOKEN  (optional) bearer token for authenticated calls.");
            Console.Error.WriteLine("  ACOP_BACKEND_TOKEN_ENDPOINT / CLIENT_ID / CLIENT_SECRET  (optional) OIDC client_credentials token source.");
            Console.Error.WriteLine("  ACOP_BACKEND_AUDIENCE  (optional) token audience.");
            return 0;
    }
}

if (string.IsNullOrWhiteSpace(baseUriString) || !Uri.TryCreate(baseUriString, UriKind.Absolute, out var baseUri))
{
    Console.Error.WriteLine("acop-mcp: ACOP_BACKEND_BASE_URI or --base-uri must be set to an absolute URL.");
    return 1;
}

using var tokenProvider = AcopTokenProvider.Create(bearerToken, tokenEndpoint, clientId, clientSecret, audience);
using var backend = new HttpAcopBackend(baseUri, tokenProvider);
var host = new McpServerHost(backend);
await host.RunAsync();
return 0;
