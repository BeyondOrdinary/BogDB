// ─────────────────────────────────────────────────────────────────────────────
//  BogDB · LLM Benchmark Explorer — entry point
//  Run:  dotnet run --project BogDb.Samples.LLMBenchmarks
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.IO;
using System.Reflection;
using BogDb.Core.Main;
using BogDb.Samples.LLMBenchmarks;

Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
Console.WriteLine("║       BogDB · LLM Benchmark Explorer                  ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════╝\n");

using var db   = BogDatabase.Open(":memory:");
using var conn = new BogConnection(db);

Console.WriteLine("▶  Setting up graph schema...");
SchemaBuilder.Setup(conn);

Console.WriteLine("▶  Seeding organisations, models, benchmarks, and scores...");
DataSeeder.Seed(conn);

Console.WriteLine("\n════════════ QUERY SHOWCASE ════════════\n");
var results = QueryShowcase.Run(conn);

var outPath  = Path.Combine(
    Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
    "llm_benchmark_report.html");
HtmlReporter.Write(results, outPath);

Console.WriteLine($"\n╔════════════════════════════════════════╗");
Console.WriteLine($"║  Report written to:                    ║");
Console.WriteLine($"║  {Path.GetFileName(outPath),-38}║");
Console.WriteLine($"╚════════════════════════════════════════╝");
Console.WriteLine($"\nFull path: {outPath}");
