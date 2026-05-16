# ACOP v0.1 Draft

## Purpose

ACOP stands for `Agentic Code Orchestration Protocol`.

It defines the coordination semantics needed for multi-agent software development work that are not
fully covered by:

- `MCP`, which standardizes model-to-tool/context access
- `A2A`, which standardizes agent-to-agent interoperability and message exchange

ACOP is not intended to replace either of those.

The intended stack is:

- `MCP` for tools, resources, and context
- `A2A` for inter-agent communication and delegation
- `ACOP` for code-work coordination semantics layered on top

## Problem statement

Agentic code generation needs stronger coordination than "send another agent a message" and
stronger structure than "call a tool."

The missing layer is responsible for:

- what work exists
- who should pick it up
- whether it is ready
- what blocks it
- whether it has been claimed
- how long a claim is valid
- what artifacts, branches, or files are in scope
- what validation and review requirements apply
- whether work has been completed, consumed, or superseded

Without that layer, agents step on each other during cross-agent development work.

## Design goals

- strong work identity
- explicit readiness and blocker state
- clean claim / lease hooks
- repo / branch / artifact aware
- review and validation aware
- transport-neutral semantics
- compatible with A2A and MCP rather than competitive with them

## Non-goals

ACOP does not define:

- a replacement for MCP tool/resource transport
- a replacement for A2A messaging
- source-code semantics for one programming language
- storage implementation details
- scheduler internals

ACOP defines the coordination contract, not the full runtime.

## Layering

### Producers

A planning producer (for example, a refactor planner or work-decomposition service)
emits:

- work items
- plans
- handoffs
- blockers
- readiness evidence
- artifact and operation identities

### Query and transport

A read-side MCP server projects coordination state and exposes:

- resources
- artifact reads
- indexed work lookup
- filtered handoff queries

### Coordination owner

An orchestration middleware layer owns:

- claims
- leases
- arbitration
- retries
- stale-work recovery
- pickup and completion transitions

### Workers

Coding agents, review agents, and validation agents consume ACOP work items and act on them.

## Core concepts

### `WorkItem`

The top-level unit of coordinated code work.

Required fields:

- `work_item_id`
- `protocol_version`
- `work_kind`
- `created_at_utc`
- `producer`
- `status`
- `priority`
- `actionability_score`

Recommended fields:

- `title`
- `summary`
- `target_repo_id`
- `target_workspace_root`
- `target_branch`
- `target_worktree`
- `source_handoff_id`
- `correlation_id`

### `WorkKind`

Examples:

- `implement`
- `refactor`
- `review`
- `validate`
- `repair`
- `handoff_followup`
- `merge_prepare`

### `Status`

Recommended baseline enum:

- `ready`
- `requires_attention`
- `blocked`
- `claimed`
- `in_progress`
- `completed`
- `consumed`
- `cancelled`
- `superseded`

Semantics:

- `ready`: immediately pickable
- `requires_attention`: human or lead-agent decision likely needed, but still relevant
- `blocked`: not pickable until blocker resolution
- `claimed`: reserved by one worker under a live claim
- `in_progress`: actively being executed
- `completed`: worker asserts done
- `consumed`: downstream system accepted output and no further pickup should occur
- `cancelled`: abandoned intentionally
- `superseded`: replaced by a newer work item

### `Blocker`

Represents why work is not directly actionable.

Required fields:

- `blocker_code`
- `severity`
- `summary`

Recommended fields:

- `artifact_id`
- `operation_id`
- `depends_on_work_item_id`
- `depends_on_external_decision`
- `suggested_resolution`

Examples:

- `seam_extraction_required`
- `host_contract_unresolved`
- `branch_conflict_risk`
- `validation_environment_missing`
- `review_required`

### `Operation`

A planned step in a work item.

Required fields:

- `operation_id`
- `kind`
- `description`

Recommended fields:

- `depends_on_operation_ids`
- `outcome_target`
- `artifact_ids`
- `validation_focus`

Examples:

- `extract_seam`
- `materialize_contract`
- `materialize_core`
- `apply_patch`
- `run_tests`
- `request_review`

### `Artifact`

A stable object the work item refers to.

Required fields:

- `artifact_id`
- `artifact_kind`
- `artifact_role`

Recommended fields:

- `resource_uri`
- `repo_relative_path`
- `namespace_hint`
- `symbol_hint`
- `readiness`

Examples:

- source file
- patch preview
- contract interface
- generated scaffold
- registration preview
- review report

### `ValidationRequirement`

Describes the evidence required before a work item can complete.

Examples:

- targeted unit test pass
- structural validation pass
- review acknowledgment
- build pass
- snapshot update

### `BlackboardEntry`

Represents a shared coordination note or intermediate artifact in a multi-agent development flow.

Blackboard coordination belongs in ACOP core because it is a general collaboration primitive rather
than a domain-specific extension.

Required fields:

- `entry_id`
- `work_item_id`
- `entry_kind`
- `author_agent_uid`
- `created_at_utc`
- `status`

Recommended fields:

- `summary`
- `details`
- `artifact_ids`
- `operation_ids`
- `confidence`
- `supersedes_entry_ids`

Example `entry_kind` values:

- `finding`
- `hypothesis`
- `partial_result`
- `risk`
- `decision`
- `constraint`
- `review_request`
- `integration_note`

Design intent:

- the blackboard is the shared development surface where agents can publish partial understanding
- it should support incremental progress without forcing early completion claims
- it should let lead and worker agents coordinate around evidence instead of only around chat

