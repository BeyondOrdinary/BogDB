using System.Collections.Concurrent;
using BogDb.Core.Common;
using BogDb.Core.Extraction;
using BogDb.Core.Main;
using Microsoft.EntityFrameworkCore;

namespace BogDb.Samples.PackageEcosystem.Blazor.Services;

// ── Domain view models (no EF, consumed by Razor pages) ──────────────────────

public record SnapshotSummary(int Id, string Label, DateTime TakenAt,
    int PackageCount, int DepCount, int VulnCount);

public record DashboardStats(
    int TotalPackages, int TotalReleases, int TotalVulns,
    int SnapshotCount, int CriticalVulns, int HighVulns,
    int YankedReleases);

public record ChangeReviewSummary(
    string FromLabel, string ToLabel,
    int AddedPackages, int RemovedPackages,
    int AddedDeps, int RemovedDeps,
    int AddedVulns, int RemovedVulns,
    string OverviewLabel,
    List<TopChangedRow> TopGainingPackages,
    List<TopChangedRow> TopDecliningPackages,
    List<TopChangedRow> TopGainingDepTypes,
    List<TimeBucketRow> DownloadTimeBuckets);

public record TopChangedRow(string Key, int AddedCount, int RemovedCount, int NetDelta);
public record TimeBucketRow(string BucketLabel, int Count);

public record NeighborhoodResult(
    string SeedId, string SeedName, int Depth,
    List<NeighborNode> Nodes, List<NeighborEdge> Edges);

public record NeighborNode(string Id, string Name, string Version, string Ecosystem, string TableName);
public record NeighborEdge(string FromId, string ToId, string RelType);

public record SelectionFamilyRow(
    string Signature, List<string> ScopeKeys,
    List<string> SelectedNodeTables, List<string> SelectedRelTypes, int ScopeCount);

public record QueryResponse(bool IsSuccess, string Error,
    List<string> Columns, List<Dictionary<string, object?>> Rows, long ElapsedMs);

public record ShowcaseQuery(string Title, string Tag, string Cypher);

// ── Graph Service ─────────────────────────────────────────────────────────────

/// <summary>
/// Singleton that bridges EF Core (SQLite) snapshots into BogDB in-memory graph.
///
/// Architecture:
///   - EF Core is the source-of-truth store (Package, Release, Snapshot, Dep, Vuln).
///   - On WarmUp(), all snapshots are loaded from EF and ingested into BogDb as graph tables.
///   - Each snapshot's data is also materialised as a <see cref="GraphShard"/> for use with
///     <see cref="GraphShardLocalRuntime"/> extraction analytics APIs.
///   - BogDb is used for ad-hoc Cypher queries and variable-length traversal.
///   - GraphShardLocalRuntime APIs handle all change-review, time-bucket, neighborhood, and
///     selection-family analytics — no Cypher needed for those operations.
/// </summary>
public sealed class PackageEcosystemGraphService : IDisposable
{
    private readonly IDbContextFactory<PackageEcosystemDbContext> _factory;
    private readonly BogDatabase _db;
    private readonly BogConnection _conn;

    private readonly ConcurrentDictionary<string, GraphShard> _shards = new();
    private readonly ConcurrentDictionary<string, GraphShardLocalRuntime> _runtimes = new();
    private List<SnapshotSummary> _snapshots = [];

