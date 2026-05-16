# ACOP Example Payloads

## Purpose

This document gives human-readable sample payloads for ACOP core and the ACOP compliance extension.

The examples use one shared `work_item_id` so it is easy to see how:

- ACOP core describes the coordinated code work
- ACOP compliance extends that same work item with requirements, evidence, and exceptions

## Example 1: Core ACOP work item

```json
{
  "protocol_version": "acop/0.1",
  "work_item": {
    "work_item_id": "work:refactor:resource-hub-http",
    "protocol_version": "acop/0.1",
    "work_kind": "refactor",
    "created_at_utc": "2026-04-17T19:10:00Z",
    "producer": "bo",
    "status": "ready",
    "priority": "high",
    "actionability_score": 0.92,
    "title": "Extract ResourceHub HTTP collaborator",
    "summary": "Split the HTTP seam from ResourceHubService and stabilize the contract boundary.",
    "target_repo_id": "repo:filetransfertool",
    "target_workspace_root": "/abs/FileTransferTool",
    "target_branch": "agent-a/resourcehub-http",
    "target_agent_uid": "worker-http",
    "participant_agent_uids": [
      "lead-01",
      "worker-http",
      "review-01"
    ],
    "owned_paths": [
      "src/FileTransferTool.Infrastructure/Resources/ResourceHubService.cs",
      "src/FileTransferTool.Infrastructure/Resources/Http/ResourceHubHttpService.cs"
    ],
    "owned_symbols": [
      "symbol:ResourceHubService",
      "symbol:ResourceHubHttpService"
    ]
  },
  "blockers": [
    {
      "blocker_code": "review_required",
      "severity": "medium",
      "summary": "Completion requires review acknowledgment before consume."
    }
  ],
  "operations": [
    {
      "operation_id": "op:extract-http-seam",
      "kind": "extract_seam",
      "description": "Materialize the HTTP collaborator and tighten the owner boundary."
    },
    {
      "operation_id": "op:run-targeted-tests",
      "kind": "run_tests",
      "description": "Run the HTTP and ResourceHub targeted test slice.",
      "depends_on_operation_ids": [
        "op:extract-http-seam"
      ],
      "validation_focus": [
        "http behavior parity",
        "resource hub compile/build integrity"
      ]
    }
  ],
  "artifacts": [
    {
      "artifact_id": "artifact:http-contract",
      "artifact_kind": "source_file",
      "artifact_role": "contract",
      "resource_uri": "bo://handoffs/aspect_extract/.bo/handoffs/http-contract.json",
      "repo_relative_path": "src/FileTransferTool.Application/Abstractions/IResourceHubHttpService.cs",
      "readiness": "ready"
    },
    {
      "artifact_id": "artifact:http-impl",
      "artifact_kind": "source_file",
      "artifact_role": "implementation",
      "repo_relative_path": "src/FileTransferTool.Infrastructure/Resources/Http/ResourceHubHttpService.cs",
      "readiness": "ready"
    }
  ],
  "validation_requirements": [
    {
      "requirement_id": "validation:http-targeted-tests",
      "kind": "test_pass",
      "summary": "HTTP seam extraction must pass targeted HTTP and ResourceHub tests."
    }
  ],
  "blackboard_entries": [
    {
      "entry_id": "bb:http-risk-1",
      "work_item_id": "work:refactor:resource-hub-http",
      "entry_kind": "risk",
      "author_agent_uid": "lead-01",
      "created_at_utc": "2026-04-17T19:12:00Z",
      "status": "active",
      "summary": "Watch for shared helper retention in owner after extraction.",
      "confidence": 0.86
    }
  ]
}
```

## Example 2: Claim intent and lease

