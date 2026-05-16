using System.Text.Json;

namespace BogDb.Acop.Mcp.Server.Adapters;

/// <summary>
/// Backend-neutral interface for ACOP coordination authority. The MCP server
/// stays a thin protocol/transport host; this interface is what every
/// coordination middleware implementation plugs into.
///
/// Implementations are expected to be the source of truth for claim grants,
/// work-item lifecycle transitions, and blackboard ordering. The MCP server
/// does not arbitrate; it just relays.
/// </summary>
public interface IAcopBackend
{
    /// <summary>
    /// Submit a claim intent for a work item. Returns the granted claim on
    /// success or throws on conflict. Implementations enforce single-owner
    /// semantics and lease TTLs.
    /// </summary>
    Task<JsonElement> ClaimWorkItemAsync(JsonElement arguments, CancellationToken cancellationToken);

    /// <summary>
    /// Send a heartbeat or release/complete signal against an active claim.
    /// </summary>
    Task<JsonElement> UpdateClaimAsync(JsonElement arguments, CancellationToken cancellationToken);

    /// <summary>
    /// Create or upsert a work item. Used by lead/supervisor agents to seed
    /// new coordination units. The arguments MUST carry a stable
    /// <c>work_item_id</c> so idempotent re-submissions can be deduped by
    /// the backend.
    /// </summary>
    Task<JsonElement> CreateWorkItemAsync(JsonElement arguments, CancellationToken cancellationToken);

    /// <summary>
    /// Accept a handoff against a work item. Causes downstream readiness to
    /// open if release policies are satisfied.
    /// </summary>
    Task<JsonElement> AcceptHandoffAsync(JsonElement arguments, CancellationToken cancellationToken);

    /// <summary>
    /// Post a blackboard entry against a work item. Implementations append;
    /// they do not edit.
    /// </summary>
    Task<JsonElement> PostBlackboardAsync(JsonElement arguments, CancellationToken cancellationToken);
}