    public PackageEcosystemGraphService(IDbContextFactory<PackageEcosystemDbContext> factory)
    {
        _factory = factory;
        _db   = BogDatabase.CreateInMemory();
        _conn = new BogConnection(_db);
        SetupSchema();
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void WarmUp()
    {
        using var ctx = _factory.CreateDbContext();

        var allSnaps = ctx.Snapshots
            .Include(s => s.Entries).ThenInclude(e => e.Release).ThenInclude(r => r.Package)
            .Include(s => s.Dependencies).ThenInclude(d => d.FromRelease).ThenInclude(r => r.Package)
            .Include(s => s.Dependencies).ThenInclude(d => d.ToRelease).ThenInclude(r => r.Package)
            .Include(s => s.VulnAffects).ThenInclude(v => v.Vulnerability)
            .Include(s => s.VulnAffects).ThenInclude(v => v.Release).ThenInclude(r => r.Package)
            .AsNoTracking()
            .OrderBy(s => s.TakenAt)
            .ToList();

        _snapshots = allSnaps.Select(BuildSnapshotSummary).ToList();

        foreach (var snap in allSnaps)
        {
            IngestSnapshotIntoBogDb(snap);
            var shard = BuildShardForSnapshot(snap);
            _shards[snap.Label]   = shard;
            _runtimes[snap.Label] = GraphShardLocalRuntime.LoadExtract(shard);
        }
    }

    public void Dispose()
    {
        _conn.Dispose();
        _db.Dispose();
    }

    // ── Public query surface ──────────────────────────────────────────────────

    public DashboardStats GetDashboardStats()
    {
        using var ctx = _factory.CreateDbContext();
        return new DashboardStats(
            ctx.Packages.Count(),
            ctx.Releases.Count(),
            ctx.Vulnerabilities.Count(),
            ctx.Snapshots.Count(),
            ctx.Vulnerabilities.Count(v => v.Severity == "CRITICAL"),
            ctx.Vulnerabilities.Count(v => v.Severity == "HIGH"),
            ctx.Releases.Count(r => r.IsYanked));
    }

    public List<SnapshotSummary> GetSnapshots() => _snapshots;

    /// <summary>
    /// Change review between two snapshots using GraphShardLocalRuntime.
    /// Calls BuildChangeReviewOverview, TopGainingNodeTables, TopDecliningNodeTables,
    /// TopGainingRelationshipTypes — all extraction analytics APIs.
    /// </summary>
    public ChangeReviewSummary? GetChangeReview(string fromLabel, string toLabel)
    {
        if (!_runtimes.TryGetValue(toLabel, out var toRuntime)) return null;
        if (!_shards.TryGetValue(fromLabel, out var fromShard)) return null;

        var overview  = toRuntime.BuildChangeReviewOverview(fromShard);
        var gainNodes = toRuntime.TopGainingNodeTables(fromShard,
            new GraphShardTopNSpec { Limit = 5 });
        var decNodes  = toRuntime.TopDecliningNodeTables(fromShard,
            new GraphShardTopNSpec { Limit = 5 });
        var gainRel   = toRuntime.TopGainingRelationshipTypes(fromShard,
            new GraphShardTopNSpec { Limit = 5 });

        var yearHistogram = toRuntime.HistogramNodesForTable("Release", "release_year", bucketSize: 1m);

        return new ChangeReviewSummary(
            FromLabel: fromLabel,
            ToLabel:   toLabel,
            AddedPackages:    overview.AddedNodeCount,
            RemovedPackages:  overview.RemovedNodeCount,
            AddedDeps:        overview.AddedRelCount,
            RemovedDeps:      overview.RemovedRelCount,
            AddedVulns:       0,
            RemovedVulns:     0,
            OverviewLabel:    overview.SummaryLabel,
            TopGainingPackages:   MapNodeDelta(gainNodes.Rows),
            TopDecliningPackages: MapNodeDelta(decNodes.Rows),
            TopGainingDepTypes:   MapRelDelta(gainRel.Rows),
            DownloadTimeBuckets:  MapYearHistogram(yearHistogram));
    }

    /// <summary>
    /// Multi-group change review across all snapshot pairs.
    /// Uses BuildMultiGroupChangeReview + BuildMultiGroupChangeReviewSelectionFamilies + Consensus.
    /// </summary>
    public (List<SelectionFamilyRow> Families, string ConsensusLabel) GetSelectionFamilies()
    {
        var snapLabels = _snapshots.Select(s => s.Label).ToList();
        if (snapLabels.Count < 2) return ([], "Need at least 2 snapshots");

        var baseLabel = snapLabels[0];
        if (!_shards.TryGetValue(baseLabel, out var baseShard)) return ([], "Missing base shard");

        var reviews = new Dictionary<string, GraphShardMultiGroupChangeReview>();
        foreach (var label in snapLabels.Skip(1))
        {
            if (!_runtimes.TryGetValue(label, out var rt)) continue;
            var review = rt.BuildMultiGroupChangeReview(baseShard,
                nodeTables: ["Package", "Release"],
                relTypes: ["DEPENDS_ON", "AFFECTS"]);
            reviews[label] = review;
        }

        if (reviews.Count == 0) return ([], "No reviews built");

        var lastLabel = snapLabels[^1];
        if (!_runtimes.TryGetValue(lastLabel, out var lastRt))
            return ([], "Missing last runtime");

        var families  = lastRt.BuildMultiGroupChangeReviewSelectionFamilies(reviews);
        var consensus = lastRt.BuildMultiGroupChangeReviewConsensus(reviews);

        var rows = families.Families.Select(f => new SelectionFamilyRow(
            f.Signature,
            f.Keys,
            f.SelectedNodeTableKeys,
            f.SelectedRelationshipTypeKeys,
            f.ScopeCount)).ToList();

        return (rows, consensus.CommonSummary.SummaryLabel);
    }

    /// <summary>ProjectNeighborhood from a runtime shard for a given package external ID.</summary>
    public NeighborhoodResult? GetNeighborhood(string snapshotLabel, string packageExternalId, int depth = 2)
    {
        if (!_runtimes.TryGetValue(snapshotLabel, out var rt)) return null;
        if (!_shards.TryGetValue(snapshotLabel, out var shard)) return null;

        // Find the first Release node for this package in the shard
        var relTable = shard.NodeTables.GetValueOrDefault("Release");
        if (relTable is null) return null;

        var releaseRow = relTable.Rows.FirstOrDefault(r =>
            r.Properties.TryGetValue("package_ext_id", out var pid) &&
            string.Equals(pid?.ToString(), packageExternalId, StringComparison.Ordinal));

        if (releaseRow is null) return null;
        var seedId = releaseRow.ExternalId;

        GraphShard neighborhood;
        try { neighborhood = rt.ProjectNeighborhood(seedId, depth); }
        catch { return null; }

        var nodes = new List<NeighborNode>();
        var edges = new List<NeighborEdge>();

        foreach (var (table, tableData) in neighborhood.NodeTables)
        {
            foreach (var row in tableData.Rows)
            {
                var name    = row.Properties.GetValueOrDefault("name")?.ToString() ?? row.ExternalId;
                var version = row.Properties.GetValueOrDefault("version")?.ToString() ?? "";
                var eco     = row.Properties.GetValueOrDefault("ecosystem")?.ToString() ?? "";
                nodes.Add(new NeighborNode(row.ExternalId, name, version, eco, table));
            }
        }

        foreach (var (relType, relTableData) in neighborhood.RelTables)
        {
            foreach (var row in relTableData.Rows)
            {
                edges.Add(new NeighborEdge(row.SourceNodeId, row.TargetNodeId, relType));
            }
        }

        var seedNode = nodes.FirstOrDefault(n => n.Id == seedId);
        return new NeighborhoodResult(seedId, seedNode?.Name ?? seedId, depth, nodes, edges);
    }

    /// <summary>Year-bucketed histogram of releases for a given snapshot.</summary>
    public List<TimeBucketRow> GetTimeBuckets(string snapshotLabel)
    {
        if (!_runtimes.TryGetValue(snapshotLabel, out var rt)) return [];
        var result = rt.HistogramNodesForTable("Release", "release_year", bucketSize: 1m);
        return MapYearHistogram(result);
    }

    public QueryResponse Execute(string cypher)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r  = _conn.Query(cypher);
        sw.Stop();
        if (!r.IsSuccess)
            return new QueryResponse(false, r.ErrorMessage ?? "Query failed",
                [], [], sw.ElapsedMilliseconds);

        var cols = r.ColumnNames.ToList();
        var rows = new List<Dictionary<string, object?>>();
        while (r.HasNext())
        {
            var row = r.GetNext();
            rows.Add(row.GetAsDictionary().ToDictionary(kv => kv.Key, kv => (object?)kv.Value));
        }
        return new QueryResponse(true, string.Empty, cols, rows, sw.ElapsedMilliseconds);
    }

