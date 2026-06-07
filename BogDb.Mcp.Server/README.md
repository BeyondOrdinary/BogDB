# BogDb MCP Server

`BogDb.Mcp.Server` is the first-class MCP host for BogDB.

Current phase-1 tool surface:
- `bogdb_query`
- `bogdb_schema`
- `bogdb_tables`
- `bogdb_table_info`

Code-intelligence tool surface (see "Code intelligence query surface" below):
- `code_symbol_search`
- `code_dependencies`
- `code_refactor_hotspots`

Current phase-1 resource surface:
- handoff resources listed from `.handoffs/index.json` or `.bo/handoffs/index.json`
- MCP `resources/read` returns the handoff envelope JSON for a listed resource

Current phase-1 rules:
- MCP runs over stdio
- `bogdb_query` is read-only
- query results are row-limited and report truncation
- schema tools read directly from the BogDB catalog/runtime metadata surface
- handoff resources are workspace-scoped and read-only

Current handoff coordination surface:
- `handoff_query` can filter indexed handoffs by kind or agent metadata
- multi-agent flows can ask for the latest handoff targeting one agent UID without assuming one producer
- convenience filters:
  - `latestForTargetAgentUid`
  - `latestReadyForTargetAgentUid`
  - `latestReadyVerificationForTargetAgentUid`
  - `latestReadyVerificationPickupForTargetAgentUid`
  - `groupReadyVerificationHandoffsForTargetAgentUid`
  - `groupReadyVerificationPickupHandoffsForTargetAgentUid`
  - `bestReadyVerificationBatchForTargetAgentUid`
  - `bestReadyVerificationWorkForTargetAgentUid`
  - `bestReadyVerificationPickupWorkForTargetAgentUid`
  - `latestBetweenAgentAUid` + `latestBetweenAgentBUid`
  - `latestActionableForTargetAgentUid`

For BO-style targeted follow-up handoffs such as
`orchestration_acceptance_verification_followup`, the most direct pickup query is:

- `handoff_query(handoffKind=..., latestReadyForTargetAgentUid=...)`

That lets a lead agent or middleware ask for the newest ready handoff for one worker without
reconstructing broader filter logic from the raw index.

For BO verification-ingest handoffs such as `orchestration_acceptance_verification`, BogDb MCP now
also supports:

- `handoff_query(latestReadyVerificationForTargetAgentUid=...)`
- `handoff_query(groupReadyVerificationHandoffsForTargetAgentUid=...)`
- `handoff_query(bestReadyVerificationBatchForTargetAgentUid=...)`
- `handoff_query(bestReadyVerificationWorkForTargetAgentUid=...)`

For BO-selected verification pickup handoffs such as `orchestration_verification_pickup`, BogDb MCP
also supports:

- `handoff_query(latestReadyVerificationPickupForTargetAgentUid=...)`
- `handoff_query(groupReadyVerificationPickupHandoffsForTargetAgentUid=...)`
- `handoff_query(bestReadyVerificationPickupWorkForTargetAgentUid=...)`

Those pickup-handoff queries reuse the deterministic BO/BogDb pickup signals already baked into the
handoff payload:
- `dominantPickupPressure`
- `pickupFactorSummary`

So worker routing can stay handoff-native while still respecting the scored ranking factors that
produced the selected verification work in the first place.

The grouped verification form clusters related ready receipts by target family so one worker can
pick up a small high-impact, low-context-switch batch instead of an arbitrary list of unrelated
verification receipts. Grouped responses now also include:
- `oldestGeneratedAtUtc`
- `newestGeneratedAtUtc`
- `ageHours`
- `ageUrgencyScore`
- `averageBlockerCount`
- `blockerPressureScore`
- `blockerPenaltyScore`
- `dominantPickupPressure`
- `pickupFactorSummary`

That age signal is used as a late ranking tie-breaker, so structurally similar verification groups
that are getting stale can float above equally cheap, equally related newer groups.
Blocker pressure is also factored into ranking, so equally related verification work with fewer
follow-on blocker codes can outrank “ready” work that is more likely to bounce back for another
coordination step.
`dominantPickupPressure` is a deterministic explanation label derived from those scored features,
not a vibe-based summary.
`pickupFactorSummary` is the compact raw score object for the winning selection, intended for
schedulers and middleware that want the numbers without unpacking the whole batch payload.

The `bestReadyVerificationWorkForTargetAgentUid` shortcut goes one step further and chooses the
best pickup shape automatically:
- returns a `batch` when a related cluster is clearly the better low-cost, higher-impact move
- falls back to a `single` ready verification handoff when batching would not help
- returns `dominantPickupPressure` for both the overall selection and the winning batch/entry
- returns `pickupFactorSummary` for both the overall selection and the winning batch/entry

