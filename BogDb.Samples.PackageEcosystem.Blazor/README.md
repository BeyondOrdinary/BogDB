# PackageEcosystem Blazor Sample

A **Blazor Server** application that demonstrates the BogDB graph runtime APIs applied to a realistic package-ecosystem domain.

The application uses **Entity Framework Core (SQLite)** as the authoritative data store and bridges it into a **BogDB in-memory graph** at startup. All analytics — change review, year-bucket histograms, neighborhood projection, and selection-family grouping — run through `GraphShardLocalRuntime` without requiring additional Cypher.

---

## What This Sample Demonstrates

| Feature | BogDB API Used | Where in the UI |
|---------|-----------------|-----------------|
| Snapshot change review | `BuildChangeReviewOverview` | Snapshot Comparison page |
| Top gaining/declining tables | `TopGainingNodeTables`, `TopDecliningNodeTables` | Snapshot Comparison page |
| Top gaining relationship types | `TopGainingRelationshipTypes` | Snapshot Comparison page |
| Numeric histogram | `HistogramNodesForTable` | Year Buckets page |
| Depth-bounded subgraph | `ProjectNeighborhood` | Neighborhood Explorer page |
| Multi-group change review | `BuildMultiGroupChangeReview` | Selection Families page |
| Selection family grouping | `BuildMultiGroupChangeReviewSelectionFamilies` | Selection Families page |
| Consensus across reviews | `BuildMultiGroupChangeReviewConsensus` | Selection Families page |
| Ad-hoc Cypher | `BogConnection.Query(...)` | Query Editor page |

---

## Architecture

The service has four distinct stages:

```
Stage 1 — Persistent store (EF Core + SQLite)
═══════════════════════════════════════════════
  EfPackage / EfPackageRelease / EfSnapshot
  EfSnapshotEntry (join table)
  EfSnapshotDep   (dependency edges, scoped to snapshot)
  EfVulnerability / EfSnapVulnAffects
       │
       │  PackageEcosystemGraphService.WarmUp()
       ▼

Stage 2 — BogDB in-memory graph (all snapshots combined)
═══════════════════════════════════════════════════════════
  _conn.UpsertNodeById("Package" / "Release" / "Vulnerability", ...)
  _conn.UpsertRelationshipById("DEPENDS_ON" / "AFFECTS" / "HAS_RELEASE", ...)

  ► Supports arbitrary Cypher including shortestPath and
    variable-length traversal across all snapshot data.
       │
       │  BuildShardForSnapshot(snap)  — one call per snapshot
       ▼

Stage 3 — GraphShard extraction (one per snapshot)
═══════════════════════════════════════════════════
  NodeShardTable  { TableName, Rows[] { ExternalId, Properties } }
  RelShardTable   { RelType, Rows[]  { RelId, SourceNodeId, TargetNodeId } }
  GraphShardAdjacency { Outgoing[nodeId], Incoming[nodeId] }

  ► Normalized via GraphShardNormalizer.Normalize(...)
  ► Stored in _shards["Q1-2024"], _shards["Q2-2024"], ...
       │
       │  GraphShardLocalRuntime.LoadExtract(shard)
       ▼

Stage 4 — Local runtime analytics (no Cypher needed)
═════════════════════════════════════════════════════
  _runtimes["Q2-2024"].BuildChangeReviewOverview(_shards["Q1-2024"])
  _runtimes["Q2-2024"].TopGainingNodeTables(...)
  _runtimes["Q2-2024"].HistogramNodesForTable(...)
  _runtimes["Q2-2024"].ProjectNeighborhood(seedId, depth)
  _runtimes["Q4-2024"].BuildMultiGroupChangeReview(...)
  _runtimes["Q4-2024"].BuildMultiGroupChangeReviewSelectionFamilies(...)
  _runtimes["Q4-2024"].BuildMultiGroupChangeReviewConsensus(...)
```

**Why two representations?** The BogDb graph (Stage 2) enables cross-snapshot Cypher — e.g. `shortestPath` from `spring-boot` to `log4j-core` across all quarters at once. The GraphShards (Stage 3+4) enable structured change analytics without Cypher: every comparison, histogram, and selection-family query runs purely on the shard objects via the local runtime.

### Data model

EF Core stores four quarterly snapshots (Q1–Q4 2024) of a synthetic package ecosystem:

- **38 packages** across npm, PyPI, NuGet, and Maven
- **~34 versioned releases per snapshot**, with version bumps and some additions/removals each quarter
- **6 known CVEs** with snapshot-scoped `AFFECTS` edges (some fixed between quarters)
- **Evolving dependency chains** — including the `spring-boot → spring-core → slf4j → log4j-core` chain that makes the `Log4Shell` vulnerability graph meaningful

