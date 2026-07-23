# BogDB

BogDB is the embeddable graph database for .NET and AI-native applications: a permissive C# (.NET 9) core, MCP-first agent integration, and enterprise security available when teams need it. Schema, queries, and bulk loading are accessible from process, and the same engine is reachable over the Model Context Protocol so agents can query, introspect schema, and coordinate handoffs against a live graph.

**Current status:** comprehensive xUnit coverage across 170+ test files in `BogDb.Tests` (parser, planner, optimizer, processor, storage, persistence, end-to-end) plus dedicated suites for each bundled extension and the MCP servers.

---

## Quick Start

```csharp
using BogDb.Core.Main;

// In-memory graph — no files needed
using var db   = BogDatabase.CreateInMemory();
using var conn = new BogConnection(db);

conn.Query("CREATE NODE TABLE Person (id INT64, name STRING, age INT64, PRIMARY KEY(id))");
conn.Query("CREATE REL TABLE KNOWS (FROM Person TO Person, since INT64)");

conn.BeginWriteTransaction();
conn.Query("CREATE (p:Person {id: 1, name: 'Alice', age: 30})");
conn.Query("CREATE (p:Person {id: 2, name: 'Bob',   age: 25})");
conn.Query("CREATE (:Person {id:1})-[:KNOWS {since: 2020}]->(:Person {id:2})");
conn.Commit();

// Basic pattern match
var r = conn.Query("MATCH (a:Person)-[:KNOWS]->(b:Person) RETURN a.name, b.name, a.age");
while (r.HasNext())
{
    var row = r.GetNext();
    Console.WriteLine($"{row.GetString(0)} knows {row.GetString(1)} (age {row.GetLong(2)})");
}

// Open a persistent database from disk instead
using var disk = BogDatabase.Open("graph.bog");
```

A fluent pipeline is also available for composing queries from C#:

```csharp
using BogDb.Core.Main;

var rows = conn.BeginPipeline()
    .Match("(p:Person)")
    .Where("p.age > 25")
    .Return("p.name", "p.age")
    .OrderBy("p.age DESC")
    .Limit(10)
    .Execute();
```

---

## Highlighted Features

### Aggregation + GROUP BY + HAVING

```csharp
// Implicit GROUP BY via WITH clause — no GROUP BY keyword needed
conn.Query(@"
    MATCH (p:Person)
    WITH p.dept AS dept, count(p) AS cnt
    WHERE cnt > 1
    RETURN dept, cnt
    ORDER BY cnt DESC");

// Explicit ORDER BY on aggregate
conn.Query("MATCH (p:Person) RETURN p.country, count(p) AS total ORDER BY total DESC LIMIT 5");
```

### Top-K Optimizer (automatic ORDER BY + LIMIT fusion)

Queries of the form `ORDER BY … LIMIT N` are automatically fused into a single O(n·log K) max-heap pass by `TopKOptimizerRule` — no full sort required:

```csharp
// This query fuses internally into PhysicalTopK (heap, not sort)
var top3 = conn.Query(
    "MATCH (p:Person) RETURN p.name, p.score ORDER BY p.score DESC LIMIT 3");
```

### Secondary Indexes (non-primary-key)

Index any node property so equality/prefix predicates plan as an `INDEX_SCAN` instead of a full `SCAN_NODE_PROPERTY`. Indexes are disk-backed for file databases and restored on reopen. `create_index` is idempotent — safe to call in re-runnable schema setup:

```csharp
conn.Query("CALL create_index('Person', 'country')");  // columns: table, property, status ('created' | 'exists')

// The predicate is now index-served; ORDER BY / SKIP / LIMIT paginate on top:
conn.Query(@"
    MATCH (p:Person) WHERE p.country = 'US'
    RETURN p.name ORDER BY p.age DESC SKIP 20 LIMIT 10");

// Confirm the plan uses the index:
conn.Query("EXPLAIN MATCH (p:Person) WHERE p.country = 'US' RETURN p.name");  // → INDEX_SCAN
```

The planner picks index scans by estimated fan-out and handles multi-property predicates (union/intersect across per-property indexes).

### Recursive Path Traversal