Code intelligence query surface (over a BO `.bo/graph` code graph, BoV01 schema):
- `code_symbol_search` — find symbols by case-insensitive name substring, optional `kind`
- `code_dependencies` — symbol→symbol edges (calls / uses-type / instantiates), anchored on a `symbol` and/or `file`, with `direction` of in/out/both
- `code_refactor_hotspots` — files ranked by refactor pressure score, with `minScore` / `recommendation` filters
- read-only; curated Cypher over the BO graph so agents don't hand-write it
- `databasePath` is optional and defaults to `<BO_WORKSPACE_ROOT or CWD>/.bo/graph` — i.e. a launched agent's own worktree
- this is the agent runtime code-intelligence path: `bo index` writes `.bo/graph`; agents query it through these tools

Current orchestration query surface:
- `orchestration_pending_gates`
- `orchestration_release_ready_gates`
- `orchestration_blocked_work`
- `orchestration_lane_acceptance_gaps`
- `orchestration_acceptance_ingest_status`
- `orchestration_acceptance_verification_status`
- `orchestration_acceptance_ingests_awaiting_local_verification`
- these queries are read-only status views over orchestration state persisted into BogDb
- they are designed for orchestration middleware and agents to ask:
  - which gates still need acceptance
  - which gates are ready to release downstream work
  - which work items are blocked because orchestration state has not advanced yet
  - which lanes are complete but still waiting on explicit acceptance
  - which acceptance ingests have reached BogDb but still do not have a local BO verification receipt
- optional filters include repo, flow, stage, owner role, and target agent UID where applicable

Current orchestration write surface:
- `orchestration_record_acceptance`
- `orchestration_ingest_acceptance_artifacts`
- `orchestration_ingest_acceptance_verification_artifacts`
- this is a narrow durable write tool for acceptance state
- it persists `AcceptanceRecord` nodes into BogDb for lanes, gates, or stages
- it is intended to feed the durable orchestration/query plane, not to become lease or scheduler logic

Current acceptance ingestion bridge:
- `orchestration_ingest_acceptance_artifacts` reads BO-style acceptance artifacts or an acceptance
  index from the filesystem
- this lets BO publish durable acceptance artifacts through its ACOP runtime path while BogDb MCP
  ingests them into `AcceptanceRecord` nodes for orchestration queries
- it is an explicit intake step, not a watcher or background scheduler
- this keeps the boundary clean:
  - BO publishes durable acceptance evidence
  - BogDb ingests and queries durable orchestration state
  - orchestration middleware still decides when and why ingestion should run in a larger workflow

Acceptance ingest lifecycle:
- BogDb now also persists a durable `AcceptanceIngestRecord` during artifact ingestion
- `orchestration_acceptance_ingest_status` exposes that lifecycle record so agents and middleware
  can ask:
  - has this acceptance been ingested yet?
  - when was it ingested?
  - was it ingested from a single artifact or from an acceptance index?
- that makes publish/ingest progress queryable instead of only inferred from downstream gate state
- BogDb can now also ingest BO local verification receipts into durable
  `AcceptanceVerificationRecord` nodes with:
  - `orchestration_ingest_acceptance_verification_artifacts`
- `orchestration_acceptance_verification_status` exposes those durable verification records
- `orchestration_acceptance_ingests_awaiting_local_verification` now prefers those durable
  verification records and only falls back to the workspace-local BO verification index at:
  - `.bo/orchestration/acceptance-verifications/index.json`
- this keeps the publish -> ingest -> verify loop queryable in the server plane while preserving
  compatibility during the transition

Acceptance model:
- BogDb currently supports two acceptance shapes during the transition:
  - legacy edge-based acceptance: `(:AcceptanceRecord)-[:ACCEPTS]->(:OrchestrationGate|:OrchestrationLane|:OrchestrationStage)`
  - property-based durable acceptance records with:
    - `target_id`
    - `target_kind`
    - `acceptance_status`
- orchestration queries read both forms so callers can persist acceptance without depending on one exact graph shape

Important boundary:
- BogDb is the durable orchestration memory and query plane
- acceptance persistence belongs here because Cypher/MCP queries need to answer release and gap questions
- live claims, leases, arbitration, retry policy, and stale-work cleanup still belong to orchestration middleware above this MCP surface

Generic handoff index contract:
- `protocol_version`
- `generated_at_utc`
- `resources[]`
- each resource entry can carry:
  - `artifact_id`
  - `resource_uri`
  - `handoff_kind`
  - `generated_at_utc`
  - `relative_path`
  - `producer`
  - `created_by_agent_uid`
  - `target_agent_uid`
  - `status`
  - `blocker_codes`
  - `actionability_score`

Run locally:

```bash
dotnet run --project /Users/demouser/core/gitroot/BogDB/BogDb.Mcp.Server/BogDb.Mcp.Server.csproj
```

The transport contract is intentionally thin so BO and future agent tooling can treat this as the database-native MCP entry point without coupling to samples or extension internals.