### EF Core entities

| Entity | Table | Notes |
|--------|-------|-------|
| `EfPackage` | `Packages` | Ecosystem-scoped unique package |
| `EfPackageRelease` | `Releases` | One row per release; reused across snapshots |
| `EfSnapshot` | `Snapshots` | Quarterly point-in-time label |
| `EfSnapshotEntry` | `SnapshotEntries` | Join: which releases are in which snapshot |
| `EfSnapshotDep` | `SnapshotDeps` | Directional dependency edge scoped to a snapshot |
| `EfVulnerability` | `Vulnerabilities` | CVE master record |
| `EfSnapVulnAffects` | `VulnAffects` | Which release is affected, in which snapshot |

### BogDB graph schema

```cypher
// Node tables
Package   { name, ecosystem, author, description, ext_id }
Release   { package_name, package_ext_id, version, ecosystem,
            release_year, downloads, is_yanked, snapshot_label }
Vulnerability { cve_id, title, severity, cvss_score }

// Relationship tables
(Release)-[:DEPENDS_ON { dep_type }]->(Release)
(Vulnerability)-[:AFFECTS]->(Release)
(Package)-[:HAS_RELEASE]->(Release)
```

---

## Running the Sample

### Prerequisites

- .NET 9.0 SDK
- The project already references `BogDb.Core` from the local solution

### Start the server

```bash
cd BogDb.Samples.PackageEcosystem.Blazor
dotnet run
```

Navigate to **http://localhost:5060**.

On first run the database is created, seeded with the synthetic data, and the graph service warms up by loading all snapshots into the BogDB in-memory graph and building four `GraphShard`s.

---

## Pages and API Walkthrough

### Dashboard (`/`)

Displays aggregate statistics sourced directly from EF Core:

- Number of snapshots, unique packages, versioned releases
- Vulnerability count by severity (CRITICAL, HIGH)
- Yanked releases count

A callout demonstrates why variable-length path queries — like tracing the transitive dependency chain from `spring-boot` — are natural in BogDB but require recursive CTEs in SQL.

### Snapshot Comparison (`/compare`)

Pick any two snapshots and press **Compare**. The service calls:

```csharp
// In PackageEcosystemGraphService.GetChangeReview(fromLabel, toLabel)

var overview = toRuntime.BuildChangeReviewOverview(fromShard);
// → GraphShardChangeReviewOverview
//   .AddedNodeCount, .RemovedNodeCount, .AddedRelCount, .RemovedRelCount, .SummaryLabel

var gainNodes = toRuntime.TopGainingNodeTables(fromShard,
    new GraphShardTopNSpec { Limit = 5 });
// → GraphShardProjectionNodeDeltaResult
//   .Rows[].TableName, .AddedCount, .RemovedCount, .NetDelta

var decNodes = toRuntime.TopDecliningNodeTables(fromShard,
    new GraphShardTopNSpec { Limit = 5 });

var gainRel = toRuntime.TopGainingRelationshipTypes(fromShard,
    new GraphShardTopNSpec { Limit = 5 });
// → GraphShardProjectionRelationshipDeltaResult
//   .Rows[].RelType, .AddedCount, .RemovedCount, .NetDelta

var yearHistogram = toRuntime.HistogramNodesForTable(
    "Release", "release_year", bucketSize: 1m);
// → GraphShardHistogramResult
//   .Buckets[].StartInclusive, .Count
```

Results are displayed as colored stat cards, ranked delta tables, and a horizontal bar chart showing the release-year distribution.

### Year Buckets (`/buckets`)

Demonstrates `HistogramNodesForTable` in isolation. Select a snapshot and press **Compute**. The service calls:

```csharp
rt.HistogramNodesForTable("Release", "release_year", bucketSize: 1m)
```

This partitions all `Release` nodes in the shard by their `release_year` INT64 property in buckets of width 1 (one bucket per year). The result is shown as a Chart.js bar chart and a raw data table with share percentages.

**Why not `TimeBucketNodes`?** `TimeBucketNodes` requires a `DateTimeOffset`-valued property. `release_year` is an integer, so `HistogramNodesForTable` is the correct API here.

### Neighborhood Explorer (`/neighborhood`)

Pick a snapshot, a seed package, and a hop depth. The service calls:

```csharp
GraphShard neighborhood = rt.ProjectNeighborhood(seedId, depth);
```

This performs a BFS expansion on the in-memory shard up to `depth` hops, returning a new `GraphShard` sub-extract containing all reachable nodes and edges. The UI displays the resulting nodes (with table label badges) and edges as tables.

