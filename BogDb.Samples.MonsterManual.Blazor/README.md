# BogDb.Samples.MonsterManual.Blazor

**Theme:** D&D bestiary browser — pull monsters from a live GitHub data source, filter them by ability scores, and assemble dungeon encounters with edge-based aggregation.
**Stack:** Blazor Interactive Server · BogDB in-memory graph · `IHttpClientFactory` GitHub ingestion · Chart.js · .NET 9

---

## What this sample demonstrates

An interactive Blazor application that builds its graph at runtime by pulling raw monster JSON from the open-source `theoperatore/dnd-monster-api` repository on GitHub. The first time you visit the Dashboard, pressing **Build Database** fetches up to 40 monster files, parses each stat block, and upserts them into an in-memory BogDB graph alongside a single seeded `Dungeon` node ("Tomb of Horrors"). Edges are added later as you assign monsters to the dungeon.

| Entity | Source | Details |
|---|---|---|
| `Monster` | GitHub fetch (up to 40) | name, size, type, alignment, ac, hp, cr, xp, str, dex, con, intel, wis, cha, image |
| `Dungeon` | Seeded at build time | `dungeon-1` — "Tomb of Horrors" |
| `IN_DUNGEON` | Created via UI | Monster → Dungeon edges added when you press *Add to Dungeon* in the Monster Manual page |

> The graph is built lazily — visit Dashboard and press **Build Database** before opening the Monster Manual or Dungeon Planner pages. The singleton service holds the graph for the life of the process.

---

## The graph-native moment

A relational schema would need a `monsters` table, a `dungeons` table, and a `dungeon_monsters` join table — then a JOIN+SUM to compute encounter difficulty:

```sql
SELECT SUM(m.xp)
FROM dungeon_monsters dm
JOIN monsters m ON m.id = dm.monster_id
WHERE dm.dungeon_id = 'dungeon-1';
```

In BogDB the join table doesn't exist — the relationship is the edge itself, and aggregation reads naturally as a one-hop traversal:

```cypher
MATCH (m:Monster)-[:IN_DUNGEON]->(d:Dungeon {id: 'dungeon-1'})
RETURN SUM(m.xp) AS total_difficulty
```

The same pattern powers the Dungeon Planner's roster table and threat-level meter.

---

## Pages

### Dashboard (`/`)
- **Build Database** button kicks off `MonsterGraphService.BuildDatabaseAsync(...)` — ingests up to 40 monsters from GitHub with a live progress bar (`processed / total`).
- Falls back to a hardcoded list of 20 monster names if the GitHub directory listing API is rate-limited.
- Once populated, displays 3 stat boxes: Monsters (nodes), Dungeons (nodes), Deployments (`IN_DUNGEON` edges).
- Attribution callout linking back to the `theoperatore/dnd-monster-api` source repo.

### Monster Manual (`/manual`)
- Three dual-handle range sliders for **STR / DEX / CON** (0–30).
- Pressing **Filter Bestiary** builds a `MATCH (m:Monster) WHERE …` query against six ability-score predicates and runs it through `MonsterGraphService.Execute(cypher)`.
- Result set is browsable one entry at a time via ◀ / ▶ buttons.
- Each entry renders as a D&D-style stat block: image header, name, alignment, CR, XP, ability scores with computed modifiers `(score − 10) / 2`, AC, HP.
- **Add to Dungeon** button creates an `IN_DUNGEON` edge to `dungeon-1` via `UpsertRelationshipById`.
- Live "Executed Cypher" panel shows the exact query the filter sliders generated.

### Dungeon Planner (`/dungeon`)
- Runs a one-hop traversal: `MATCH (m:Monster)-[:IN_DUNGEON]->(d:Dungeon {id: 'dungeon-1'}) RETURN …` ordered by XP descending.
- Sums XP across all matched monsters and bins it into a threat level: **Trivial / Moderate / Dangerous / Deadly**, driven by `< 1000 / < 4000 / < 10000 / ≥ 10000`.
- Chart.js bar chart of XP per monster (rendered via `JS.InvokeVoidAsync("renderDungeonChart", …)`).
- Roster table with **Dismiss** buttons that delete the `IN_DUNGEON` edge via a Cypher `DELETE`.

