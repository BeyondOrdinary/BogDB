# ACOP Orchestration Extension v0.1 Draft

## Purpose

This document defines a reusable orchestration extension for ACOP.

Core ACOP defines work coordination semantics:

- work items
- blockers
- operations
- artifacts
- validation
- blackboard
- claim / lease hooks

The orchestration extension adds a higher-order execution flow that can be loaded by a lead agent
or orchestration middleware and used to control multi-agent development work across phases, lanes,
gates, and acceptance transitions.

This is intended for projects that need stronger flow control than "here is a queue of work items,"
especially when:

- multiple agents operate in parallel
- downstream work must not start early
- integration or reviewer gates release the next tranche of work
- compliance or architectural checkpoints must block advancement

`ExampleProject` is a good representative example, but the schema is intentionally project-agnostic.

## Relationship to ACOP core

The orchestration extension is layered on top of:

- [acop.md](./acop.md)
- [acop.schema.json](./acop.schema.json)

It does not replace ACOP core work items.

Instead:

- orchestration flow defines the allowed execution graph
- orchestration runtime emits or releases ACOP work items
- acceptance state feeds back into the orchestration graph

## Why this exists

In complex multi-agent development, the missing contract is often not "what a work item looks like"
but "what order and gate structure should govern those work items."

Examples:

- phase `2.x` must not start until phase `1.3` is accepted
- three parallel lanes may proceed together, but all must complete before an integration gate opens
- a compliance gate must block release of the next execution tranche
- a schema review must occur before implementation work is marked actionable

These are orchestration semantics, not just work-item semantics.

## Core concepts

### `OrchestrationFlow`

The top-level reusable execution definition.

Required fields:

- `flow_id`
- `protocol_version`
- `orchestration_profile_version`
- `title`
- `target_repo_id`

Recommended fields:

- `summary`
- `version`
- `producer`
- `default_release_policy`

### `Stage`

A major bounded segment of work.

Examples:

- foundation
- schema
- backend
- ui
- integration
- compliance

Required fields:

- `stage_id`
- `title`
- `stage_kind`

### `Lane`

A parallelizable stream of work within a stage.

Examples:

- backend lane
- ui lane
- graph lane
- reviewer lane
- compliance lane

Required fields:

- `lane_id`
- `stage_id`
- `title`

### `Gate`

A stop/check/release checkpoint.

Required fields:

- `gate_id`
- `gate_kind`
- `title`
- `release_policy`

Examples:

- `integration_gate`
- `review_gate`
- `schema_gate`
- `compliance_gate`
- `release_gate`

### `ReleaseCondition`

Declares what must become true before a stage, lane, or gate can release downstream work.

Examples:

- all upstream work items accepted
- required artifacts present
- required validation satisfied
- required compliance matrix entries satisfied
- explicit reviewer approval present

### `AcceptanceRecord`

Represents accepted completion of a unit in the orchestration flow.

This is especially important for graph-backed orchestration because acceptance state should be
queryable independently from raw work-item completion.

Examples:

- lane accepted
- gate accepted
- stage released

### `FlowEdge`

Declares the orchestration dependency graph.

Examples:

- `lane -> gate`
- `gate -> stage`
- `gate -> lane`
- `stage -> stage`

## Recommended execution semantics

### 1. Flow first

Load the orchestration flow before creating or claiming work.

### 2. Release, do not just enqueue

Downstream ACOP work items should become actionable only when their upstream release conditions are
met.

### 3. Acceptance is stronger than completion

Work-item `completed` is not enough for orchestration progress by itself.

The orchestration layer should track:

- completed
- reviewed
- accepted
- released

### 4. Gates are explicit ownership boundaries

Each gate should have an owner role or owner agent type, such as:

- lead agent
- reviewer
- compliance agent
- integration agent

### 5. Blackboard and compliance extend the flow

Blackboard entries and ACOP compliance records should be linkable to:

- stages
- lanes
- gates
- acceptance records

## graph-friendly projection

This extension is designed to project cleanly into the graph projection layer.

Recommended node types:

- `OrchestrationFlow`
- `OrchestrationStage`
- `OrchestrationLane`
- `OrchestrationGate`
- `ReleaseCondition`
- `AcceptanceRecord`

Recommended edges:

- `(:OrchestrationFlow)-[:HAS_STAGE]->(:OrchestrationStage)`
- `(:OrchestrationStage)-[:HAS_LANE]->(:OrchestrationLane)`
- `(:OrchestrationStage)-[:HAS_GATE]->(:OrchestrationGate)`
- `(:OrchestrationLane)-[:FLOWS_TO]->(:OrchestrationGate)`
- `(:OrchestrationGate)-[:RELEASES]->(:OrchestrationStage|:OrchestrationLane)`
- `(:AcceptanceRecord)-[:ACCEPTS]->(:OrchestrationGate|:OrchestrationLane|:OrchestrationStage)`
- `(:WorkItem)-[:IMPLEMENTS_FLOW_UNIT]->(:OrchestrationLane|:OrchestrationGate)`
- `(:ReleaseCondition)-[:GOVERNS]->(:OrchestrationGate|:OrchestrationStage|:OrchestrationLane)`

Most importantly, acceptance status must be relayed into the graph so orchestration queries can ask:

- which lanes are complete but not accepted
- which gates are waiting on missing acceptance
- which downstream stages are unreleased
- which blocked work items are blocked because a gate is still pending

## Example use

For a project like `ExampleProject`, the orchestration flow can express:

- phase-level progression
- parallel parallel implementation, UI, and graph lanes
- reviewer-driven integration gates
- compliance gates before regulated execution phases

But the same schema should also work for:

- refactor programs
- staged migrations
- multi-repo rollout plans
- compliance-heavy implementation projects

## Files

- [acop-orchestration.schema.json](./acop-orchestration.schema.json)
- [acop_examples.md](./acop_examples.md)
- [acop-orchestration-cypher.md](./acop-orchestration-cypher.md)
- [acop-orchestration-mcp.md](./acop-orchestration-mcp.md)