```csharp
// Variable-length path (1 to 3 hops)
var paths = conn.Query(@"
    MATCH (a:Person)-[r*1..3]->(b:Person)
    WHERE a.name = 'Alice'
    RETURN a.name, length(r), nodes(r), rels(r)");

// nodes(r) returns the list of intermediate node IDs along each path
// rels(r)  returns the list of edge objects traversed
```

`LogicalRecursiveExtend` cooperates with `LimitPushDownRule` so `LIMIT` counts are propagated into the BFS frontier as an early-stop bound.

**Per-hop filtering.** A comprehension filter prunes edges *during* traversal, so only matching hops extend the path — enabling, e.g., native temporal multi-hop traversal ("follow only edges valid at `$t`"):

```csharp
conn.Query(@"
    MATCH (a:Account {id: 1})-[r:TRANSFER*1..4
        (rr, nn | WHERE rr.valid_from <= $t AND (rr.valid_to IS NULL OR rr.valid_to > $t))
    ]->(b:Account)
    RETURN b.id, length(r)");
```

`rr` binds each intermediate edge and `nn` each intermediate node; the `WHERE` is evaluated per hop to prune — a path through a failing edge (or onto a failing node) does not continue. Works with `->`/`<-`/`-`(both), multi-type `:A|B`, and stays compatible with `LIMIT` early-stop.

### Temporal (valid-time) edges

BogDB has no dedicated temporal feature — but valid-time modeling is a first-class pattern over ordinary edge properties. Store `valid_from`/`valid_to` as **INT64 epoch** on a relationship, leave `valid_to` unset for open-ended intervals, and filter with a half-open `WHERE` (inclusive lower, exclusive upper, `NULL` = open):

```csharp
// "What was true at instant T?"  — the 'current' flag is independent of the as-of instant.
conn.Query(@"
    MATCH (e:Entity {id: $id})-[r:FACT]->(o:Entity)
    WHERE r.valid_from <= $t AND (r.valid_to IS NULL OR r.valid_to > $t)
    RETURN o.id, (r.valid_to IS NULL) AS current");

// Invalidate: SET r.valid_to = $now.
// Supersede: close the old interval + open a new one in one transaction (MVCC-atomic, rollback-safe).
```