### Cypher Grimoire (`/cypher`)
- A short static reference page with three worked examples:
  1. Node filtering with attribute constraints (`WHERE m.str >= 13 AND m.str <= 17`)
  2. Edge creation (`CREATE (m)-[:IN_DUNGEON]->(d)`)
  3. Aggregation traversal (`SUM(m.xp)` across `IN_DUNGEON`)

---

## Key APIs demonstrated

| API | Used in |
|---|---|
| `BogDatabase.CreateInMemory()` | `MonsterGraphService` constructor |
| `EnsureNodeTable` | Schema — `Monster` (15 properties), `Dungeon` |
| `EnsureRelTable` | `IN_DUNGEON` (Monster → Dungeon, no edge properties) |
| `UpsertNodeById` | Per-monster ingest from parsed GitHub JSON; default dungeon seed |
| `UpsertRelationshipById` | "Add to Dungeon" action in the Manual page |
| `BeginWriteTransaction()` / `Commit()` | Schema setup + bulk ingest + per-action edge writes |
| `conn.Query(cypher)` | Stats counts, ability-score filtering, dungeon aggregation, edge deletion |
| `MATCH … WHERE … AND …` | Six-way ability-score range filter in the Monster Manual page |
| `MATCH (a)-[:IN_DUNGEON]->(b)` | Dungeon roster + total-XP aggregation |
| `MATCH … DELETE r` | "Dismiss" button removes a monster from the dungeon |
| `SUM(m.xp)` | Encounter threat-level computation |

---

## Architecture patterns

1. **Lazy, user-triggered ingest.** Unlike most samples that seed on startup, the database starts empty. `BuildDatabaseAsync` is invoked by the Dashboard button so the user controls when the GitHub fetch happens — and the progress bar makes the cost visible.
2. **Singleton service with `IHttpClientFactory`.** The `MonsterGraphService` is a singleton (so the graph persists across page navigations), but `HttpClient` is pulled from a named factory inside each ingest call. `AddHttpClient<T>` would bind a transient client to a singleton — broken; using `IHttpClientFactory.CreateClient("MonsterApiClient")` avoids that.
3. **Hardcoded fallback list.** GitHub's contents API is unauthenticated and rate-limited. If the directory listing call fails, `BuildDatabaseAsync` falls back to a hardcoded array of 20 monster filenames so the sample still works.
4. **`intel` instead of `int`.** `int` collides with the runtime keyword, so the Intelligence stat is stored as `intel` on the `Monster` node. Worth noting if you write your own Cypher against the schema.
5. **Single seeded dungeon.** All `IN_DUNGEON` edges target `dungeon-1` ("Tomb of Horrors") — the sample focuses on edge aggregation, not multi-dungeon planning. Extending it to multiple dungeons is a one-line change in `BuildDatabaseAsync`.
6. **Throttled fetch.** `await Task.Delay(100)` between file fetches to stay polite with the GitHub raw-content API.

---

## Running the sample

```bash
cd BogDb.Samples.MonsterManual.Blazor
dotnet run
# → http://localhost:5042
```

Visit the Dashboard, press **Build Database**, wait for the progress bar (~30–60 s depending on GitHub responsiveness), then navigate to **Monster Manual** to filter and **Dungeon Planner** to aggregate.

No API key is required, but the ingest needs outbound HTTPS to `api.github.com` and `raw.githubusercontent.com`.

---

## Graph schema

```
(Monster {id, name, size, type, alignment, ac, hp, cr, xp,
          str, dex, con, intel, wis, cha, image})
   ─[:IN_DUNGEON]─►
(Dungeon {id, name})
```

The schema is intentionally small — one node table with a rich property bag, one node table with two properties, and one relationship type with no properties. The complexity in this sample is in the **ingest pipeline** (parsing varied JSON shapes from the source repo) and in showing how a join table collapses into an edge.

---

## Attribution

Monster data and images are pulled from [theoperatore/dnd-monster-api](https://github.com/theoperatore/dnd-monster-api) at runtime. Nothing is bundled with the sample.
