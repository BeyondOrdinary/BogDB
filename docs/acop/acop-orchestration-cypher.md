# ACOP Orchestration Cypher Query Pack

## Purpose

This document provides a first reusable Cypher query pack for ACOP orchestration state projected
into the graph projection layer.

The goal is not to define every future query shape. The goal is to establish the minimum practical
queries an orchestration middleware, MCP surface, or lead agent would immediately want:

- lane acceptance status
- pending gates
- unreleased downstream stages
- blocked work tied to orchestration state
- release readiness

This assumes the orchestration projection described in:

- [acop-orchestration.md](./acop-orchestration.md)

## Conventions

This pack assumes the following node and edge families exist:

- `OrchestrationFlow`
- `OrchestrationStage`
- `OrchestrationLane`
- `OrchestrationGate`
- `AcceptanceRecord`
- `WorkItem`

and:

- `HAS_STAGE`
- `HAS_LANE`
- `HAS_GATE`
- `FLOWS_TO`
- `RELEASES`
- `ACCEPTS`
- `IMPLEMENTS_FLOW_UNIT`

Property names are illustrative. Adapt them to the final persisted graph contract.

## 1. Lane completion without acceptance

Question:

- which lanes are complete from a work-item perspective but not yet accepted?

```cypher
MATCH (lane:OrchestrationLane)
MATCH (work:WorkItem)-[:IMPLEMENTS_FLOW_UNIT]->(lane)
WHERE work.status = 'completed'
OPTIONAL MATCH (accept:AcceptanceRecord)-[:ACCEPTS]->(lane)
WITH lane, collect(accept.acceptance_status) AS acceptance_states
WHERE size(acceptance_states) = 0
   OR all(state IN acceptance_states WHERE state <> 'accepted' AND state <> 'released')
RETURN
  lane.lane_id AS lane_id,
  lane.title AS lane_title,
  acceptance_states
ORDER BY lane_id;
```

## 2. Pending gates

Question:

- which gates are still waiting on acceptance?

```cypher
MATCH (gate:OrchestrationGate)
OPTIONAL MATCH (accept:AcceptanceRecord)-[:ACCEPTS]->(gate)
WITH gate, collect(accept.acceptance_status) AS acceptance_states
WHERE size(acceptance_states) = 0
   OR all(state IN acceptance_states WHERE state <> 'accepted' AND state <> 'released')
RETURN
  gate.gate_id AS gate_id,
  gate.title AS gate_title,
  gate.gate_kind AS gate_kind,
  acceptance_states
ORDER BY gate_id;
```

## 3. Unreleased downstream stages

Question:

- which stages remain unreleased because upstream gates have not been accepted?

```cypher
MATCH (gate:OrchestrationGate)-[:RELEASES]->(stage:OrchestrationStage)
OPTIONAL MATCH (accept:AcceptanceRecord)-[:ACCEPTS]->(gate)
WITH gate, stage, collect(accept.acceptance_status) AS acceptance_states
WHERE size(acceptance_states) = 0
   OR all(state IN acceptance_states WHERE state <> 'accepted' AND state <> 'released')
RETURN
  stage.stage_id AS stage_id,
  stage.title AS stage_title,
  gate.gate_id AS blocking_gate_id,
  gate.title AS blocking_gate_title
ORDER BY stage_id, blocking_gate_id;
```

## 4. Gates waiting on specific lanes

Question:

- which gates are not yet releasable because one or more upstream lanes are not accepted?

```cypher
MATCH (lane:OrchestrationLane)-[:FLOWS_TO]->(gate:OrchestrationGate)
OPTIONAL MATCH (accept:AcceptanceRecord)-[:ACCEPTS]->(lane)
WITH lane, gate, collect(accept.acceptance_status) AS acceptance_states
WHERE size(acceptance_states) = 0
   OR all(state IN acceptance_states WHERE state <> 'accepted' AND state <> 'released')
RETURN
  gate.gate_id AS gate_id,
  gate.title AS gate_title,
  lane.lane_id AS blocking_lane_id,
  lane.title AS blocking_lane_title
ORDER BY gate_id, blocking_lane_id;
```

## 5. Work items blocked by orchestration state

Question:

- which work items are blocked because an upstream gate or release condition is still pending?

This query assumes blocked work items record orchestration blocker codes.

```cypher
MATCH (work:WorkItem)
WHERE work.status = 'blocked'
  AND work.blocker_code IN [
    'upstream_gate_pending',
    'upstream_stage_unreleased',
    'acceptance_pending'
  ]
RETURN
  work.work_item_id AS work_item_id,
  work.title AS title,
  work.blocker_code AS blocker_code,
  work.summary AS summary
ORDER BY work_item_id;
```

## 6. Release-ready gates

Question:

- which gates have all upstream lanes accepted and are ready for owner action?

```cypher
MATCH (gate:OrchestrationGate)
OPTIONAL MATCH (lane:OrchestrationLane)-[:FLOWS_TO]->(gate)
OPTIONAL MATCH (accept:AcceptanceRecord)-[:ACCEPTS]->(lane)
WITH
  gate,
  collect(DISTINCT lane.lane_id) AS upstream_lane_ids,
  collect(DISTINCT CASE
    WHEN accept.acceptance_status IN ['accepted', 'released'] THEN lane.lane_id
    ELSE null
  END) AS accepted_lane_ids
WITH
  gate,
  [id IN upstream_lane_ids WHERE id IS NOT NULL] AS upstream_lane_ids,
  [id IN accepted_lane_ids WHERE id IS NOT NULL] AS accepted_lane_ids
WHERE size(upstream_lane_ids) > 0
  AND size(upstream_lane_ids) = size(accepted_lane_ids)
RETURN
  gate.gate_id AS gate_id,
  gate.title AS gate_title,
  gate.gate_kind AS gate_kind
ORDER BY gate_id;
```

## 7. Flow summary dashboard

Question:

- what is the current orchestration state of one flow at a glance?

```cypher
MATCH (flow:OrchestrationFlow {flow_id: $flow_id})
OPTIONAL MATCH (flow)-[:HAS_STAGE]->(stage:OrchestrationStage)
OPTIONAL MATCH (stage)-[:HAS_LANE]->(lane:OrchestrationLane)
OPTIONAL MATCH (stage)-[:HAS_GATE]->(gate:OrchestrationGate)
OPTIONAL MATCH (accept:AcceptanceRecord)-[:ACCEPTS]->(lane)
WITH
  flow,
  count(DISTINCT stage) AS stage_count,
  count(DISTINCT lane) AS lane_count,
  count(DISTINCT gate) AS gate_count,
  count(DISTINCT CASE WHEN accept.acceptance_status IN ['accepted', 'released'] THEN lane END) AS accepted_lane_count
RETURN
  flow.flow_id AS flow_id,
  flow.title AS title,
  stage_count,
  lane_count,
  gate_count,
  accepted_lane_count;
```

## Why acceptance matters

The crucial distinction in orchestration graphs is:

- `completed` work item status is necessary
- `accepted` orchestration status is what safely releases downstream work

If acceptance state is not written back into the graph projection layer, the graph cannot reliably answer the queries
above, and agents will end up reconstructing orchestration truth from ad hoc notes or raw work-item
completion events.