```json
{
  "protocol_version": "acop/0.1",
  "work_item": {
    "work_item_id": "work:refactor:resource-hub-http",
    "protocol_version": "acop/0.1",
    "work_kind": "refactor",
    "created_at_utc": "2026-04-17T19:10:00Z",
    "producer": "bo",
    "status": "claimed",
    "priority": "high",
    "actionability_score": 0.92
  },
  "claim_intent": {
    "claim_intent_id": "claim-intent:worker-http:001",
    "work_item_id": "work:refactor:resource-hub-http",
    "worker_agent_uid": "worker-http",
    "requested_at_utc": "2026-04-17T19:13:00Z",
    "requested_ttl_seconds": 1800,
    "scope": "owned_paths"
  },
  "claim": {
    "claim_id": "claim:worker-http:001",
    "work_item_id": "work:refactor:resource-hub-http",
    "worker_agent_uid": "worker-http",
    "granted_at_utc": "2026-04-17T19:13:02Z",
    "expires_at_utc": "2026-04-17T19:43:02Z",
    "claim_status": "active"
  }
}
```

## Example 3: ACOP compliance extension for the same work item

```json
{
  "protocol_version": "acop/0.1",
  "compliance_profile_version": "acop-compliance/0.1",
  "work_item_id": "work:refactor:resource-hub-http",
  "requirements": [
    {
      "requirement_id": "req:review-required",
      "name": "Code Review Required",
      "category": "review",
      "description": "Refactor work must receive review before consume.",
      "applies_to_work_kinds": [
        "refactor"
      ],
      "framework": "local-engineering-policy",
      "control_family": "peer-review",
      "severity": "high",
      "required_evidence_kinds": [
        "review_artifact"
      ]
    },
    {
      "requirement_id": "req:targeted-tests",
      "name": "Targeted Test Evidence",
      "category": "validation",
      "description": "Refactor work must retain behavior through targeted tests.",
      "applies_to_work_kinds": [
        "refactor"
      ],
      "framework": "local-engineering-policy",
      "control_family": "validation",
      "severity": "high",
      "required_evidence_kinds": [
        "test_run"
      ]
    }
  ],
  "matrix_entries": [
    {
      "matrix_entry_id": "matrix:review",
      "work_item_id": "work:refactor:resource-hub-http",
      "requirement_id": "req:review-required",
      "completeness_status": "partial",
      "evidence_ids": [],
      "assessed_at_utc": "2026-04-17T19:20:00Z",
      "assessed_by_agent_uid": "lead-01",
      "notes": "Implementation is in progress; review artifact not yet attached."
    },
    {
      "matrix_entry_id": "matrix:tests",
      "work_item_id": "work:refactor:resource-hub-http",
      "requirement_id": "req:targeted-tests",
      "completeness_status": "satisfied",
      "evidence_ids": [
        "evidence:test-run-http"
      ],
      "assessed_at_utc": "2026-04-17T19:33:00Z",
      "assessed_by_agent_uid": "worker-http"
    }
  ],
  "evidence_records": [
    {
      "evidence_id": "evidence:test-run-http",
      "evidence_kind": "test_run",
      "summary": "HTTP and ResourceHub targeted tests passed.",
      "created_at_utc": "2026-04-17T19:32:00Z",
      "artifact_ids": [
        "artifact:http-impl"
      ],
      "producer": "worker-http",
      "source_work_item_id": "work:refactor:resource-hub-http",
      "validation_result": "passed"
    }
  ],
  "exception_justifications": [],
  "policy_gates": [
    {
      "policy_gate_id": "gate:consume",
      "name": "Consume Gate",
      "applies_to_transition": "completed -> consumed",
      "gate_status": "blocked",
      "blocking_requirement_ids": [
        "req:review-required"
      ],
      "failure_summary": "Review evidence still missing."
    }
  ]
}
```

## Example 4: Compliance exception

```json
{
  "protocol_version": "acop/0.1",
  "compliance_profile_version": "acop-compliance/0.1",
  "work_item_id": "work:repair:build-break-fix",
  "exception_justifications": [
    {
      "exception_id": "exception:expedite-review",
      "requirement_id": "req:review-required",
      "scope": "Emergency build-break fix on release branch",
      "justification": "Immediate production unblock required before normal review window.",
      "approved_by_role": "release_manager",
      "approved_by_agent_uid": "lead-release",
      "approved_at_utc": "2026-04-17T20:05:00Z",
      "risk_acceptance_summary": "Low-scope surgical fix with follow-up review required within one business day.",
      "mitigations": [
        "targeted validation run",
        "post-merge review follow-up"
      ],
      "expires_at_utc": "2026-04-18T20:05:00Z"
    }
  ]
}
```