    public static readonly List<ShowcaseQuery> ShowcaseQueries =
    [
        new("All packages by ecosystem", "aggregation",
            "MATCH (p:Package) RETURN p.ecosystem, count(p) AS c ORDER BY c DESC"),
        new("Top 10 downloads — Q4-2024", "filter",
            "MATCH (r:Release) WHERE r.snapshot_label = 'Q4-2024' RETURN r.package_name, r.version, r.downloads ORDER BY r.downloads DESC LIMIT 10"),
        new("Transitive deps from spring-boot", "traversal",
            "MATCH (r:Release {package_name: 'spring-boot'})-[:DEPENDS_ON*1..5]->(dep:Release) WHERE r.snapshot_label = 'Q4-2024' AND dep.snapshot_label = 'Q4-2024' RETURN DISTINCT dep.package_name, dep.version ORDER BY dep.package_name"),
        new("All vuln-affected releases", "traversal",
            "MATCH (v:Vulnerability)-[:AFFECTS]->(r:Release) RETURN v.cve_id, v.severity, r.package_name, r.version, r.snapshot_label ORDER BY v.cvss_score DESC"),
        new("Yanked releases", "filter",
            "MATCH (r:Release) WHERE r.is_yanked = true RETURN r.package_name, r.version, r.snapshot_label ORDER BY r.package_name"),
        new("Packages in all 4 snapshots", "aggregation",
            "MATCH (r:Release) WITH r.package_name AS name, count(DISTINCT r.snapshot_label) AS snap_count WHERE snap_count = 4 RETURN name, snap_count ORDER BY name"),
        new("Dep path: spring-boot → log4j-core", "path",
            "MATCH (a:Release {package_name: 'spring-boot', snapshot_label: 'Q1-2024'}), (b:Release {package_name: 'log4j-core', snapshot_label: 'Q1-2024'}) MATCH p = shortestPath((a)-[:DEPENDS_ON*]->(b)) RETURN [n IN nodes(p) | n.package_name] AS chain, length(p) AS hops"),
        new("CRITICAL CVEs and packages", "filter",
            "MATCH (v:Vulnerability)-[:AFFECTS]->(r:Release) WHERE v.severity = 'CRITICAL' RETURN v.cve_id, v.title, r.package_name, r.version, r.snapshot_label"),
    ];

