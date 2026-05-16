# ACOP — Agentic Code Orchestration Protocol

ACOP is a vendor-neutral coordination protocol for multi-agent software
development work. It defines work-item, claim, blackboard, and orchestration
semantics that sit *above* MCP (tool/context access) and A2A
(agent-to-agent messaging).

These documents are an early draft (v0.1). See [TODO.md](./TODO.md) for the
work required to promote the spec to public v1.0.

## Documents

| File                                                           | What it defines                                            |
| -------------------------------------------------------------- | ---------------------------------------------------------- |
| [acop.md](./acop.md)                                           | Core protocol: work items, claims, blackboard, artifacts.  |
| [acop.schema.json](./acop.schema.json)                         | JSON Schema for the core contract.                         |
| [acop-orchestration.md](./acop-orchestration.md)               | Orchestration extension: stages, lanes, gates, acceptance. |
| [acop-orchestration.schema.json](./acop-orchestration.schema.json) | JSON Schema for orchestration flows.                       |
| [acop-orchestration-mcp.md](./acop-orchestration-mcp.md)       | Recommended MCP **read** tools for orchestration state.    |
| [acop-orchestration-cypher.md](./acop-orchestration-cypher.md) | Cypher query templates for graph-projected orchestration.  |
| [acop-compliance.md](./acop-compliance.md)                     | Compliance extension: requirements, evidence, exceptions.  |
| [acop-compliance.schema.json](./acop-compliance.schema.json)   | JSON Schema for the compliance extension.                  |
| [acop_examples.md](./acop_examples.md)                         | Worked examples of the core and extension shapes.          |

## Layering

ACOP layers on top of existing standards:

- **MCP** — tools, resources, and context exposure
- **A2A** — inter-agent communication and delegation
- **ACOP** — code-work coordination semantics layered on top

ACOP does not replace MCP or A2A; it specifies the contract those transports
carry for work coordination.

## Reference implementation

The reference write-side MCP server for ACOP coordination lives in
[`BogDb.Acop.Mcp.Server`](../../BogDb.Acop.Mcp.Server). It exposes ACOP
claim/complete/work-item/blackboard tools over stdio JSON-RPC and delegates
the actual coordination authority to a pluggable backend adapter.

The read-side MCP surface (orchestration state, blocked work, gate status)
is provided by [`BogDb.Mcp.Server`](../../BogDb.Mcp.Server) and reads from
the graph projection of coordination state.
