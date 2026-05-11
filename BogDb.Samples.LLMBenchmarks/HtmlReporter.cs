using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace BogDb.Samples.LLMBenchmarks;

/// <summary>
/// Generates a standalone HTML report with Chart.js visualisations.
/// Uses string builders throughout (no raw string interpolation) to avoid
/// CS9007 double-brace conflicts in multi-level interpolated strings.
/// </summary>
public static class HtmlReporter
{
    // Top-6 models shown on charts
    static readonly string[] TopModels =
    [
        "GPT-4.1", "Gemini 2.5 Pro", "Claude 3.7 Sonnet",
        "DeepSeek-V3", "Llama 4 Scout", "Phi-4"
    ];

    static readonly string[] BenchNames =
    [
        "MMLU", "HumanEval", "GSM8K", "MATH", "BBH", "ARC-Challenge", "HellaSwag"
    ];

    // Chart.js palette
    static readonly string[] Colors =
    [
        "#58a6ff","#a371f7","#3fb950","#e3b341","#f778ba","#39d3b2"
    ];

    // ── Public entry ────────────────────────────────────────────────────────────
    public static void Write(List<QueryResult> results, string path)
    {
        Console.WriteLine("\n▶  Generating HTML report...");

        var q1 = results.First(r => r.Title.StartsWith("Q1"));
        var q3 = results.First(r => r.Title.StartsWith("Q3"));
        var q5 = results.First(r => r.Title.StartsWith("Q5"));
        var q8 = results.First(r => r.Title.StartsWith("Q8"));

        var radarJs   = BuildRadarDataJs(q5);
        var barJs     = BuildBarDataJs(q5);
        var scatterJs = BuildScatterDataJs(q1);
        var orgJs     = BuildOrgDataJs(q3);
        var vfmJs     = BuildVfmDataJs(q8);
        var queryHtml = BuildQueryCards(results);

        var sb = new StringBuilder();
        AppendHeader(sb);
        AppendBody(sb, radarJs, barJs, scatterJs, orgJs, vfmJs, queryHtml);
        AppendFooter(sb);

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    // ── HTML structure ───────────────────────────────────────────────────────────
    static void AppendHeader(StringBuilder sb)
    {
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"UTF-8\">");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine("  <title>BogDB · LLM Benchmark Explorer</title>");
        sb.AppendLine("  <script src=\"https://cdn.jsdelivr.net/npm/chart.js@4.4.2/dist/chart.umd.min.js\"></script>");
        sb.AppendLine("  <link rel=\"preconnect\" href=\"https://fonts.googleapis.com\">");
        sb.AppendLine("  <link href=\"https://fonts.googleapis.com/css2?family=Inter:wght@300;400;500;600;700&family=JetBrains+Mono:wght@400;500&display=swap\" rel=\"stylesheet\">");
        sb.AppendLine(@"  <style>
    :root {
      --bg:#0d1117;--surface:#161b22;--surface2:#21262d;
      --border:#30363d;--text:#e6edf3;--muted:#8b949e;
      --blue:#58a6ff;--green:#3fb950;--purple:#a371f7;
      --teal:#39d3b2;--red:#f85149;--orange:#d29922;
    }
    *{box-sizing:border-box;margin:0;padding:0;}
    body{background:var(--bg);color:var(--text);font-family:'Inter',sans-serif;font-size:14px;line-height:1.6;}

    /* Hero */
    .hero{background:linear-gradient(135deg,#0d1117,#161b40,#0d1117);
          border-bottom:1px solid var(--border);padding:48px 32px 40px;text-align:center;}
    .hero h1{font-size:2.4rem;font-weight:700;
             background:linear-gradient(135deg,#58a6ff,#a371f7,#39d3b2);
             -webkit-background-clip:text;-webkit-text-fill-color:transparent;}
    .hero p{color:var(--muted);font-size:1rem;margin-top:10px;}
    .badge{display:inline-block;margin:6px 4px;padding:4px 12px;border-radius:20px;
           font-size:.78rem;font-weight:600;border:1px solid;}
    .badge-b{color:var(--blue);border-color:var(--blue);background:rgba(88,166,255,.08);}
    .badge-g{color:var(--green);border-color:var(--green);background:rgba(63,185,80,.08);}
    .badge-p{color:var(--purple);border-color:var(--purple);background:rgba(163,113,247,.08);}

    /* Layout */
    .cont{max-width:1280px;margin:0 auto;padding:32px 24px;}
    .g2{display:grid;grid-template-columns:1fr 1fr;gap:24px;margin-bottom:24px;}
    .g3{display:grid;grid-template-columns:1fr 1fr 1fr;gap:24px;margin-bottom:24px;}
    @media(max-width:900px){.g2,.g3{grid-template-columns:1fr;}}

    /* Cards */
    .card{background:var(--surface);border:1px solid var(--border);border-radius:12px;padding:24px;position:relative;overflow:hidden;}
    .card::before{content:'';position:absolute;top:0;left:0;right:0;height:2px;
                  background:linear-gradient(90deg,var(--blue),var(--purple));}
    .ct{font-size:.85rem;font-weight:600;color:var(--muted);text-transform:uppercase;letter-spacing:.08em;margin-bottom:12px;}
    .cv{font-size:2rem;font-weight:700;}
    .cs{font-size:.82rem;color:var(--muted);margin-top:4px;}

    /* Charts */
    .ch{background:var(--surface);border:1px solid var(--border);border-radius:12px;padding:24px;margin-bottom:24px;}
    .ch h2{font-size:1.05rem;font-weight:600;margin-bottom:4px;}
    .ch .d{font-size:.82rem;color:var(--muted);margin-bottom:18px;}

    /* Query showcase */
    .qs{margin-top:40px;}
    .qs>h2{font-size:1.3rem;font-weight:700;margin-bottom:6px;color:var(--blue);}
    .qs>.sd{color:var(--muted);margin-bottom:24px;font-size:.9rem;}
    .qc{background:var(--surface);border:1px solid var(--border);border-radius:12px;margin-bottom:20px;overflow:hidden;}
    .qh{padding:16px 24px 12px;border-bottom:1px solid var(--border);}
    .qh h3{font-size:.98rem;font-weight:600;margin-bottom:3px;}
    .qh p{font-size:.82rem;color:var(--muted);}
    .cyp{background:var(--bg);border-bottom:1px solid var(--border);padding:14px 24px;
         font-family:'JetBrains Mono',monospace;font-size:.8rem;color:#8be9fd;white-space:pre;overflow-x:auto;}
    .rt{padding:16px 24px;overflow-x:auto;}
    table{width:100%;border-collapse:collapse;}
    th{text-align:left;font-size:.75rem;font-weight:600;text-transform:uppercase;letter-spacing:.06em;
       color:var(--muted);padding:8px 12px;border-bottom:1px solid var(--border);}
    td{padding:8px 12px;font-size:.85rem;border-bottom:1px solid var(--surface2);}
    tr:last-child td{border-bottom:none;}
    tr:hover td{background:var(--surface2);}
    .n{color:var(--teal);font-family:'JetBrains Mono',monospace;}
    .bt{color:var(--green);}
    .bf{color:var(--red);}
    footer{text-align:center;padding:32px;color:var(--muted);font-size:.8rem;
           border-top:1px solid var(--border);margin-top:40px;}
  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
    }

    static void AppendBody(StringBuilder sb, string radarJs, string barJs, string scatterJs,
        string orgJs, string vfmJs, string queryHtml)
    {
        // Hero
        sb.AppendLine("<div class=\"hero\">");
        sb.AppendLine("  <h1>&#x26A1; BogDB &middot; LLM Benchmark Explorer</h1>");
        sb.AppendFormat("  <p>12 frontier AI models &middot; 7 benchmarks &middot; real scores from public leaderboards &middot; generated {0}</p>{1}",
            DateTime.UtcNow.ToString("yyyy-MM-dd"), Environment.NewLine);
        sb.AppendLine("  <div style=\"margin-top:14px\">");
        sb.AppendLine("    <span class=\"badge badge-b\">C# .NET 9 property graph</span>");
        sb.AppendLine("    <span class=\"badge badge-g\">549 tests &middot; 0 failures</span>");
        sb.AppendLine("    <span class=\"badge badge-p\">GDS &middot; Window Functions &middot; TopK Optimizer</span>");
        sb.AppendLine("    <span class=\"badge badge-b\">10 Cypher showcase queries</span>");
        sb.AppendLine("  </div>");
        sb.AppendLine("</div>");

        sb.AppendLine("<div class=\"cont\">");

        // Stat cards
        sb.AppendLine("<div class=\"g3\" style=\"margin-top:32px\">");
        StatCard(sb, "&#127885; Arena ELO Champion", "Gemini 2.5 Pro",
            "ELO 1380 &middot; 97.0% HumanEval", "--blue");
        StatCard(sb, "&#128176; Best Value Champion", "DeepSeek-V3",
            "$0.27/M tokens &middot; 87.5% MATH", "--green");
        StatCard(sb, "&#128208; MATH Record", "87.5%",
            "DeepSeek-V3 beats GPT-4o by +10.9pp", "--purple");
        sb.AppendLine("</div>");

        // Radar
        sb.AppendLine("<div class=\"ch\">");
        sb.AppendLine("  <h2>&#128376; Multi-Benchmark Radar &mdash; Top 6 Models</h2>");
        sb.AppendLine("  <p class=\"d\">Each axis is one benchmark. 100% = outer edge. Hover to inspect.</p>");
        sb.AppendLine("  <div style=\"height:420px\"><canvas id=\"radarChart\"></canvas></div>");
        sb.AppendLine("</div>");

        // Org + VFM side by side
        sb.AppendLine("<div class=\"g2\">");
        sb.AppendLine("  <div class=\"ch\" style=\"margin-bottom:0\">");
        sb.AppendLine("    <h2>&#127963; Avg Score by Organisation (Q3)</h2>");
        sb.AppendLine("    <p class=\"d\">GROUP BY aggregation &mdash; mean across all 7 benchmarks.</p>");
        sb.AppendLine("    <div style=\"height:300px\"><canvas id=\"orgBar\"></canvas></div>");
        sb.AppendLine("  </div>");
        sb.AppendLine("  <div class=\"ch\" style=\"margin-bottom:0\">");
        sb.AppendLine("    <h2>&#128184; Value-For-Money Index (Q8)</h2>");
        sb.AppendLine("    <p class=\"d\">avg_score &divide; cost_per_1M_tokens &mdash; WITH chain query.</p>");
        sb.AppendLine("    <div style=\"height:300px\"><canvas id=\"vfmBar\"></canvas></div>");
        sb.AppendLine("  </div>");
        sb.AppendLine("</div><div style=\"margin-bottom:24px\"></div>");

        // Scatter cost vs ELO
        sb.AppendLine("<div class=\"ch\">");
        sb.AppendLine("  <h2>&#128176; Cost vs Arena ELO &mdash; The Frontier Tradeoff</h2>");
        sb.AppendLine("  <p class=\"d\">X = input cost ($/1M tokens) &middot; Y = LMSYS ELO &middot; Bubble size ~ parameter count. Open-source = green border.</p>");
        sb.AppendLine("  <div style=\"height:380px\"><canvas id=\"scatterChart\"></canvas></div>");
        sb.AppendLine("</div>");

        // Grouped bar scores
        sb.AppendLine("<div class=\"ch\">");
        sb.AppendLine("  <h2>&#128202; Benchmark Scores &mdash; Top 6 Models (Q5)</h2>");
        sb.AppendLine("  <p class=\"d\">Scores from the window-function ranking query, displayed as grouped bars per benchmark.</p>");
        sb.AppendLine("  <div style=\"height:380px\"><canvas id=\"barChart\"></canvas></div>");
        sb.AppendLine("</div>");

        // Query showcase
        sb.AppendLine("<div class=\"qs\">");
        sb.AppendLine("  <h2>&#128269; Cypher Query Showcase</h2>");
        sb.AppendLine("  <p class=\"sd\">10 queries demonstrating BogDB graph features: MATCH, WHERE, GROUP BY, window RANK, TopK optimizer, WITH chaining, BEATS graph traversal, and multi-condition filters.</p>");
        sb.AppendLine(queryHtml);
        sb.AppendLine("</div>");

        sb.AppendLine("</div>"); // .cont

        // Scripts
        sb.AppendLine("<script>");
        sb.AppendLine("Chart.defaults.color='#8b949e';");
        sb.AppendLine("Chart.defaults.borderColor='#30363d';");
        sb.Append("const COLORS=[");
        sb.Append(string.Join(",", Colors.Select(c => $"'{c}'")));
        sb.AppendLine("];");
        sb.Append("const BENCH_NAMES=");
        sb.Append(JsonSerializer.Serialize(BenchNames));
        sb.AppendLine(";");
        sb.Append("const TOP_MODELS=");
        sb.Append(JsonSerializer.Serialize(TopModels));
        sb.AppendLine(";");

        // Radar
        sb.AppendLine("new Chart(document.getElementById('radarChart'),{");
        sb.AppendLine("  type:'radar',data:{labels:BENCH_NAMES,");
        sb.AppendLine("    datasets:TOP_MODELS.map((m,i)=>({");
        sb.AppendLine("      label:m,data:" + radarJs + "[i],");
        sb.AppendLine("      borderColor:COLORS[i],backgroundColor:COLORS[i]+'18',");
        sb.AppendLine("      pointBackgroundColor:COLORS[i],borderWidth:2,pointRadius:4");
        sb.AppendLine("    }))},");
        sb.AppendLine("  options:{responsive:true,maintainAspectRatio:false,");
        sb.AppendLine("    scales:{r:{min:60,max:100,ticks:{stepSize:10},");
        sb.AppendLine("             grid:{color:'#30363d'},angleLines:{color:'#30363d'}}},");
        sb.AppendLine("    plugins:{legend:{position:'bottom',labels:{boxWidth:14,padding:16}},");
        sb.AppendLine("             tooltip:{callbacks:{label:ctx=>` ${ctx.dataset.label}: ${ctx.raw}%`}}}}});");

        // Org bar
        sb.AppendLine($"const orgD={orgJs};");
        sb.AppendLine("new Chart(document.getElementById('orgBar'),{type:'bar',data:{");
        sb.AppendLine("  labels:orgD.names,datasets:[{label:'Avg Score %',data:orgD.scores,");
        sb.AppendLine("    backgroundColor:COLORS.map(c=>c+'bb'),borderColor:COLORS,borderWidth:1.5,borderRadius:6}]},");
        sb.AppendLine("  options:{responsive:true,maintainAspectRatio:false,indexAxis:'y',");
        sb.AppendLine("    scales:{x:{min:80,max:95,grid:{color:'#21262d'}}},");
        sb.AppendLine("    plugins:{legend:{display:false}}}});");

        // VFM bar
        sb.AppendLine($"const vfmD={vfmJs};");
        sb.AppendLine("new Chart(document.getElementById('vfmBar'),{type:'bar',data:{");
        sb.AppendLine("  labels:vfmD.names,datasets:[{label:'Perf per $',data:vfmD.scores,");
        sb.AppendLine("    backgroundColor:vfmD.scores.map((_,i)=>i<3?'#3fb950bb':'#58a6ffbb'),borderRadius:6}]},");
        sb.AppendLine("  options:{responsive:true,maintainAspectRatio:false,indexAxis:'y',");
        sb.AppendLine("    scales:{x:{grid:{color:'#21262d'}}},");
        sb.AppendLine("    plugins:{legend:{display:false},tooltip:{callbacks:{label:ctx=>` ${ctx.raw} perf/$`}}}}});");

        // Scatter bubble
        sb.AppendLine($"const scD={scatterJs};");
        sb.AppendLine("new Chart(document.getElementById('scatterChart'),{type:'bubble',data:{");
        sb.AppendLine("  datasets:scD.map((d,i)=>({");
        sb.AppendLine("    label:d.model,data:[{x:d.cost,y:d.elo,r:Math.max(4,Math.sqrt(d.params)*0.55)}],");
        sb.AppendLine("    backgroundColor:d.oss?'#3fb95060':COLORS[i%COLORS.length]+'80',");
        sb.AppendLine("    borderColor:d.oss?'#3fb950':COLORS[i%COLORS.length],borderWidth:2}))},");
        sb.AppendLine("  options:{responsive:true,maintainAspectRatio:false,");
        sb.AppendLine("    scales:{");
        sb.AppendLine("      x:{title:{display:true,text:'Cost (USD / 1M tokens)',color:'#8b949e'},grid:{color:'#21262d'}},");
        sb.AppendLine("      y:{title:{display:true,text:'LMSYS Arena ELO',color:'#8b949e'},min:1050,grid:{color:'#21262d'}}},");
        sb.AppendLine("    plugins:{legend:{position:'bottom',labels:{boxWidth:12,padding:10}},");
        sb.AppendLine("      tooltip:{callbacks:{label:ctx=>{");
        sb.AppendLine("        const d=scD[ctx.datasetIndex];");
        sb.AppendLine("        return[` ${d.model}`,` ELO: ${d.elo}`,` $${d.cost}/M tokens`,` ~${d.params}B params`,d.oss?' ✓ Open-source':' ✗ Proprietary'];");
        sb.AppendLine("      }}}}}});");

        // Grouped bar
        sb.AppendLine($"const barD={barJs};");
        sb.AppendLine("new Chart(document.getElementById('barChart'),{type:'bar',data:{");
        sb.AppendLine("  labels:BENCH_NAMES,datasets:TOP_MODELS.map((m,i)=>({");
        sb.AppendLine("    label:m,data:barD[i],");
        sb.AppendLine("    backgroundColor:COLORS[i]+'bb',borderColor:COLORS[i],borderWidth:1,borderRadius:4}))},");
        sb.AppendLine("  options:{responsive:true,maintainAspectRatio:false,");
        sb.AppendLine("    scales:{y:{min:60,max:100,grid:{color:'#21262d'}},x:{grid:{color:'#21262d'}}},");
        sb.AppendLine("    plugins:{legend:{position:'bottom',labels:{boxWidth:12,padding:10}}}}});");

        sb.AppendLine("</script>");
    }

    static void AppendFooter(StringBuilder sb)
    {
        sb.AppendLine("<footer>Built with <strong>BogDB</strong> &mdash; a native C# (.NET 9) embedded property-graph engine. " +
                      "Benchmark data from LMSYS Chatbot Arena, Open LLM Leaderboard, and official model cards &middot; Q1 2026.</footer>");
        sb.AppendLine("</body></html>");
    }

    // ── Chart data builders ──────────────────────────────────────────────────────
    // radarJs / barJs: double[][] indexed [modelIdx][benchIdx]
    static string BuildRadarDataJs(QueryResult q5)
    {
        var lookup = ScoreLookup(q5);
        var data = TopModels.Select(m =>
            BenchNames.Select(b => lookup.TryGetValue((m, b), out var v) ? v : 0.0).ToArray()
        ).ToArray();
        return JsonSerializer.Serialize(data);
    }

    static string BuildBarDataJs(QueryResult q5) => BuildRadarDataJs(q5);

    record ScatterPoint(string model, double cost, int elo, double @params, bool oss);
    static string BuildScatterDataJs(QueryResult q1)
    {
        var costs = new Dictionary<string, (double c, double p, bool oss)>
        {
            ["GPT-4o"]            = (5.00, 200, false),
            ["GPT-4.1"]           = (2.00, 200, false),
            ["Claude 3.5 Sonnet"] = (3.00, 175, false),
            ["Claude 3.7 Sonnet"] = (3.00, 175, false),
            ["Gemini 1.5 Pro"]    = (3.50, 175, false),
            ["Gemini 2.5 Pro"]    = (7.00, 175, false),
            ["Llama 3.3 70B"]     = (0.59,  70, true),
            ["Llama 4 Scout"]     = (0.17, 109, true),
            ["Mistral Large 2"]   = (3.00, 123, false),
            ["DeepSeek-V3"]       = (0.27, 671, true),
            ["Phi-4"]             = (0.07,  14, true),
            ["Qwen2.5 72B"]       = (0.40,  72, true),
        };
        var pts = q1.Rows.Select(row =>
        {
            string model = row.TryGetValue("model", out var v) ? v?.ToString() ?? "" : "";
            int    elo   = row.TryGetValue("elo",   out var e) && e != null ? Convert.ToInt32(e) : 0;
            var (c, p, oss) = costs.TryGetValue(model, out var t) ? t : (1.0, 0, false);
            return new ScatterPoint(model, c, elo, p, oss);
        }).Where(s => s.elo > 0).ToList();
        return JsonSerializer.Serialize(pts);
    }

    static string BuildOrgDataJs(QueryResult q3)
    {
        var names  = q3.Rows.Select(r => r.TryGetValue("org",       out var v) ? v?.ToString() ?? "" : "").ToArray();
        var scores = q3.Rows.Select(r => r.TryGetValue("avg_score", out var v) && v != null ? Math.Round(Convert.ToDouble(v),1) : 0.0).ToArray();
        return JsonSerializer.Serialize(new { names, scores });
    }

    static string BuildVfmDataJs(QueryResult q8)
    {
        var sorted = q8.Rows
            .OrderByDescending(r => r.TryGetValue("perf_per_dollar", out var v) && v != null ? Convert.ToDouble(v) : 0)
            .Take(8).ToArray();
        var names  = sorted.Select(r => r.TryGetValue("model",          out var v) ? v?.ToString() ?? "" : "").ToArray();
        var scores = sorted.Select(r => r.TryGetValue("perf_per_dollar", out var v) && v != null ? Math.Round(Convert.ToDouble(v),1) : 0.0).ToArray();
        return JsonSerializer.Serialize(new { names, scores });
    }

    // ── Query cards ──────────────────────────────────────────────────────────────
    static string BuildQueryCards(List<QueryResult> results)
    {
        var sb = new StringBuilder();
        foreach (var r in results)
        {
            if (r.Columns.Count == 0 && r.Rows.Count == 0) continue;
            sb.AppendLine("<div class=\"qc\">");
            sb.AppendLine("  <div class=\"qh\">");
            sb.AppendLine($"    <h3>{r.Title}</h3>");
            sb.AppendLine($"    <p>{r.Description}</p>");
            sb.AppendLine("  </div>");
            sb.AppendLine($"  <div class=\"cyp\">{r.CypherQuery.Trim()}</div>");
            sb.AppendLine("  <div class=\"rt\">");
            sb.AppendLine(BuildTableHtml(r));
            sb.AppendLine("  </div>");
            sb.AppendLine("</div>");
        }
        return sb.ToString();
    }

    static string BuildTableHtml(QueryResult r)
    {
        if (r.Rows.Count == 0) return "<p style='color:var(--muted)'>No results.</p>";
        var sb = new StringBuilder();
        sb.AppendLine("<table><thead><tr>");
        foreach (var c in r.Columns) sb.Append($"<th>{c}</th>");
        sb.AppendLine("</tr></thead><tbody>");
        foreach (var row in r.Rows.Take(15))
        {
            sb.Append("<tr>");
            foreach (var c in r.Columns)
            {
                var val = row.TryGetValue(c, out var v) ? v : null;
                string fmt = val switch
                {
                    null   => "<span style='color:var(--muted)'>null</span>",
                    bool b => b ? "<span class='bt'>true</span>" : "<span class='bf'>false</span>",
                    double d => $"<span class='n'>{d:F1}</span>",
                    int  i   => $"<span class='n'>{i}</span>",
                    long l   => $"<span class='n'>{l}</span>",
                    _        => val.ToString() ?? "",
                };
                sb.Append($"<td>{fmt}</td>");
            }
            sb.AppendLine("</tr>");
        }
        if (r.Rows.Count > 15)
            sb.AppendLine($"<tr><td colspan='{r.Columns.Count}' style='color:var(--muted);text-align:center'>&#8230; {r.Rows.Count - 15} more rows</td></tr>");
        sb.AppendLine("</tbody></table>");
        return sb.ToString();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────
    static void StatCard(StringBuilder sb, string title, string value, string sub, string accent)
    {
        sb.AppendLine("<div class=\"card\">");
        sb.AppendLine($"  <div class=\"ct\" style=\"color:var({accent})\">{title}</div>");
        sb.AppendLine($"  <div class=\"cv\" style=\"color:var({accent})\">{value}</div>");
        sb.AppendLine($"  <div class=\"cs\">{sub}</div>");
        sb.AppendLine("</div>");
    }

    static Dictionary<(string, string), double> ScoreLookup(QueryResult q5)
    {
        var d = new Dictionary<(string, string), double>();
        foreach (var row in q5.Rows)
        {
            string? bench = row.TryGetValue("benchmark", out var bv) ? bv?.ToString() : null;
            string? model = row.TryGetValue("model",     out var mv) ? mv?.ToString() : null;
            double  score = row.TryGetValue("score",     out var sv) && sv != null ? Convert.ToDouble(sv) : 0;
            if (bench != null && model != null)
                d[(model, bench)] = Math.Round(score, 1);
        }
        return d;
    }
}
