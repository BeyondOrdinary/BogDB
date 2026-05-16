# BogDb.Acop.Mcp.Server

`BogDb.Acop.Mcp.Server` is the reference Model Context Protocol server for
the [Agentic Code Orchestration Protocol (ACOP)](../docs/acop/README.md). It
exposes the ACOP write-side coordination tools — claim a work item, update a
claim, create a work item, accept a handoff, post a blackboard entry — over
stdio JSON-RPC so any MCP-capable agent (Claude Code, Codex, custom
harnesses) can drive coordination state via tool calls.

The server itself contains no coordination policy. Tool calls are forwarded
to an `IAcopBackend` implementation; the shipped `HttpAcopBackend` calls a
configurable HTTP-bound coordination middleware. This separation keeps the
MCP surface vendor-neutral.

## Why a separate MCP server?

The companion `BogDb.Mcp.Server` exposes a **read** surface
(`orchestration_pending_gates`, `orchestration_blocked_work`, etc.). Per the
ACOP MCP design principle, that read surface MUST NOT create or mutate
claims — read tools and write tools are intentionally split. This server
hosts the write tools.

## Tools

| Tool                       | Purpose                                                                          |
| -------------------------- | -------------------------------------------------------------------------------- |
| `acop_claim_work_item`     | Submit a claim intent for a work item.                                           |
| `acop_update_claim`        | Renew, release, or complete an active claim.                                     |
| `acop_create_work_item`    | Seed a work item with a stable `work_item_id` (idempotent at the backend).       |
| `acop_accept_handoff`      | Accept a handoff; triggers downstream release-policy evaluation.                 |
| `acop_post_blackboard`     | Append a blackboard entry against a work item.                                   |

See [docs/acop/acop.md](../docs/acop/acop.md) for full semantics and
[docs/acop/acop.schema.json](../docs/acop/acop.schema.json) for field
contracts.

## Running

```bash
# Install as a dotnet tool from the published package
dotnet tool install --global BogDB.Acop.Mcp.Server

# Run, pointed at an ACOP-compatible HTTP coordination middleware
acop-mcp --base-uri http://localhost:5000/ --bearer-token "$ACOP_TOKEN"
```

Environment-variable equivalents:

| Variable                      | Effect                                                  |
| ----------------------------- | ------------------------------------------------------- |
| `ACOP_BACKEND_BASE_URI`       | Base URL of the coordination middleware.                |
| `ACOP_BACKEND_BEARER_TOKEN`   | Bearer token sent on every backend call (optional).     |

## Configuring agents

Add `acop-mcp` to the agent's MCP server list. For Claude Code, drop a
`.mcp.json` next to the project root:

```json
{
  "mcpServers": {
    "acop": {
      "command": "acop-mcp",
      "args": ["--base-uri", "http://localhost:5000/"],
      "env": {
        "ACOP_BACKEND_BEARER_TOKEN": "<token>"
      }
    }
  }
}
```

Codex uses a similar entry under `[mcp_servers]` in `~/.codex/config.toml`.

## Adapter contract

Backend implementations implement `IAcopBackend` (in `Adapters/`). The
shipped HTTP adapter is one example; in-memory or library-bound adapters
can be added without touching the protocol host.

## License

Same as the rest of BogDB.
