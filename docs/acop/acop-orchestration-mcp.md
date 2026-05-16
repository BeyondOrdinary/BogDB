# ACOP Orchestration MCP Query Surface

## Purpose

This document defines a reusable MCP-facing query surface for ACOP orchestration state.

It sits above raw Cypher and below orchestration middleware:

- Cypher remains the database-native query language
- orchestration middleware remains the live coordination owner
- MCP query templates give agents and tools a stable read surface for orchestration status

The goal is to avoid forcing every agent to:

- know the exact graph shape
- hand-author Cypher
- re-encode the same orchestration queries repeatedly

## Relationship to other documents

- [acop-orchestration.md](./acop-orchestration.md)
- [acop-orchestration.schema.json](./acop-orchestration.schema.json)
- [acop-orchestration-cypher.md](./acop-orchestration-cypher.md)

## Design principles

- read-only
- flow-aware
- acceptance-aware
- repo/workspace scoped
- agent-friendly
- thin wrapper over durable orchestration truth

This surface should not:

- create or mutate claims
- grant or revoke leases
- accept or reject gates directly
- become the scheduler

## Recommended MCP tools

### `orchestration_flow_summary`

Returns a high-level dashboard for one flow.

Inputs:

- `flowId`

Outputs:

- `flow_id`
- `title`
- `stage_count`
- `lane_count`
- `gate_count`
- `accepted_lane_count`

Use when:

- a lead agent wants the current state of one orchestration flow
- a worker wants to confirm whether a flow is still active and progressing

### `orchestration_pending_gates`

Returns gates waiting on acceptance.

Inputs:

- optional `flowId`
- optional `repoId`
- optional `stageId`

Outputs:

- `gate_id`
- `gate_title`
- `gate_kind`
- `acceptance_states`
- optional blocking lane summary

Use when:

- an agent wants to know what is preventing downstream release

### `orchestration_release_ready_gates`

Returns gates whose upstream lanes are accepted and are ready for owner action.

Inputs:

- optional `flowId`
- optional `repoId`
- optional `ownerRole`

Outputs:

- `gate_id`
- `gate_title`
- `gate_kind`
- `owner_role`

Use when:

- an integration or reviewer agent wants the next actionable gate

### `orchestration_unreleased_stages`

Returns stages that remain unreleased because upstream gates are not accepted.

Inputs:

- optional `flowId`
- optional `repoId`

Outputs:

- `stage_id`
- `stage_title`
- `blocking_gate_id`
- `blocking_gate_title`

Use when:

- a lead agent wants to understand why a later phase has not opened

### `orchestration_lane_acceptance_gaps`

Returns lanes whose work is complete but not accepted.

Inputs:

- optional `flowId`
- optional `repoId`
- optional `stageId`

Outputs:

- `lane_id`
- `lane_title`
- `work_item_ids`
- `acceptance_states`

Use when:

- a reviewer or lead agent wants to close the gap between work completion and orchestration release

### `orchestration_blocked_work`

Returns work items blocked specifically by orchestration state.

Inputs:

- optional `flowId`
- optional `repoId`
- optional `targetAgentUid`

Outputs:

- `work_item_id`
- `title`
- `blocker_code`
- `summary`

Recommended blocker filters:

- `upstream_gate_pending`
- `upstream_stage_unreleased`
- `acceptance_pending`

Use when:

- a worker wants to know why work is blocked
- a lead agent wants to find work that can be unblocked by acceptance rather than reimplementation

## Recommended result conventions

All orchestration MCP responses should ideally carry:

- `flow_id` when known
- `target_repo_id` when known
- `query_kind`
- `generated_at_utc`
- `rows`

Optional additions:

- `cypher_template_id`
- `truncated`
- `next_suggested_queries`

## Suggested query kinds

Stable query kind names help agents reason without re-learning tool-specific phrasing:

- `flow_summary`
- `pending_gates`
- `release_ready_gates`
- `unreleased_stages`
- `lane_acceptance_gaps`
- `blocked_work`

## Agent-facing usage patterns

Examples:

- "show me release-ready gates for repo `repo:example-project`"
- "show me pending gates for flow `flow:example-project:phase-delivery`"
- "show me blocked work caused by orchestration state for agent `worker-http`"

These should map to stable MCP calls instead of requiring the caller to author Cypher.

## Why this matters

Without this layer:

- every agent must understand graph internals
- every client duplicates orchestration query logic
- orchestration observability becomes brittle

With this layer:

- the read-side MCP becomes a reusable orchestration visibility plane
- ACOP orchestration stays transport-neutral
- middleware and agents can share one stable read contract
