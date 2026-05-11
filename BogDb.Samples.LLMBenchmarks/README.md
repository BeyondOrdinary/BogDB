# BogDb.Samples.LLMBenchmarks

A self-contained **C# .NET 9** sample demonstrating the full BogDB property-graph workflow:
schema setup → graph seeding → Cypher queries → HTML visualisation report.

```bash
dotnet run --project BogDb.Samples.LLMBenchmarks
# → writes  bin/Debug/net9.0/llm_benchmark_report.html
```

---

## What it does

Seeds a BogDB **in-memory property graph** with real AI/LLM benchmark data (sourced from LMSYS Chatbot Arena, Open LLM Leaderboard, and official model cards — Q1 2026), then runs 10 Cypher queries that showcase the engine's features, and generates a dark-theme HTML report with 5 interactive Chart.js visualisations.

---

## Graph Schema

```
(Organisation) <─[:MADE_BY]─ (Model) ─[:SCORED_ON]─> (Benchmark)
                                  │
                                  └─[:BEATS]──────────> (Model)
```

| Node table | Key properties |
|------------|---------------|
| `Organisation` | name, country, tier |
| `Model` | name, version, params_b, context_k, open_source, cost_per_1m_in, arena_elo |
| `Benchmark` | name, category, max_score |

---

## Seeded data

| Entity | Count |
|--------|-------|
| Models | 12 (GPT-4o/4.1, Claude 3.5/3.7, Gemini 1.5/2.5, Llama 3.3/4, Mistral Large 2, DeepSeek-V3, Phi-4, Qwen2.5 72B) |
| Benchmarks | 7 (MMLU, HumanEval, GSM8K, MATH, BBH, ARC-Challenge, HellaSwag) |
| SCORED_ON edges | 84 |
| BEATS edges | 8 |
| MADE_BY edges | 12 |

---

## 10 Cypher showcase queries

| # | Title | Feature demonstrated | Rows |
|---|-------|---------------------|------|
| Q1 | All models & orgs | `MATCH` + `ORDER BY` | 12 |
| Q2 | Open-source only | `WHERE` boolean filter | 5 |
| Q3 | Avg score by org | `GROUP BY` + `AVG` | 8 |
| Q4 | Score stats by category | `MIN`/`MAX`/`AVG` | 4 |
| Q5 | Per-benchmark rankings | `RANK() OVER (PARTITION BY ...)` | 84 |
| Q6 | Top 5 overall | `ORDER BY … LIMIT` → TopK optimizer | 5 |
| Q7 | Who beats OpenAI on MATH? | Multi-hop `BEATS` graph traversal | 1 |
| Q8 | Value-for-money index | `WITH` aggregation + computed ratio | 12 |
| Q9 | DeepSeek-V3 scorecard | `WHERE` string equality | 7 |
| Q10 | Budget champions | Multi-condition `WHERE` over rel properties | 4 |

---

## Report charts

| Chart | Data source | Type |
|-------|------------|------|
| Multi-benchmark radar | Q5 (window RANK) | Radar — 6 models × 7 benchmarks |
| Avg score by organisation | Q3 (GROUP BY) | Horizontal bar |
| Value-for-money index | Q8 (WITH aggregation) | Horizontal bar — perf/$ ratio |
| Cost vs Arena ELO | Q1 (MATCH) | Bubble scatter — size = params, green = OSS |
| Benchmark scores | Q5 (window RANK) | Grouped bar — 6 models × 7 benchmarks |

---

## Key findings

- **ELO champion**: Gemini 2.5 Pro · 1380 ELO · 97.0% HumanEval
- **MATH record**: DeepSeek-V3 · 87.5% · beats GPT-4o by **+10.9pp** (via `BEATS` edge traversal)
- **Value champion**: Phi-4 at **1210 perf/$** ($0.07/M tokens)
- **Best ELO open-source**: Llama 4 Scout · 1254 ELO · $0.17/M tokens

---

## Project files

| File | Responsibility |
|------|---------------|
| `Program.cs` | Entry point |
| `BenchmarkData.cs` | `SchemaBuilder` · `DataSeeder` · `QueryShowcase` · `QueryResult` record |
| `HtmlReporter.cs` | Standalone HTML + Chart.js report generator |