## Example 5: Recommended ACOP + the graph projection layer split

Use `the graph projection layer` for:

- durable work-item history
- blocker and readiness traceability
- artifact / operation identity lookup
- blackboard and evidence history
- compliance matrix and exception indexing
- MCP query surfaces

Do not force `the graph projection layer` to own:

- live claim truth
- lease renewal / expiry authority
- worker arbitration
- retry scheduling
- stale-work cleanup

Those belong to orchestration middleware sitting above the producer and the graph projection layer.

## Example 6: ACOP orchestration flow

```json
{
  "protocol_version": "acop/0.1",
  "orchestration_profile_version": "acop-orchestration/0.1",
  "flow": {
    "flow_id": "flow:example-project:phase-delivery",
    "protocol_version": "acop/0.1",
    "orchestration_profile_version": "acop-orchestration/0.1",
    "title": "ExampleProject phased delivery",
    "summary": "Parallel implementation lanes with explicit integration and compliance release gates.",
    "version": "0.1",
    "producer": "bo",
    "target_repo_id": "repo:example-project",
    "default_release_policy": "all_upstream_accepted"
  },
  "stages": [
    {
      "stage_id": "stage:foundation",
      "title": "Foundation",
      "stage_kind": "foundation",
      "owner_role": "lead_agent"
    },
    {
      "stage_id": "stage:property",
      "title": "Property Intelligence",
      "stage_kind": "backend",
      "owner_role": "lead_agent"
    }
  ],
  "lanes": [
    {
      "lane_id": "lane:foundation:backend",
      "stage_id": "stage:foundation",
      "title": "Backend skeleton",
      "owner_role": "worker_agent",
      "emits_work_kind": "implement"
    },
    {
      "lane_id": "lane:foundation:ui",
      "stage_id": "stage:foundation",
      "title": "UI skeleton",
      "owner_role": "worker_agent",
      "emits_work_kind": "implement"
    },
    {
      "lane_id": "lane:foundation:graph",
      "stage_id": "stage:foundation",
      "title": "Graph schema",
      "owner_role": "worker_agent",
      "emits_work_kind": "implement"
    }
  ],
  "gates": [
    {
      "gate_id": "gate:foundation:integration",
      "stage_id": "stage:foundation",
      "title": "Foundation integration gate",
      "gate_kind": "integration_gate",
      "release_policy": "all_upstream_accepted",
      "owner_role": "integration_agent"
    },
    {
      "gate_id": "gate:property:compliance",
      "stage_id": "stage:property",
      "title": "Property phase compliance gate",
      "gate_kind": "compliance_gate",
      "release_policy": "all_required_compliance_satisfied",
      "owner_role": "compliance_agent",
      "required_requirement_ids": [
        "req:fair-housing-review",
        "req:privacy-data-flow-review"
      ]
    }
  ],
  "flow_edges": [
    {
      "from_id": "lane:foundation:backend",
      "to_id": "gate:foundation:integration",
      "edge_kind": "feeds_gate"
    },
    {
      "from_id": "lane:foundation:ui",
      "to_id": "gate:foundation:integration",
      "edge_kind": "feeds_gate"
    },
    {
      "from_id": "lane:foundation:graph",
      "to_id": "gate:foundation:integration",
      "edge_kind": "feeds_gate"
    },
    {
      "from_id": "gate:foundation:integration",
      "to_id": "stage:property",
      "edge_kind": "releases"
    }
  ],
  "acceptance_records": [
    {
      "acceptance_id": "accept:foundation-backend",
      "target_id": "lane:foundation:backend",
      "acceptance_status": "accepted",
      "recorded_at_utc": "2026-04-18T12:00:00Z",
      "recorded_by_agent_uid": "lead-01",
      "source_work_item_id": "work:foundation:backend"
    }
  ]
}
```