    // ── Schema setup ─────────────────────────────────────────────────────────

    private void SetupSchema()
    {
        _conn.BeginWriteTransaction();

        _conn.EnsureNodeTable("Package", new()
        {
            ["name"]        = LogicalTypeID.STRING,
            ["ecosystem"]   = LogicalTypeID.STRING,
            ["author"]      = LogicalTypeID.STRING,
            ["description"] = LogicalTypeID.STRING,
            ["ext_id"]      = LogicalTypeID.STRING,
        });

        _conn.EnsureNodeTable("Release", new()
        {
            ["package_name"]   = LogicalTypeID.STRING,
            ["package_ext_id"] = LogicalTypeID.STRING,
            ["version"]        = LogicalTypeID.STRING,
            ["ecosystem"]      = LogicalTypeID.STRING,
            ["release_year"]   = LogicalTypeID.INT64,
            ["downloads"]      = LogicalTypeID.INT64,
            ["is_yanked"]      = LogicalTypeID.BOOL,
            ["snapshot_label"] = LogicalTypeID.STRING,
        });

        _conn.EnsureNodeTable("Vulnerability", new()
        {
            ["cve_id"]     = LogicalTypeID.STRING,
            ["title"]      = LogicalTypeID.STRING,
            ["severity"]   = LogicalTypeID.STRING,
            ["cvss_score"] = LogicalTypeID.DOUBLE,
        });

        _conn.EnsureRelTable("DEPENDS_ON", "Release", "Release", new()
        {
            ["dep_type"] = LogicalTypeID.STRING,
        });
        _conn.EnsureRelTable("AFFECTS",     "Vulnerability", "Release",  new());
        _conn.EnsureRelTable("HAS_RELEASE", "Package",       "Release",  new());

        _conn.Commit();
    }

    // ── EF → BogDb ingest ─────────────────────────────────────────────────────

