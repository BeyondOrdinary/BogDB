# ACOP spec polish: path to public v1.0

This file tracks the remaining work to promote ACOP from v0.1 draft to a
defensible public v1.0 specification. Open it after the ASE/CLAiR runtime
gap-closure work is done.

## Required for v1.0

- [ ] **HTTP+JSON transport binding.** ACOP is currently transport-neutral.
  Pick HTTP+JSON as the normative wire format and document request/response
  shapes for every core verb (claim, renew, release, complete, post
  blackboard, create work item, accept handoff). The JSON schemas already
  exist; the binding adds the operation/URL/status code mapping.
- [ ] **RFC 2119 normative language.** Replace "should," "recommended," and
  "examples" with MUST / SHOULD / MAY per clause so conformance is testable.
  Add a "Conformance" section to acop.md.
- [ ] **State-machine diagrams** for `WorkItem.status` transitions and
  `Claim.claim_status` transitions. Document which transitions an
  implementation MUST support, MAY support, and MUST NOT permit.
- [ ] **Error model.** Define a code vocabulary for failure responses
  (e.g. `claim_conflict`, `work_item_superseded`, `lease_expired`,
  `unauthorized_worker`). Include retry guidance per code.
- [ ] **Authentication and authorization sketch.** The spec doesn't need to
  mandate a scheme but MUST state that implementations authenticate workers
  and scope claims to authenticated identity. Recommend (not mandate) OIDC
  bearer tokens for HTTP binding.
- [ ] **Versioning policy.** Document `protocol_version` semantics:
  what's additive, what's breaking, and how implementations negotiate.
- [ ] **Conformance test fixtures.** A set of request/response examples
  per verb, ideally executable against any HTTP binding implementation.
  Lives next to the spec as JSON files.

## Strongly recommended

- [ ] **Sequence diagrams** for the worker happy path (read pending → claim →
  heartbeat → complete) and the handoff path.
- [ ] **Concurrency and consistency notes.** What's the contract when two
  workers race for the same claim? Lease-renewal semantics on contention?
- [ ] **Stability labels per extension.** Mark `acop-orchestration` and
  `acop-compliance` independently from core (e.g. "core: stable," extensions:
  "experimental").
- [ ] **Recommended blocker code namespaces.** The current examples
  (`seam_extraction_required`, etc.) are illustrative; a public spec should
  reserve and document core blocker codes.

## Nice to have

- [ ] A two-page "ACOP at a glance" document for first-time readers.
- [ ] A second backend adapter in `BogDb.Acop.Mcp.Server` to prove the
  protocol is genuinely backend-agnostic (e.g. an in-memory implementation
  for testing).
- [ ] Public hosting of the JSON schemas under the `acop.dev` (or chosen)
  domain so `$id` URLs resolve.

## Out of scope for v1.0

- A full A2A binding (separate spec workstream).
- Scheduler internals — ACOP defines the coordination contract, not the
  scheduler.
- Source-code semantics for any particular programming language.