Use INT64 epoch (not ISO strings) so comparisons stay numeric and type-consistent, and always keep the `valid_to IS NULL` guard — BogDB evaluates a comparison against `NULL` as false, so dropping the guard silently excludes still-open facts. See `TemporalKnowledgeGraphTests` for a full worked example (as-of, invalidate, supersede, timeline, both-directions). For **multi-hop** temporal traversal ("what was reachable at `$t`?"), push the same validity filter into the traversal itself with a per-hop comprehension — see [Recursive Path Traversal](#recursive-path-traversal).

### Scalar Functions — Broad Coverage

24+ function categories ship in `BogDb.Core` — math, string, list, date, interval, timestamp, struct, union, map, array, blob, UUID, internal id, casting, arithmetic, pattern, path, and more:

```csharp
// Math
conn.Query("RETURN factorial(6), gcd(12, 18), lcm(4, 6)");

// Path / schema functions
conn.Query("MATCH (n:Person) RETURN id(n), label(n)");
conn.Query("MATCH (a)-[r]->(b) RETURN start_node(r), end_node(r), type(r)");

// Timestamp / epoch
conn.Query("RETURN to_epoch_ms('2024-01-01 00:00:00')");
conn.Query("RETURN timestamp_year('2026-03-20 12:00:00')");
```

### Window Functions (OVER / PARTITION BY / ORDER BY)

```csharp
// ROW_NUMBER with global ordering
var r = conn.Query(@"
    MATCH (e:Employee)
    RETURN e.dept, e.name, e.salary,
           ROW_NUMBER() OVER (PARTITION BY e.dept ORDER BY e.salary DESC) AS rn
    ORDER BY e.dept, rn");

// Ranking with ties
conn.Query("MATCH (e:Employee) RETURN e.name, RANK() OVER (ORDER BY e.salary DESC) AS rnk");

// Sliding window aggregates
conn.Query(@"
    MATCH (e:Employee)
    RETURN e.dept, e.name,
           AVG(e.salary) OVER (PARTITION BY e.dept) AS dept_avg,
           SUM(e.salary) OVER (PARTITION BY e.dept) AS dept_total");
```

Window evaluation is handled by `WindowFunctionService` and `WindowEvaluator`, with full frame clause support (`ROWS/RANGE BETWEEN …`) and the standard ranking, navigation, and aggregate-over variants.

### Graph Data Science (GDS)

Run graph algorithms via the `CALL algo() YIELD *` syntax, or invoke them directly from C#. Algorithms live in `BogDb.Core/GraphDataScience/`:

```csharp
// PageRank — iterative rank propagation
var r = conn.Query("CALL pagerank() YIELD *");
while (r.HasNext())
{
    var row = r.GetNext();
    Console.WriteLine($"node {row.GetString(0)} → rank {row.GetDouble(1):F6}");
}

// Weakly Connected Components
conn.Query("CALL wcc() YIELD *");  // columns: node, component_id

// Single-Source Shortest Paths
conn.Query("CALL sssp('0') YIELD *");  // columns: node, distance
```

| Algorithm | CALL name | Key parameters | Output columns |
|---|---|---|---|
| PageRank | `pagerank` | `maxIterations`, `dampingFactor` | `node`, `rank` |
| Weakly Connected Components | `wcc` | — | `node`, `component_id` |
| Single-Source Shortest Paths | `sssp` | source node offset | `node`, `distance` |

### Sequences

```csharp
conn.Query("RETURN nextval('order_seq')");  // → 1
conn.Query("RETURN nextval('order_seq')");  // → 2
conn.Query("RETURN currval('order_seq')");  // → 2
```

### Bulk Load (COPY FROM)

```csharp
// CSV → node table (schema-typed: INT64/INT32/STRING/DOUBLE/FLOAT/BOOL)
conn.Query("COPY Person FROM 'people.csv'");

// CSV → relationship table
conn.Query("COPY KNOWS FROM 'friendships.csv'");
```

Bulk load is driven by `LogicalCopyFrom` → `PhysicalCopyFrom` with full transactional integration.

### Extensions

Custom data-source extensions implement `ITableFunction` and are registered at runtime through `IExtension` / `IStorageExtension`. Bundled extensions:

| Extension | Purpose |
|---|---|
| `BogDb.Extensions.Json` | `LOAD FROM 'file.json'` table-scan support |
| `BogDb.Extensions.Vector` | Vector similarity, embeddings, and HNSW ANN indexes |
| `BogDb.Extensions.FTS` | Full-text search indexes |
| `BogDb.Extensions.Algo` | Additional graph algorithms |
| `BogDb.Extensions.HttpFS` | Read remote files over HTTP(S) |
| `BogDb.Extensions.Postgres` | Scan/copy from PostgreSQL |
| `BogDb.Extensions.DuckDB` | DuckDB interop |
| `BogDb.Extensions.SQLite` | SQLite interop |
| `BogDb.Extensions.LLM` | LLM-backed scalar/table functions |
| `BogDb.Extensions.Demo` | Reference implementation for custom extensions |

```csharp
// Implement
public class MyScanFn : ITableFunction
{
    public string Name => "scan_mydb";
    public IEnumerable<Dictionary<string, object?>> Invoke(IReadOnlyList<object?> args)
    {
        foreach (var record in OpenMySource((string)args[0]!))
            yield return record;
    }
}

// Register and query
new MyExtension().Load(db);
conn.Query("CALL scan_mydb('source.bin') RETURN *");
```

### Vector / HNSW indexes

`BogDb.Extensions.Vector` provides approximate nearest-neighbor (ANN) search over `FLOAT[]` embedding properties, backed by an HNSW graph, alongside the vector distance scalar functions (`vector_cosine_similarity`, `array_distance`, …).

```csharp
new VectorExtension().Load(db);

conn.Query("CREATE NODE TABLE Document(id STRING, embedding FLOAT[])");
conn.Query("CREATE (:Document {id:'a', embedding:[1.0, 0.0]})");

// Build an index (metric: cosine | l2 | dotproduct)
conn.Query("CALL create_vector_index('Document', 'doc_vec', 'embedding', 'cosine') RETURN *");

// k-NN query → columns: rank, id, distance, embedding
conn.Query("CALL query_vector_index('Document', 'doc_vec', [1.0, 0.0], 5) RETURN *");
```

| Function | Purpose |
|---|---|
| `create_vector_index(table, index, property [, metric])` | Build an HNSW index (options: `metric`, `mu`, `ml`, `pu`, `alpha`, `efc`) |
| `query_vector_index(table, index, vector, k [, metric])` | k-NN search (option: `efs`) |
| `rebuild_vector_index(table [, index])` | Rebuild the graph from current rows after a write batch |
| `drop_vector_index(table, index)` | Remove an index |
| `show_vector_indexes([table])` / `show_indexes()` | List registered indexes |

**Distance semantics.** `query_vector_index` reports the same `distance` whether it serves a result from the HNSW fast path or the exact fallback: `cosine` → `1 − cosine_similarity`, `l2` → Euclidean distance, `dotproduct` → negated inner product. Lower is nearer.

**Index lifecycle (incrementally maintained + persisted).** The HNSW graph is built by `create_vector_index`, maintained **incrementally at commit** (inserts/updates/deletes — an update tombstones the old vector and inserts the new; rollbacks are discarded), and **persisted to disk at checkpoint** so a reopen *restores* the graph rather than rebuilding it. On open the persisted graph is loaded only if its row-count stamp still matches the table, otherwise it is rebuilt from current rows. A staleness fingerprint makes `query_vector_index` fall back to an exact scan if a write ever bypasses maintenance, and `rebuild_vector_index` rebuilds from current rows (compacting tombstones) — so results stay correct either way.

### Full-text search (FTS)

`BogDb.Extensions.FTS` provides BM25-ranked full-text search over text properties, with an inverted index, phrase queries (`"exact phrase"`), stemming, and stop-word filtering.

```csharp
new FtsExtension().Load(db);

conn.Query("CREATE NODE TABLE Article(id INT64 PRIMARY KEY, title STRING, body STRING)");
// … insert rows …

// Build an index over one or more text properties (BM25 k1/b are tunable)
conn.Query("CALL CREATE_FTS_INDEX('Article', 'art_idx', ['title', 'body'], k1 := 1.5, b := 0.75) RETURN *");

// Search → columns: node_offset, score
conn.Query("CALL QUERY_FTS_INDEX('art_idx', 'graph database', 10) RETURN *");
```

| Function | Purpose |
|---|---|
| `CREATE_FTS_INDEX(table, index, [props] [, k1 := , b := ])` | Build a BM25 inverted index (defaults `k1 = 1.2`, `b = 0.75`) |
| `QUERY_FTS_INDEX(index, query [, top_k] [, mode])` | Ranked search; `mode := 'conjunctive'` requires all terms |
| `REBUILD_FTS_INDEX([index])` | Rebuild one index (or all) from current rows after a write batch |
| `DROP_FTS_INDEX(index)` | Remove an index |

**Index lifecycle.** Like vectors, FTS indexes are maintained **incrementally at commit** (inserts/updates/deletes, discarded on rollback) and **persisted to disk at checkpoint**, so a reopen restores the inverted index (validated by a row-count stamp) rather than rebuilding it. If a write bypasses maintenance, the next query transparently rebuilds — or call `REBUILD_FTS_INDEX` to control when that cost is paid.

### MCP-First Agent Integration

BogDB ships three first-class MCP servers so agents can talk to a live graph without bespoke glue:

- **`BogDb.Mcp.Server`** — read-only query surface (`bogdb_query`), schema introspection (`bogdb_schema`, `bogdb_tables`, `bogdb_table_info`), and handoff resource coordination. `ReadOnlyQueryGuard` enforces read-only semantics at the boundary.
- **`BogDb.Acop.Mcp.Server`** — the ACOP (Agentic Code Orchestration Protocol) layer for multi-agent handoff coordination, acceptance verification, and durable ingest receipts. See [`docs/acop/`](docs/acop/) for the v1.0 protocol, schemas, and fixtures.
- **`BogDb.Mcp.Codegen.Server`** — code generation tools backed by the graph.

The handoff index contract carries `protocol_version`, `artifact_id`, `handoff_kind`, `target_agent_uid`, `blocker_codes`, and `actionability_score` — designed so orchestrators can filter, batch, and prioritize agent work against a graph-backed source of truth.

---

## Architecture

```
Query string
  → ANTLR4 parse     (BogDb.Core/Parser)          CypherLexer, CypherParser, Transformer
  → Bind             (BogDb.Core/Binder)          BoundStatement, BoundRegularQuery, …
  → Plan             (BogDb.Core/Planner)         LogicalPlan / LogicalOperator tree
  → Optimize         (BogDb.Core/Optimizer)       12 rules incl. TopK, FilterPushDown, LimitPushDown
  → Map              (BogDb.Core/Processor/PlanMapper)
  → Execute          (BogDb.Core/Processor/Operator/*)
  → QueryResult
```

### Optimizer rules

| Rule | Effect |
|---|---|
| `FilterPushDownRule` | Moves predicates as close to their scan as possible |
| `ProjectionPushDownRule` | Eliminates unused column projections |
| `LimitPushDownRule` | Propagates LIMIT count into `RecursiveExtend` early-stop bound |
| `TopKOptimizerRule` | Fuses `ORDER BY … LIMIT N` → O(n·log K) heap |
| `RemoveUnnecessaryJoinRule` | Prunes trivial property-less scan nodes from hash-join sides |
| `AggKeyDependencyRule` | Partitions GROUP BY keys into primary vs. dependent sets |
| `AccHashJoinRule` | Inserts `LogicalAccumulate` before hash-join build side (pipeline breaker) |
| `SchemaPopulatorRule` | Bottom-up `ComputeSchema()` pass over the logical plan |
| `CorrelatedSubqueryUnnestRule` | Correlated-subquery rewrite hook |
| `FactorizationRewriterRule` | Deliberate no-op (flat-row architecture) |
| `RemoveFactorizationRewriterRule` | Inverse no-op pair |

### Physical pipeline operators

`PhysicalOperatorType` enumerates 77 operator kinds. Frequently-used ones:

| Operator | Notes |
|---|---|
| `PhysicalScanFrontier`, `PhysicalIndexScanNode`, `PhysicalExternalIndexScanNode` | Node-table scans and index-driven probes |
| `PhysicalExpressionsScan`, `PhysicalUnwind`, `SingleRowPhysicalOperator` | Constant / row-source operators |
| `PhysicalFilter` | Predicate evaluation |
| `PhysicalProjection` | RETURN / WITH column projection |
| `PhysicalAggregate`, `PhysicalDistinct` | GROUP BY + COUNT/SUM/AVG/MIN/MAX, DISTINCT |
| `PhysicalTopK` | ORDER BY + LIMIT (O(n·log K) max-heap) |
| `PhysicalAccumulate`, `PhysicalFlatten` | Pipeline materialization / fan-out helpers |
| `PhysicalSemiMasker` | Hash-join pre-filter via semi-masks |
| `PhysicalCallSubquery` | Correlated and non-correlated subqueries |
| `PhysicalTableFunctionCall` | Extension table functions and `CALL` |
| `PhysicalInsert`, `PhysicalMergeNode`, `PhysicalMergeRel`, `PhysicalMergeGraph` | CREATE / MERGE |
| `PhysicalSetAndDelete` | SET and DELETE clauses |
| `PhysicalCopyFrom` | CSV / external batch import |
| `PhysicalUnionAll` | UNION ALL |
| `PhysicalProfile` | Stopwatch passthrough; exposes `__profile_ms` |
| `PhysicalCreateMacro` | User-defined macros |

---

## Samples

End-to-end sample apps live alongside the engine and double as integration coverage:

| Sample | Form |
|---|---|
| [`BogDb.Samples.FraudGraph.Console`](BogDb.Samples.FraudGraph.Console/) | Console — fraud-ring detection over a transaction graph |
| [`BogDb.Samples.TemporalGraph.Console`](BogDb.Samples.TemporalGraph.Console/) | Console — temporal reachability via per-hop filtered traversal |
| [`BogDb.Samples.SocialGraph.Blazor`](BogDb.Samples.SocialGraph.Blazor/) | Blazor — friend/follower exploration |
| [`BogDb.Samples.PackageEcosystem.Blazor`](BogDb.Samples.PackageEcosystem.Blazor/) | Blazor — NPM/NuGet dependency analysis |
| [`BogDb.Samples.SqlToCypher.Blazor`](BogDb.Samples.SqlToCypher.Blazor/) | Blazor — SQL → Cypher translation playground |
| [`BogDb.Samples.MonsterManual.Blazor`](BogDb.Samples.MonsterManual.Blazor/) | Blazor — D&D bestiary modeled as a graph |
| [`BogDb.Samples.NutriGraph.Blazor`](BogDb.Samples.NutriGraph.Blazor/) | Blazor — recipe and nutrition relationships |
| [`BogDb.Samples.TacticalMessaging.Blazor`](BogDb.Samples.TacticalMessaging.Blazor/) | Blazor — tactical messaging scenarios |
| [`BogDb.Samples.LLMBenchmarks`](BogDb.Samples.LLMBenchmarks/) + [`.Blazor`](BogDb.Samples.LLMBenchmarks.Blazor/) | Console + Blazor — LLM-over-graph benchmark harness |

---

## Repository Layout

| Project | Role |
|---|---|
| `BogDb.Core` | Engine — parser, binder, planner, optimizer, processor, storage, GDS |
| `BogDb.Mcp.Server` | MCP server: read-only query, schema, handoff coordination |
| `BogDb.Acop.Mcp.Server` | ACOP orchestration server (multi-agent handoffs, acceptance) |
| `BogDb.Mcp.Codegen.Server` | MCP-exposed code generation tooling |
| `BogDb.Extensions.*` | Bundled extensions (Json, Vector, FTS, Algo, HttpFS, Postgres, DuckDB, SQLite, LLM, Demo) |
| `BogDb.Samples.*` | End-to-end sample apps (Console + Blazor) |
| `BogDb.Tests` | xUnit suite spanning parser, planner, optimizer, processor, storage, persistence, end-to-end |
| `BogDb.Tests.ProcessHost` | Test process host utilities |
| `docs/acop/` | ACOP v1.0 protocol, schemas, examples, compliance fixtures |

---

## What Makes BogDB Different

| Aspect | BogDB |
|---|---|
| Runtime | Pure .NET 9 — no native binaries, no `dlopen`; extensions load via `AssemblyLoadContext` |
| Agent integration | MCP-first: three production MCP servers cover query, codegen, and ACOP orchestration |
| Orchestration protocol | ACOP v1.0 layered on MCP/A2A for handoff coordination, acceptance verification, and ingest receipts |
| Security posture | `ReadOnlyQueryGuard` at the MCP boundary; orchestration acceptance verification persists durable receipts |
| Extensibility | `ITableFunction` + `IExtension` + `IStorageExtension` contracts; 10 bundled extensions including SQL connectors and LLM/vector |
| Storage | C#-native disk-backed storage with WAL and page-level recovery |
| Type system | CLR-native `object?` with `TypeCoercionHelper` (no chunked `ValueVector`) |

---

## Known Gaps

1. **Live SDK wiring for some external connectors:** Postgres, DuckDB, and SQLite extensions are present; some live SDK integrations are still being hardened against production workloads.
2. **C API:** native-style `c_api` parity is intentionally out of scope; the supported public surface is the C# API and the MCP servers.
3. **Feature parity matrix:** ACOP documentation under `docs/acop/` is the canonical contract reference; a unified feature-matrix document is not yet published at the repo root.
4. **Index durability & compaction:** vector (HNSW) and FTS index structures are maintained incrementally, persisted at checkpoint, and restored on open (validated by a row-count stamp; rebuilt on mismatch). Residual limitations: the stamp catches inserts/deletes but not an in-place property update made after the last checkpoint but before a crash (a `rebuild_*` fixes it — the stamp is a fast validity check, not a full content hash); bulk `COPY` applies one incremental update per row; and HNSW deletes are tombstones that only a rebuild compacts.
