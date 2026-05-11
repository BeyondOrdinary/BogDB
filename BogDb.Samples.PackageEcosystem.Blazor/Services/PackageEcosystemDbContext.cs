using Microsoft.EntityFrameworkCore;

namespace BogDb.Samples.PackageEcosystem.Blazor.Services;

// ── EF Entity Models ──────────────────────────────────────────────────────────

/// <summary>A published package in the ecosystem (npm / PyPI / NuGet / Maven).</summary>
public sealed class EfPackage
{
    public int    Id          { get; set; }
    public string ExternalId  { get; set; } = string.Empty;  // stable graph key
    public string Name        { get; set; } = string.Empty;
    public string Ecosystem   { get; set; } = string.Empty;  // npm | pypi | nuget | maven
    public string Description { get; set; } = string.Empty;
    public string Author      { get; set; } = string.Empty;

    public ICollection<EfPackageRelease> Releases { get; set; } = [];
}

/// <summary>A specific versioned release of a package.</summary>
public sealed class EfPackageRelease
{
    public int       Id          { get; set; }
    public string    ExternalId  { get; set; } = string.Empty;
    public int       PackageId   { get; set; }
    public EfPackage Package     { get; set; } = null!;
    public string    Version     { get; set; } = string.Empty;
    public int       ReleaseYear { get; set; }
    public int       Downloads   { get; set; }
    public bool      IsYanked    { get; set; }
}

/// <summary>A point-in-time snapshot of the ecosystem (e.g. "Q1 2025").</summary>
public sealed class EfSnapshot
{
    public int      Id    { get; set; }
    public string   Label { get; set; } = string.Empty;  // "Q1-2025"
    public DateTime TakenAt { get; set; }

    public ICollection<EfSnapshotEntry>    Entries        { get; set; } = [];
    public ICollection<EfSnapshotDep>      Dependencies   { get; set; } = [];
    public ICollection<EfSnapVulnAffects>  VulnAffects    { get; set; } = [];
}

/// <summary>Which package releases are active in a snapshot.</summary>
public sealed class EfSnapshotEntry
{
    public int              Id         { get; set; }
    public int              SnapshotId { get; set; }
    public EfSnapshot       Snapshot   { get; set; } = null!;
    public int              ReleaseId  { get; set; }
    public EfPackageRelease Release    { get; set; } = null!;
}

/// <summary>A dependency edge between two releases within a snapshot.</summary>
public sealed class EfSnapshotDep
{
    public int              Id           { get; set; }
    public int              SnapshotId   { get; set; }
    public EfSnapshot       Snapshot     { get; set; } = null!;
    public int              FromReleaseId { get; set; }
    public EfPackageRelease FromRelease  { get; set; } = null!;
    public int              ToReleaseId  { get; set; }
    public EfPackageRelease ToRelease    { get; set; } = null!;
    public string           DepType      { get; set; } = "direct";  // direct | dev | peer
}

/// <summary>A known vulnerability in the ecosystem.</summary>
public sealed class EfVulnerability
{
    public int    Id          { get; set; }
    public string ExternalId  { get; set; } = string.Empty;
    public string CveId       { get; set; } = string.Empty;
    public string Title       { get; set; } = string.Empty;
    public string Severity    { get; set; } = string.Empty;  // CRITICAL | HIGH | MEDIUM | LOW
    public double CvssScore   { get; set; }

    public ICollection<EfSnapVulnAffects> SnapVulnAffects { get; set; } = [];
}

/// <summary>Which releases in a given snapshot are affected by a vulnerability.</summary>
public sealed class EfSnapVulnAffects
{
    public int               Id             { get; set; }
    public int               SnapshotId     { get; set; }
    public EfSnapshot        Snapshot       { get; set; } = null!;
    public int               VulnerabilityId { get; set; }
    public EfVulnerability   Vulnerability  { get; set; } = null!;
    public int               ReleaseId      { get; set; }
    public EfPackageRelease  Release        { get; set; } = null!;
}

// ── DbContext ─────────────────────────────────────────────────────────────────

public sealed class PackageEcosystemDbContext(DbContextOptions<PackageEcosystemDbContext> options)
    : DbContext(options)
{
    public DbSet<EfPackage>         Packages        { get; set; }
    public DbSet<EfPackageRelease>  Releases        { get; set; }
    public DbSet<EfSnapshot>        Snapshots       { get; set; }
    public DbSet<EfSnapshotEntry>   SnapshotEntries { get; set; }
    public DbSet<EfSnapshotDep>     SnapshotDeps    { get; set; }
    public DbSet<EfVulnerability>   Vulnerabilities { get; set; }
    public DbSet<EfSnapVulnAffects> VulnAffects     { get; set; }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<EfSnapshotEntry>()
            .HasIndex(e => new { e.SnapshotId, e.ReleaseId }).IsUnique();

        mb.Entity<EfSnapshotDep>()
            .HasIndex(d => new { d.SnapshotId, d.FromReleaseId, d.ToReleaseId }).IsUnique();

        mb.Entity<EfSnapVulnAffects>()
            .HasIndex(v => new { v.SnapshotId, v.VulnerabilityId, v.ReleaseId }).IsUnique();
    }
}
