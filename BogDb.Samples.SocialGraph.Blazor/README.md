# BogDb.Samples.SocialGraph.Blazor

**Theme:** Social network explorer — six degrees, influence, and community detection  
**Stack:** Blazor Interactive Server · BogDB in-memory graph · Chart.js · Canvas force renderer · .NET 9

---

## What this sample demonstrates

An interactive Blazor application backed by a BogDB property graph modelling a fictional 200-person engineering organisation.

| Entity | Count | Details |
|---|---|---|
| `Person` | 200 | Spread across 10 teams / 3 departments. Title, team, department, is_bridge flag. |
| `Team` | 10 | Backend, Frontend, Platform, ML, Design, PM, Growth, DevOps, Security, Analytics |
| `Department` | 3 | Engineering, Product, Operations |
| `KNOWS` | 600 + | Directed social ties with `since` (year) and `strength` (0–1.0) properties |
| `REPORTS_TO` | 80+ | Org-hierarchy edges connecting ICs → leads → managers |
| `MEMBER_OF` | 200 | Person → Team edges |
| `PART_OF` | 10 | Team → Department edges |

---

## The graph-native moment

A relational database requires 6+ self-join CTEs to answer:  
*"What is the shortest chain of introductions between Alice and Zara?"*

```sql
-- SQL: 6 chained self-joins just to handle depth ≤ 6
WITH RECURSIVE chain AS (...)
SELECT ...
```

In BogDB, the undirected variable-length path is a single pattern:

```cypher
MATCH p = shortestPath(
  (a:Person {name: 'Alice Chen'})-[:KNOWS*]-(b:Person {name: 'Zara Khan'})
)
RETURN [n IN nodes(p) | n.name] AS introduction_chain, length(p) AS hops
```

The `-[:KNOWS*]-` (no arrow) traverses edges in both directions — impossible to express cleanly in SQL without two copies of every edge in the adjacency table.

---

## Pages

### Dashboard
- 6 stat cards: people count, KNOWS edge count, team/dept counts, average degree, bridge nodes, org edges.
- 3 Chart.js visualisations: people by department (doughnut), people by team (bar), top 10 most connected (bar).
- Graph-native moment callout with inline Cypher.

### Six Degrees Explorer
- Two `select` pickers for source and target person.
- Runs `shortestPath((a)-[:KNOWS*]-(b))` — **undirected** traversal across the full KNOWS graph.
- Results displayed as an animated hop-chain with team labels at each node.
- 5 pre-seeded interesting example paths (cross-department, engineering to operations, etc.).
- Inline Cypher callout shows the exact query used.

### Community Map
- Canvas-based force-directed graph simulation (200-frame spring layout).
- Nodes coloured by department: purple (Engineering), pink (Product), cyan (Operations).
- Edge width and opacity proportional to `KNOWS.strength`.
- Drag-to-pin interaction: click and drag any node to reposition it.
- Explains how the visual topology mirrors what `CALL wcc() YIELD *` discovers analytically.

### Influence Leaderboard
- Runs `CALL pagerank('KNOWS') YIELD node, rank`.
- Visual rank bars for top 10, table for top 20.
- Explains why PageRank beats raw degree — a hub connected to hubs ranks higher than an equal-degree peripheral node.
- Falls back to degree-based ranking if the runtime's pagerank procedure isn't available.

### Org Chart
- Runs `MATCH (emp)-[:REPORTS_TO]->(mgr)` to load all reporting edges.
- Renders as an indented recursive tree (C# `RenderFragment` recursion over `OrgNode.Reports`).
- Cypher callout shows path extraction via `REPORTS_TO*1..5`.

### Cypher Editor
- Free-form Cypher textarea with **Run** button.
- Output as interactive table (capped at 200 rows). Elapsed time displayed.
- 5 social-graph specific quick-starters pre-loaded.

### Query Library
- 10 curated queries, tag-filtered by `traversal | path | aggregation | algorithm | filter`.
- Accordion expand/collapse. Per-query **Run** button with inline results (capped at 20 rows).

---

## Key APIs demonstrated

| API | Used in |
|---|---|
| `BogDatabase.CreateInMemory()` | `SocialGraphService` constructor |
| `EnsureNodeTable` | Schema — `Person`, `Team`, `Department` |
| `EnsureRelTable` | `KNOWS` (with `since`, `strength`), `REPORTS_TO`, `MEMBER_OF`, `PART_OF` |
| `UpsertNodeById` | Seed — 200 people, 10 teams, 3 depts |
| `UpsertRelationshipById` | Seed — 600+ KNOWS, 80+ REPORTS_TO, 200 MEMBER_OF |
| `conn.Query(cypher)` | All queries via `Execute()` helper |
| `BeginWriteTransaction()` / `Commit()` | Schema + seed in separate transactions |
| `MATCH … -[:KNOWS*]-` | Undirected variable-length path |
| `shortestPath((a)-[:KNOWS*]-(b))` | Six Degrees Explorer + Q1, Q9 |
| `nodes(path)` / `length(path)` | Path chain extraction |
| `CALL pagerank('KNOWS') YIELD node, rank` | Influence Leaderboard + Q5 |
| `CALL wcc('KNOWS') YIELD node, componentId` | Q6 Community Detection |
| `COUNT(DISTINCT …)` + `WHERE` | Bridge detection (Q4), mutual friends (Q2) |
| `REPORTS_TO*1..5` | Org Chart traversal + Q7 |

---

## Architecture patterns

1. **KNOWS is bidirectional.** Each `KNOWS` edge is seeded in both directions (A→B and B→A) so undirected path patterns (`-[:KNOWS]-`) work correctly.
2. **PageRank fallback.** If `CALL pagerank()` isn't yet available in the runtime, `GetInfluenceLeaderboard()` automatically falls back to degree-based ranking.
3. **Separate schema and seed transactions.** `SetupSchema()` commits before `SeedData()` begins — ensures table definitions are visible before upserts.
4. **Cached PageRank.** The result is expensive — stored in `_cachedPageRank` after first compute, matching singleton lifetime.
5. **Force graph in JS.** The canvas simulation runs entirely in `graph.js` — Blazor passes node/edge data via `IJSRuntime.InvokeVoidAsync("drawForceGraph", …)` and never touches the canvas again.

---

## Running the sample

```bash
cd BogDb.Samples.SocialGraph.Blazor
dotnet run
# → http://localhost:5052
```

The graph seeds automatically on startup (schema + 200 people + 600+ edges, ~100ms).

---

## Graph schema

```
(Person)-[:KNOWS {since, strength}]-->(Person)   ← directed, seeded bidirectionally
(Person)-[:REPORTS_TO]-->(Person)                ← org hierarchy
(Person)-[:MEMBER_OF]-->(Team)
(Team)-[:PART_OF]-->(Department)
```

The `KNOWS` network is the heart of the sample — seeded with intra-team rings, cross-team bridges, and random high-strength collaborator pairs to ensure interesting shortest-path and PageRank results.