**Why this is graph-native:** in a relational store this requires a recursive CTE or level-by-level self-join chain with explicit de-duplication at each level. `ProjectNeighborhood` is a single runtime call.

### Selection Families (`/families`)

Demonstrates the full multi-group change pipeline across all quarterly snapshots:

```csharp
// Build a per-snapshot multi-group review against the Q1 baseline
var review = rt.BuildMultiGroupChangeReview(
    baseShard,
    nodeTables: ["Package", "Release"],
    relTypes:   ["DEPENDS_ON", "AFFECTS"]);

// Group scopes that made the same selection shape
var families = lastRt.BuildMultiGroupChangeReviewSelectionFamilies(reviews);
// → GraphShardSelectionFamilyResult
//   .Families[].Signature, .Keys, .ScopeCount,
//   .SelectedNodeTableKeys, .SelectedRelationshipTypeKeys

// Find what is universally common across all reviews
var consensus = lastRt.BuildMultiGroupChangeReviewConsensus(reviews);
// → GraphShardMultiGroupChangeReviewConsensus
//   .CommonSummary.SummaryLabel
```

Each family card shows:
- **Scope count** — how many snapshots share this selection shape
- **Signature** — the stable normalized shape key
- **Selected node tables** and **relationship types**

The consensus label summarizes what is universally present across all scope projections.

**Signature semantics:** two scopes share a signature if and only if their selected node-table key set and relationship-type key set are identical. The signature is stable across runs — it is a function of the selected keys, not the data.

### Query Editor (`/query`)

A live Cypher editor against the BogDB in-memory graph. Includes eight pre-loaded showcase queries:

| Title | Demonstrates |
|-------|-------------|
| All packages by ecosystem | Aggregation |
| Top 10 downloads — Q4-2024 | Filter + sort |
| Transitive deps from spring-boot | Variable-length path |
| All vuln-affected releases | Graph traversal |
| Yanked releases | Filter |
| Packages in all 4 snapshots | Multi-snapshot aggregation |
| Dep path: spring-boot → log4j-core | `shortestPath` |
| CRITICAL CVEs and packages | Filter + join |

---

## Key Implementation Notes

### EF → GraphShard bridge

The `PackageEcosystemGraphService.BuildShardForSnapshot` method constructs a `GraphShard` manually from EF snapshot data:

- `NodeShardTable` for `Package`, `Release`, and `Vulnerability`
- `RelShardTable` for `DEPENDS_ON` and `AFFECTS`
- `GraphShardAdjacency` with separate `Outgoing` / `Incoming` dictionaries keyed by node ID
- `RelShardRow.SourceNodeId` / `TargetNodeId` for edge endpoint tracking

Each shard is then normalized via `GraphShardNormalizer.Normalize(...)` and loaded into `GraphShardLocalRuntime.LoadExtract(shard)`.

### Why both BogDb graph tables and GraphShards?

- The **BogDb graph** (populated via `UpsertNodeById` / `UpsertRelationshipById`) supports arbitrary Cypher — including `shortestPath` and variable-length traversals — across all snapshots simultaneously.
- **GraphShards** are snapshot-scoped extracts that feed `GraphShardLocalRuntime`. All extraction analytics (change review, histograms, neighborhood projection, selection families) run on shards without Cypher.

This means snapshot comparison is purely graph-analytic (no `WHERE snapshot_label = 'Q1-2024'` filters needed in queries) while cross-snapshot Cypher queries benefit from all data being in a single graph.

---

## File Structure

```
BogDb.Samples.PackageEcosystem.Blazor/
├── Components/
│   ├── App.razor
│   ├── Routes.razor
│   ├── _Imports.razor
│   ├── Layout/
│   │   └── MainLayout.razor
│   └── Pages/
│       ├── Home.razor                 # Dashboard (EF stats)
│       ├── SnapshotComparison.razor   # BuildChangeReviewOverview + TopGaining/Declining
│       ├── TimeBuckets.razor          # HistogramNodesForTable
│       ├── NeighborhoodExplorer.razor # ProjectNeighborhood
│       ├── SelectionFamilies.razor    # BuildMultiGroupChangeReviewSelectionFamilies
│       └── QueryEditor.razor          # Live Cypher
├── Services/
│   ├── PackageEcosystemDbContext.cs   # EF Core models + DbContext
│   ├── PackageEcosystemSeedData.cs    # Synthetic 4-snapshot dataset
│   └── PackageEcosystemGraphService.cs # EF→BogDb bridge + GraphShardLocalRuntime
├── wwwroot/
│   ├── app.css                        # Dark glassmorphism design system
│   └── charts.js                      # Chart.js 4 helpers
├── Program.cs
├── appsettings.json
└── BogDb.Samples.PackageEcosystem.Blazor.csproj
```
