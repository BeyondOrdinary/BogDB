# ACOP Compliance Profile v0.1 Draft

## Purpose

This document defines a compliance-oriented extension profile for ACOP.

The goal is to let multi-agent code work operate under formalized requirements without forcing
every ACOP deployment to become compliance-heavy.

This profile is designed around the same style already used in this style:

- formalized requirements
- a completeness matrix
- evidence-backed status
- explicit justifications for exceptions

## Scope

The ACOP compliance profile extends ACOP core with:

- requirement catalogs
- control mappings
- completeness tracking
- evidence references
- exception / waiver justifications
- approval and policy gates

It does not replace ACOP core work-item semantics.

This profile should be treated as an extension layered on top of:

- [acop.md](./acop.md)
- [acop.schema.json](./acop.schema.json)
- [acop-compliance.schema.json](./acop-compliance.schema.json)
- [acop_examples.md](./acop_examples.md)

It is not a separate orchestration protocol.

## Core principle

Compliance should be machine-readable and reviewable.

That means:

- no hidden policy assumptions
- no vague “looks compliant” states
- every required control should map to evidence or an explicit exception

## Extension concepts

### `Requirement`

A formal requirement that applies to one or more coordinated work items.

Required fields:

- `requirement_id`
- `name`
- `category`
- `description`
- `applies_to_work_kinds`

Recommended fields:

- `framework`
- `control_family`
- `severity`
- `owner_role`
- `required_evidence_kinds`

Examples:

- code review required
- security scan required
- separation of duty required
- traceable validation evidence required
- architectural approval required

### `RequirementMatrixEntry`

Represents one requirement applied to one work item or work scope.

Required fields:

- `matrix_entry_id`
- `work_item_id`
- `requirement_id`
- `completeness_status`

Recommended fields:

- `evidence_ids`
- `exception_id`
- `assessed_at_utc`
- `assessed_by_agent_uid`
- `notes`

### `CompletenessStatus`

Recommended baseline enum:

- `not_started`
- `partial`
- `satisfied`
- `blocked`
- `excepted`
- `not_applicable`

Meaning:

- `not_started`: no evidence or exception yet
- `partial`: some evidence exists, but requirement is incomplete
- `satisfied`: evidence supports completion
- `blocked`: requirement cannot complete due to an unresolved blocker
- `excepted`: not satisfied normally, but an explicit approved exception exists
- `not_applicable`: requirement does not apply to this work item

### `EvidenceRecord`

Represents evidence supporting a requirement or matrix entry.

Required fields:

- `evidence_id`
- `evidence_kind`
- `summary`
- `created_at_utc`

Recommended fields:

- `artifact_ids`
- `resource_uri`
- `producer`
- `source_work_item_id`
- `validation_result`
- `review_result`

Examples:

- test run result
- review artifact
- build output
- static analysis report
- approval note
- traceability report

### `ExceptionJustification`

Represents a formal exception to a requirement.

Required fields:

- `exception_id`
- `requirement_id`
- `scope`
- `justification`
- `approved_by_role`

Recommended fields:

- `approved_by_agent_uid`
- `approved_at_utc`
- `risk_acceptance_summary`
- `mitigations`
- `expires_at_utc`

This is the key place for “justifications for exceptions to the requirements.”

### `PolicyGate`

Represents a gate that must pass before work may transition states.

Required fields:

- `policy_gate_id`
- `name`
- `applies_to_transition`
- `gate_status`

Recommended fields:

- `blocking_requirement_ids`
- `required_approval_roles`
- `failure_summary`

Example transitions:

- `ready -> claimed`
- `in_progress -> completed`
- `completed -> consumed`

## Compliance matrix model

The central view of this profile is the compliance controls matrix.

At minimum it should answer:

- what requirements apply?
- what evidence exists?
- what is complete?
- what is still partial or blocked?
- what is excepted and why?

Recommended matrix columns:

- `work_item_id`
- `requirement_id`
- `requirement_name`
- `category`
- `completeness_status`
- `evidence_count`
- `exception_id`
- `blocker_codes`
- `last_assessed_at_utc`

## Minimal lifecycle

1. apply requirements to work
2. collect evidence as work proceeds
3. update matrix completeness
4. record exceptions where necessary
5. enforce policy gates before key transitions
6. retain the matrix plus evidence chain as audit-friendly output

## Relationship to ACOP core

ACOP core remains responsible for:

- work identity
- blockers
- claims/leases
- artifacts
- validation hooks
- blackboard coordination

The compliance profile adds:

- formal requirement sets
- completeness accounting
- evidence obligations
- exception justifications
- gate enforcement semantics

Extension rule:

- ACOP compliance should reuse core ACOP identities and lifecycle semantics
- compliance-specific objects should attach to ACOP work items rather than redefining them
- future compliance schemas should be framed as profile/extension schemas over ACOP core, not as
  a disconnected sibling protocol

The initial machine-readable compliance profile now lives at:

- [acop-compliance.schema.json](./acop-compliance.schema.json)

## Blackboard and compliance

Blackboard entries can contribute to compliance, but they are not compliance truth on their own.

Examples:

- a blackboard `finding` may inform a risk requirement
- a blackboard `decision` may point to an approval artifact
- a blackboard `partial_result` may contribute evidence

But compliance state should still live in:

- requirement matrix entries
- evidence records
- exception justifications
- policy gates

This keeps informal collaboration and formal compliance from collapsing into one object.

## Design rules

- every required control should map to evidence, an explicit block, or an explicit exception
- exceptions must be justified, not silently treated as satisfied
- compliance state should be queryable and reviewable by both humans and agents
- the profile should remain transport-neutral and compatible with A2A/MCP layering

## Producer alignment

A producer should eventually be able to emit ACOP-compliance-compatible evidence for:

- validation plans and results
- review artifacts
- blocker codes
- structured justifications for deferred or exceptional paths

A producer should not become the compliance authority by itself.

## Open questions

- should requirement catalogs be globally versioned or workspace-local?
- should approval roles remain symbolic in v0.1 or require identity binding?
- should exception expiration be mandatory for some requirement categories?
