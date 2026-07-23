# Temporal Trust Network — BogDB Console Sample

A runnable demonstration of BogDB's **per-hop comprehension filter** in variable-length
traversal — the ability to evaluate a predicate on **every hop during traversal** and prune
edges (and nodes) that fail:

```cypher
MATCH (a)-[:REL*lo..hi (e, n | WHERE <predicate over e / n>)]->(b)
```

`e` binds each intermediate edge and `n` each intermediate node; the `WHERE` runs per hop,
so a path only continues through hops that pass.

## Why it matters

The sample models a network of people who `TRUSTS` one another, where each edge has a
validity window `[valid_from, valid_to)` (stored as INT64 "day" epochs; an open `valid_to`
means still active) and a trust `weight`.

This lets you ask **"who could Ana reach through relationships that were *all* valid at time
T?"** in a single query — and get a different answer as the network changes over time:

| As of | Reachable from Ana |
|------:|--------------------|
| naive (any time) | ben, cara, dan, eve, finn, gus |
| day 30 | ben, cara, eve, finn, gus |
| day 70 | ben, **dan**, eve, finn, gus  *(cara is gone)* |
| day 30, trust ≥ 0.5 | ben, cara, gus  *(weak links pruned)* |

The key point: you **cannot** compute this by taking all paths and filtering afterward,
because two edges on a path (e.g. `Ben→Cara` valid `[10,50)` and `Ben→Dan` valid `[60,∞)`)
may never be valid at the same instant. The per-hop filter evaluates validity *during*
traversal and prunes early, so the reachable set — and the paths themselves — are correct
for the instant you ask about.

## Run it

```bash
# Guided walkthrough
dotnet run --project BogDb.Samples.TemporalGraph.Console

# Ad-hoc temporal reachability
dotnet run --project BogDb.Samples.TemporalGraph.Console -- reach ana --at 55
dotnet run --project BogDb.Samples.TemporalGraph.Console -- reach ana --at 70 --min-weight 0.85
dotnet run --project BogDb.Samples.TemporalGraph.Console -- reach ana --at 30 --active-only
```

`--at <t>` is the as-of instant; `--min-weight <w>` keeps only edges with `weight ≥ w`;
`--active-only` keeps only hops onto active nodes (a per-hop **node** predicate).

The temporal-validity pattern itself needs no special BogDB feature — see the
"Temporal (valid-time) edges" and "Recursive Path Traversal" sections of the root README.