    private void IngestSnapshotIntoBogDb(EfSnapshot snap)
    {
        _conn.BeginWriteTransaction();

        foreach (var entry in snap.Entries)
        {
            var pkg = entry.Release.Package;
            _conn.UpsertNodeById("Package", pkg.ExternalId, new Dictionary<string, object?>
            {
                ["name"] = pkg.Name, ["ecosystem"] = pkg.Ecosystem,
                ["author"] = pkg.Author, ["description"] = pkg.Description,
                ["ext_id"] = pkg.ExternalId,
            });
        }

        foreach (var entry in snap.Entries)
        {
            var rel = entry.Release;
            var pkg = rel.Package;
            _conn.UpsertNodeById("Release", rel.ExternalId, new Dictionary<string, object?>
            {
                ["package_name"]   = pkg.Name,
                ["package_ext_id"] = pkg.ExternalId,
                ["version"]        = rel.Version,
                ["ecosystem"]      = pkg.Ecosystem,
                ["release_year"]   = (long)rel.ReleaseYear,
                ["downloads"]      = (long)rel.Downloads,
                ["is_yanked"]      = rel.IsYanked,
                ["snapshot_label"] = snap.Label,
            });
            _conn.UpsertRelationshipById("HAS_RELEASE", pkg.ExternalId, rel.ExternalId,
                new Dictionary<string, object?> { });
        }

        foreach (var va in snap.VulnAffects)
        {
            var v = va.Vulnerability;
            _conn.UpsertNodeById("Vulnerability", v.ExternalId, new Dictionary<string, object?>
            {
                ["cve_id"] = v.CveId, ["title"] = v.Title,
                ["severity"] = v.Severity, ["cvss_score"] = v.CvssScore,
            });
        }

        foreach (var dep in snap.Dependencies)
            _conn.UpsertRelationshipById("DEPENDS_ON",
                dep.FromRelease.ExternalId, dep.ToRelease.ExternalId,
                new Dictionary<string, object?> { ["dep_type"] = dep.DepType });

        foreach (var va in snap.VulnAffects)
            _conn.UpsertRelationshipById("AFFECTS",
                va.Vulnerability.ExternalId, va.Release.ExternalId,
                new Dictionary<string, object?> { });

        _conn.Commit();
    }

    // ── Shard builder ────────────────────────────────────────────────────────