## Claim and lease hooks

ACOP should define hooks for claim / lease semantics even if the actual lease authority lives in
middleware.

### `ClaimIntent`

Represents an attempt by a worker to take ownership of a work item.

Fields:

- `claim_intent_id`
- `work_item_id`
- `worker_agent_uid`
- `requested_at_utc`
- `requested_ttl_seconds`
- `scope`

### `Claim`

Represents granted temporary ownership.

Fields:

- `claim_id`
- `work_item_id`
- `worker_agent_uid`
- `granted_at_utc`
- `expires_at_utc`
- `claim_status`

`claim_status` examples:

- `active`
- `released`
- `expired`
- `revoked`

### `Lease heartbeat`

Optional liveness updates from the worker or middleware.

Fields:

- `claim_id`
- `observed_at_utc`
- `progress_status`
- `progress_summary`

### Claim design rules

- ACOP must support claim / lease semantics because cross-agent code work frequently collides.
- Claim truth should live in orchestration middleware, not in producers and not in MCP.
- Producers may emit claimable work and MCP may expose claim state, but neither should silently
  become the lease owner.

## Pickup semantics

The main query shape ACOP should enable is:

- latest actionable work for agent X

Secondary useful shapes:

- latest work between agent A and agent B
- all blocked work for repo Y
- all claimable work for branch Z
- all work blocked on blocker code K

This is why ACOP needs explicit:

- `status`
- `blocker_codes`
- `actionability_score`
- `target_agent_uid`

## Repo-aware coordination

ACOP should support code-work scoping directly.

Recommended fields:

- `repo_id`
- `workspace_root`
- `branch_name`
- `worktree_id`
- `base_commit`
- `expected_head_commit`
- `owned_paths`
- `owned_symbols`

These are important for avoiding collisions during cross-agent code generation.

## Review-aware coordination

Code work is not done when edits exist; it is done when the required validation and review state is
satisfied.

Recommended fields:

- `review_required`
- `review_scope`
- `review_artifact_ids`
- `validation_requirements`
- `completion_evidence`

## Minimal message families

ACOP should at least define these semantic message families:

- `work_offer`
- `work_update`
- `claim_intent`
- `claim_grant`
- `claim_release`
- `blocker_update`
- `artifact_update`
- `validation_update`
- `completion_notice`
- `supersession_notice`

These can travel over A2A messages or be represented in indexed artifacts/resources.

## Relationship to A2A

A2A can carry:

- delegation
- messages
- task lifecycle exchange

ACOP adds code-work semantics that A2A does not define strongly enough, such as:

- repo/worktree ownership hints
- patch and artifact identity
- claimability
- blocker taxonomy for code workflows
- validation and review completion requirements

So ACOP should be viewed as a domain-specific contract layered over A2A, not as a rival standard.

## Relationship to MCP

MCP can expose:

- tools
- resources
- handoff artifacts
- plan artifacts
- graph queries

ACOP should use MCP for:

- resource reads
- artifact lookup
- handoff discovery
- validation/report retrieval

But MCP does not define code-work coordination semantics by itself.

## Initial practical scope

ACOP v0.1 should prioritize:

- stable work identity
- explicit status and blockers
- artifact and operation identity
- actionability
- target agent and participant agent fields
- claim / lease hooks
- repo/branch/worktree scoping

It should avoid overreaching into:

- global scheduling policy
- model routing policy
- transport lock-in

## Core schema

The initial machine-readable core contract lives at:

- [acop.schema.json](./acop.schema.json)
- [acop_examples.md](./acop_examples.md)
- [acop-orchestration.md](./acop-orchestration.md)
- [acop-orchestration.schema.json](./acop-orchestration.schema.json)

That schema is intentionally narrow. It covers:

- `WorkItem`
- `Blocker`
- `Operation`
- `Artifact`
- `ValidationRequirement`
- `BlackboardEntry`
- `ClaimIntent`
- `Claim`
- `LeaseHeartbeat`

It does not yet attempt to encode every possible orchestration policy.

Reusable orchestration flow semantics such as:

- stages
- lanes
- gates
- release conditions
- acceptance state

now live in the orchestration extension rather than in ACOP core.

## Producer alignment

Producers (planners, refactor engines, decomposition services) align with ACOP by:

- keeping handoff envelopes strongly typed
- keeping blocker codes explicit
- keeping artifact and operation IDs stable
- publishing readiness and actionability
- not owning lease truth

## Compliance extension hook

ACOP core should remain useful without compliance-heavy deployment assumptions.

Compliance-specific coordination belongs in an extension/profile, not in a separate sibling
protocol and not in the minimal core.

Current direction:

- core ACOP includes blackboard coordination and validation-aware work semantics
- compliance-heavy environments can apply the ACOP compliance profile for:
  - formalized requirement sets
  - completeness matrices
  - evidence obligations
  - exception justifications
  - policy and approval gates

See:

- [acop-compliance.md](./acop-compliance.md)

## Read-side MCP alignment

The MCP read surface should:

- index ACOP-compatible work items
- expose ACOP-relevant query surfaces
- remain query/transport oriented
- not become the hidden coordination owner

## Open questions

- how much of claim / lease state should be queryable via MCP versus only through orchestration
  middleware APIs?
- should ACOP define standard blocker-code namespaces?
- should ACOP require review state as first-class protocol objects or allow them to remain artifact
  references in v0.1?
- should `actionability_score` be producer-supplied, middleware-supplied, or both?