    private GraphShard BuildShardForSnapshot(EfSnapshot snap)
    {
        var nodeTables = new Dictionary<string, NodeShardTable>();
        var relTables  = new Dictionary<string, RelShardTable>();

        // OUTGOING adjacency: Release → Release via DEPENDS_ON
        var outgoing = new Dictionary<string, List<ShardEdgeRef>>(StringComparer.Ordinal);
        var incoming = new Dictionary<string, List<ShardEdgeRef>>(StringComparer.Ordinal);

        // Package nodes
        var pkgRows = snap.Entries
            .Select(e => e.Release.Package)
            .DistinctBy(p => p.ExternalId)
            .Select(pkg => new NodeShardRow
            {
                ExternalId = pkg.ExternalId,
                Properties = new Dictionary<string, object?>
                {
                    ["name"] = pkg.Name, ["ecosystem"] = pkg.Ecosystem,
                    ["author"] = pkg.Author, ["ext_id"] = pkg.ExternalId,
                }
            }).ToList();
        nodeTables["Package"] = new NodeShardTable { TableName = "Package", Rows = pkgRows };

        // Release nodes + adjacency
        var relRows = snap.Entries.Select(entry =>
        {
            var rel = entry.Release;
            var pkg = rel.Package;
            outgoing[rel.ExternalId] = [];
            incoming[rel.ExternalId] = [];
            return new NodeShardRow
            {
                ExternalId = rel.ExternalId,
                Properties = new Dictionary<string, object?>
                {
                    ["package_name"]   = pkg.Name,
                    ["package_ext_id"] = pkg.ExternalId,
                    ["version"]        = rel.Version,
                    ["ecosystem"]      = pkg.Ecosystem,
                    ["release_year"]   = (long)rel.ReleaseYear,
                    ["downloads"]      = (long)rel.Downloads,
                    ["is_yanked"]      = rel.IsYanked,
                    ["snapshot_label"] = snap.Label,
                }
            };
        }).ToList();
        nodeTables["Release"] = new NodeShardTable { TableName = "Release", Rows = relRows };

        // Vuln nodes
        var vulnRows = snap.VulnAffects
            .Select(va => va.Vulnerability)
            .DistinctBy(v => v.ExternalId)
            .Select(v => new NodeShardRow
            {
                ExternalId = v.ExternalId,
                Properties = new Dictionary<string, object?>
                {
                    ["cve_id"] = v.CveId, ["title"] = v.Title,
                    ["severity"] = v.Severity, ["cvss_score"] = v.CvssScore,
                }
            }).ToList();
        if (vulnRows.Count > 0)
            nodeTables["Vulnerability"] = new NodeShardTable { TableName = "Vulnerability", Rows = vulnRows };

        // DEPENDS_ON edges + populate adjacency
        var depRelRows = snap.Dependencies.Select(dep =>
        {
            var relId = $"dep-{dep.Id}";
            if (!outgoing.ContainsKey(dep.FromRelease.ExternalId))
                outgoing[dep.FromRelease.ExternalId] = [];
            if (!incoming.ContainsKey(dep.ToRelease.ExternalId))
                incoming[dep.ToRelease.ExternalId] = [];

            outgoing[dep.FromRelease.ExternalId].Add(new ShardEdgeRef
                { RelId = relId, RelType = "DEPENDS_ON", NeighborNodeId = dep.ToRelease.ExternalId, Direction = "out" });
            incoming[dep.ToRelease.ExternalId].Add(new ShardEdgeRef
                { RelId = relId, RelType = "DEPENDS_ON", NeighborNodeId = dep.FromRelease.ExternalId, Direction = "in" });

            return new RelShardRow
            {
                RelId        = relId,
                SourceNodeId = dep.FromRelease.ExternalId,
                TargetNodeId = dep.ToRelease.ExternalId,
                Properties   = new Dictionary<string, object?> { ["dep_type"] = dep.DepType }
            };
        }).ToList();
        if (depRelRows.Count > 0)
            relTables["DEPENDS_ON"] = new RelShardTable { RelType = "DEPENDS_ON", Rows = depRelRows };

        // AFFECTS edges
        var affectsRelRows = snap.VulnAffects.Select(va => new RelShardRow
        {
            RelId        = $"affects-{va.Id}",
            SourceNodeId = va.Vulnerability.ExternalId,
            TargetNodeId = va.Release.ExternalId,
            Properties   = new Dictionary<string, object?> { }
        }).ToList();
        if (affectsRelRows.Count > 0)
            relTables["AFFECTS"] = new RelShardTable { RelType = "AFFECTS", Rows = affectsRelRows };

        return GraphShardNormalizer.Normalize(new GraphShard
        {
            FormatVersion     = GraphShard.CurrentFormatVersion,
            ExtractorVersion  = GraphShard.CurrentExtractorVersion,
            GraphVersionToken = $"snapshot-{snap.Label}",
            ExtractedAtUtc    = snap.TakenAt.ToString("O",
                System.Globalization.CultureInfo.InvariantCulture),
            ExtractionPolicy  = "ef-snapshot",
            IsComplete        = true,
            NodeTables        = nodeTables,
            RelTables         = relTables,
            Adjacency         = new GraphShardAdjacency
            {
                Outgoing = outgoing,
                Incoming = incoming,
            },
            SeedProvenance    = new GraphShardSeedProvenance
            {
                RequestedCount = snap.Entries.Count,
                IncludedCount  = snap.Entries.Count,
            },
            Boundary          = new GraphShardBoundary(),
            Options           = new GraphShardExtractionOptions
            {
                IncludeOutgoing = true, IncludeIncoming = true,
                IncludeNodeProperties = true, IncludeRelProperties = true,
                IncludeAdjacency = true,
            },
            Metadata = new Dictionary<string, object?>
            {
                ["snapshotLabel"] = snap.Label,
                ["packageCount"]  = pkgRows.Count,
            }
        });
    }

    // ── Mapping helpers ───────────────────────────────────────────────────────

    private static SnapshotSummary BuildSnapshotSummary(EfSnapshot s) => new(
        s.Id, s.Label, s.TakenAt,
        s.Entries.Select(e => e.Release.PackageId).Distinct().Count(),
        s.Dependencies.Count,
        s.VulnAffects.Select(v => v.VulnerabilityId).Distinct().Count());

    private static List<TopChangedRow> MapNodeDelta(
        IEnumerable<GraphShardProjectionNodeDeltaRow> rows)
        => rows.Select(r => new TopChangedRow(
            r.TableName, r.AddedCount, r.RemovedCount, r.NetDelta)).ToList();

    private static List<TopChangedRow> MapRelDelta(
        IEnumerable<GraphShardProjectionRelationshipDeltaRow> rows)
        => rows.Select(r => new TopChangedRow(
            r.RelType, r.AddedCount, r.RemovedCount, r.NetDelta)).ToList();

    private static List<TimeBucketRow> MapYearHistogram(GraphShardHistogramResult result)
        => result.Buckets
            .Select(b => new TimeBucketRow(((long)b.StartInclusive).ToString(), b.Count))
            .ToList();
}
