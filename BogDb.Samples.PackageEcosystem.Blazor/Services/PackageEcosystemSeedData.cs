namespace BogDb.Samples.PackageEcosystem.Blazor.Services;

/// <summary>
/// Synthetic seed data: 4 quarterly snapshots, 38 packages across npm/PyPI/NuGet/Maven,
/// 6 known vulnerabilities, and evolving dependency graphs to make change-review meaningful.
/// </summary>
public static class PackageEcosystemSeedData
{
    public static void Seed(PackageEcosystemDbContext ctx)
    {
        if (ctx.Snapshots.Any()) return;

        // ── Packages ────────────────────────────────────────────────────────
        var pkgs = new[]
        {
            // npm
            P("pkg-react",          "react",           "npm", "Facebook", "UI library — component model for declarative web UIs"),
            P("pkg-lodash",         "lodash",          "npm", "John-Henry Daher", "Utility library — functional helpers for JS"),
            P("pkg-axios",          "axios",           "npm", "Matt Zabriskie", "HTTP client for browser and Node"),
            P("pkg-express",        "express",         "npm", "TJ Holowaychuk", "Fast, minimal web framework for Node.js"),
            P("pkg-webpack",        "webpack",         "npm", "Tobias Koppers", "Module bundler for modern JS applications"),
            P("pkg-babel-core",     "babel-core",      "npm", "Sebastian McKenzie", "JS compiler — next-gen syntax today"),
            P("pkg-chalk",          "chalk",           "npm", "Sindre Sorhus", "Terminal string styling"),
            P("pkg-moment",         "moment",          "npm", "Tim Wood", "Date manipulation library"),
            P("pkg-dayjs",          "dayjs",           "npm", "iamkun", "Fast 2kB alternative to Moment.js"),
            P("pkg-uuid",           "uuid",            "npm", "Robert Kieffer", "RFC-compliant UUID generation"),
            // PyPI
            P("pkg-requests",       "requests",        "pypi", "Kenneth Reitz", "Simple HTTP library for Python"),
            P("pkg-numpy",          "numpy",           "pypi", "NumPy Steering", "Fundamental package for scientific computing"),
            P("pkg-pandas",         "pandas",          "pypi", "Wes McKinney", "Data analysis and manipulation library"),
            P("pkg-flask",          "flask",           "pypi", "Armin Ronacher", "Lightweight WSGI web application framework"),
            P("pkg-django",         "django",          "pypi", "Django Software Foundation", "High-level Python web framework"),
            P("pkg-pillow",         "pillow",          "pypi", "Alex Clark", "Python Imaging Library fork"),
            P("pkg-sqlalchemy",     "sqlalchemy",      "pypi", "Mike Bayer", "SQL toolkit and Object Relational Mapper"),
            P("pkg-celery",         "celery",          "pypi", "Ask Solem", "Distributed task queue"),
            P("pkg-pydantic",       "pydantic",        "pypi", "Samuel Colvin", "Data validation using Python type hints"),
            P("pkg-httpx",          "httpx",           "pypi", "Tom Christie", "A next-gen HTTP client for Python"),
            // NuGet
            P("pkg-newtonsoft",     "Newtonsoft.Json", "nuget", "James Newton-King", "Popular high-performance JSON framework"),
            P("pkg-serilog",        "Serilog",         "nuget", "Nicholas Blumhardt", "Structured logging for .NET"),
            P("pkg-polly",          "Polly",           "nuget", "App vNext", "Resilience and transient-fault-handling library"),
            P("pkg-autofac",        "Autofac",         "nuget", "Autofac Contributors", "Inversion of control container for .NET"),
            P("pkg-dapper",         "Dapper",          "nuget", "Marc Gravell", "Simple object mapper for .NET"),
            P("pkg-fluentvalidation","FluentValidation","nuget", "Jeremy Skinner", "Rule-based validation library"),
            P("pkg-mediatr",        "MediatR",         "nuget", "Jimmy Bogard", "Mediator implementation in .NET"),
            P("pkg-nunit",          "NUnit",           "nuget", "NUnit Project", "Unit-testing framework for .NET"),
            P("pkg-moq",            "Moq",             "nuget", "Castle Project", "Popular mocking library for .NET"),
            // Maven
            P("pkg-guava",          "guava",           "maven", "Google", "Google core Java libraries"),
            P("pkg-log4j-core",     "log4j-core",      "maven", "Apache", "Java logging framework — log4j2 core"),
            P("pkg-slf4j",          "slf4j-api",       "maven", "QOS.ch", "Logging facade for Java"),
            P("pkg-jackson",        "jackson-databind", "maven", "FasterXML", "JSON processing library for Java"),
            P("pkg-spring-core",    "spring-core",     "maven", "Pivotal", "Core container for the Spring Framework"),
            P("pkg-spring-boot",    "spring-boot",     "maven", "Pivotal", "Convention-over-configuration Spring launcher"),
            P("pkg-junit",          "junit-jupiter",   "maven", "JUnit", "Unit testing framework for Java"),
            P("pkg-hibernate",      "hibernate-core",  "maven", "Red Hat", "ORM framework for Java"),
            P("pkg-commons-lang",   "commons-lang3",   "maven", "Apache", "Extra functionality for java.lang classes"),
        };
        ctx.Packages.AddRange(pkgs);

        // ── Vulnerabilities ─────────────────────────────────────────────────
        var vulns = new[]
        {
            V("vuln-log4shell",    "CVE-2021-44228", "Log4Shell — RCE in log4j-core JNDI lookup",
              "CRITICAL", 10.0),
            V("vuln-lodash-proto", "CVE-2019-10744", "Lodash prototype pollution via _.defaultsDeep",
              "HIGH", 9.1),
            V("vuln-jackson-poly", "CVE-2020-36518", "Jackson Databind stack overflow via deeply nested arrays",
              "HIGH", 7.5),
            V("vuln-pillow-oob",  "CVE-2021-25293", "Pillow out-of-bounds read in SGI RLE image decoder",
              "HIGH", 9.1),
            V("vuln-django-open", "CVE-2021-45115", "Django open redirect via malformed URL path",
              "MEDIUM", 5.3),
            V("vuln-axios-ssrf",  "CVE-2023-45857", "Axios XSRF header exposure via cross-site request",
              "MEDIUM", 6.5),
        };
        ctx.Vulnerabilities.AddRange(vulns);
        ctx.SaveChanges();

        // ── Releases per snapshot ────────────────────────────────────────────
        // 4 snapshots: Q1-2024, Q2-2024, Q3-2024, Q4-2024
        // Each snapshot adds/drops releases to make change-review interesting.

        var snapQ1 = Snap("Q1-2024", new DateTime(2024, 3, 31));
        var snapQ2 = Snap("Q2-2024", new DateTime(2024, 6, 30));
        var snapQ3 = Snap("Q3-2024", new DateTime(2024, 9, 30));
        var snapQ4 = Snap("Q4-2024", new DateTime(2024, 12, 31));
        ctx.Snapshots.AddRange(snapQ1, snapQ2, snapQ3, snapQ4);
        ctx.SaveChanges();

        var lookup = ctx.Packages.ToDictionary(p => p.ExternalId);

        // Helper: release within snapshot
        void AddReleases(EfSnapshot snap, (string pkgId, string ver, int year, int dl, bool yanked)[] releases)
        {
            foreach (var (pkgId, ver, year, dl, yanked) in releases)
            {
                var rel = new EfPackageRelease
                {
                    ExternalId  = $"{pkgId}-{ver.Replace('.', '_')}-{snap.Label}",
                    PackageId   = lookup[pkgId].Id,
                    Version     = ver,
                    ReleaseYear = year,
                    Downloads   = dl,
                    IsYanked    = yanked
                };
                ctx.Releases.Add(rel);
                ctx.SaveChanges();
                ctx.SnapshotEntries.Add(new EfSnapshotEntry { SnapshotId = snap.Id, ReleaseId = rel.Id });
            }
            ctx.SaveChanges();
        }

        // Q1 — stable baseline
        AddReleases(snapQ1, [
            ("pkg-react",          "18.2.0", 2022, 42000000, false),
            ("pkg-lodash",         "4.17.21",2021, 25000000, false),
            ("pkg-axios",          "1.6.2",  2023, 18000000, false),
            ("pkg-express",        "4.18.2", 2022, 15000000, false),
            ("pkg-webpack",        "5.89.0", 2023, 12000000, false),
            ("pkg-babel-core",     "7.23.0", 2023, 10000000, false),
            ("pkg-chalk",          "5.3.0",  2023,  8000000, false),
            ("pkg-moment",         "2.29.4", 2022,  7000000, false),
            ("pkg-uuid",           "9.0.0",  2023,  6000000, false),
            ("pkg-requests",       "2.31.0", 2023, 22000000, false),
            ("pkg-numpy",          "1.26.0", 2023, 20000000, false),
            ("pkg-pandas",         "2.1.0",  2023, 18000000, false),
            ("pkg-flask",          "3.0.0",  2023, 12000000, false),
            ("pkg-pillow",         "10.1.0", 2023, 10000000, false),
            ("pkg-sqlalchemy",     "2.0.23", 2023,  9000000, false),
            ("pkg-celery",         "5.3.4",  2023,  7000000, false),
            ("pkg-pydantic",       "2.5.0",  2023,  8000000, false),
            ("pkg-newtonsoft",     "13.0.3", 2022, 30000000, false),
            ("pkg-serilog",        "3.1.1",  2023, 15000000, false),
            ("pkg-polly",          "8.2.0",  2023, 12000000, false),
            ("pkg-dapper",         "2.1.15", 2023, 10000000, false),
            ("pkg-fluentvalidation","11.8.0",2023,  9000000, false),
            ("pkg-nunit",          "4.0.1",  2024,  8000000, false),
            ("pkg-log4j-core",     "2.20.0", 2023, 11000000, false),
            ("pkg-slf4j",          "2.0.9",  2023,  9000000, false),
            ("pkg-jackson",        "2.16.0", 2023, 14000000, false),
            ("pkg-spring-core",    "6.1.2",  2023, 12000000, false),
            ("pkg-spring-boot",    "3.2.1",  2024, 10000000, false),
            ("pkg-junit",          "5.10.1", 2023,  8000000, false),
            ("pkg-commons-lang",   "3.14.0", 2023,  7000000, false),
        ]);

        // Q2 — moment replaced by dayjs; django added; httpx added; guava added
        AddReleases(snapQ2, [
            ("pkg-react",          "18.3.0", 2024, 45000000, false),
            ("pkg-lodash",         "4.17.21",2021, 23000000, false),
            ("pkg-axios",          "1.6.8",  2024, 19000000, false),
            ("pkg-express",        "4.19.0", 2024, 15500000, false),
            ("pkg-webpack",        "5.91.0", 2024, 12500000, false),
            ("pkg-babel-core",     "7.24.0", 2024, 10200000, false),
            ("pkg-chalk",          "5.3.0",  2023,  8100000, false),
            ("pkg-dayjs",          "1.11.10",2023,  5000000, false),  // ← new (replaces moment)
            ("pkg-uuid",           "9.0.1",  2024,  6200000, false),
            ("pkg-requests",       "2.31.0", 2023, 23000000, false),
            ("pkg-numpy",          "1.26.4", 2024, 21000000, false),
            ("pkg-pandas",         "2.2.0",  2024, 19000000, false),
            ("pkg-flask",          "3.0.2",  2024, 12500000, false),
            ("pkg-django",         "5.0.2",  2024, 11000000, false),  // ← new
            ("pkg-pillow",         "10.2.0", 2024, 10500000, false),
            ("pkg-sqlalchemy",     "2.0.28", 2024,  9500000, false),
            ("pkg-celery",         "5.3.6",  2024,  7200000, false),
            ("pkg-pydantic",       "2.6.0",  2024,  8500000, false),
            ("pkg-httpx",          "0.27.0", 2024,  4000000, false),  // ← new
            ("pkg-newtonsoft",     "13.0.3", 2022, 29000000, false),
            ("pkg-serilog",        "3.1.1",  2023, 15500000, false),
            ("pkg-polly",          "8.3.0",  2024, 12800000, false),
            ("pkg-dapper",         "2.1.28", 2024, 10300000, false),
            ("pkg-fluentvalidation","11.9.0",2024,  9200000, false),
            ("pkg-mediatr",        "12.2.0", 2024,  7000000, false),  // ← new
            ("pkg-nunit",          "4.0.1",  2024,  8000000, false),
            ("pkg-log4j-core",     "2.23.0", 2024, 11500000, false),
            ("pkg-slf4j",          "2.0.12", 2024,  9200000, false),
            ("pkg-jackson",        "2.17.0", 2024, 14500000, false),
            ("pkg-spring-core",    "6.1.6",  2024, 12500000, false),
            ("pkg-spring-boot",    "3.2.4",  2024, 10500000, false),
            ("pkg-junit",          "5.10.2", 2024,  8200000, false),
            ("pkg-hibernate",      "6.4.4",  2024,  6500000, false),  // ← new
            ("pkg-commons-lang",   "3.14.0", 2023,  7100000, false),
            ("pkg-guava",          "33.0.0", 2024,  9000000, false),  // ← new
        ]);

        // Q3 — moment fully removed; moq added; autofac added; babel-core slightly drops
        AddReleases(snapQ3, [
            ("pkg-react",          "18.3.1", 2024, 47000000, false),
            ("pkg-lodash",         "4.17.21",2021, 22000000, false),
            ("pkg-axios",          "1.7.2",  2024, 20000000, false),
            ("pkg-express",        "4.19.2", 2024, 16000000, false),
            ("pkg-webpack",        "5.93.0", 2024, 13000000, false),
            // babel-core remains but smaller
            ("pkg-babel-core",     "7.24.7", 2024,  9000000, false),
            ("pkg-chalk",          "5.3.0",  2023,  8200000, false),
            ("pkg-dayjs",          "1.11.11",2024,  7000000, false),
            ("pkg-uuid",           "10.0.0", 2024,  6500000, false),
            ("pkg-requests",       "2.32.0", 2024, 24000000, false),
            ("pkg-numpy",          "2.0.0",  2024, 22000000, false),
            ("pkg-pandas",         "2.2.2",  2024, 20000000, false),
            ("pkg-flask",          "3.0.3",  2024, 13000000, false),
            ("pkg-django",         "5.0.6",  2024, 11500000, false),
            ("pkg-pillow",         "10.4.0", 2024, 11000000, false),
            ("pkg-sqlalchemy",     "2.0.31", 2024, 10000000, false),
            ("pkg-celery",         "5.4.0",  2024,  7500000, false),
            ("pkg-pydantic",       "2.8.0",  2024,  9000000, false),
            ("pkg-httpx",          "0.27.2", 2024,  5000000, false),
            ("pkg-newtonsoft",     "13.0.3", 2022, 28000000, false),
            ("pkg-serilog",        "4.0.0",  2024, 16000000, false),
            ("pkg-polly",          "8.4.0",  2024, 13200000, false),
            ("pkg-autofac",        "8.0.0",  2024,  5500000, false),  // ← new
            ("pkg-dapper",         "2.1.35", 2024, 10600000, false),
            ("pkg-fluentvalidation","11.9.2",2024,  9400000, false),
            ("pkg-mediatr",        "12.3.0", 2024,  7500000, false),
            ("pkg-nunit",          "4.1.0",  2024,  8300000, false),
            ("pkg-moq",            "4.20.70",2024,  6000000, false),  // ← new
            ("pkg-log4j-core",     "2.23.1", 2024, 12000000, false),
            ("pkg-slf4j",          "2.0.13", 2024,  9500000, false),
            ("pkg-jackson",        "2.17.2", 2024, 15000000, false),
            ("pkg-spring-core",    "6.1.10", 2024, 13000000, false),
            ("pkg-spring-boot",    "3.3.2",  2024, 11000000, false),
            ("pkg-junit",          "5.10.3", 2024,  8500000, false),
            ("pkg-hibernate",      "6.5.2",  2024,  6800000, false),
            ("pkg-commons-lang",   "3.14.0", 2023,  7300000, false),
            ("pkg-guava",          "33.2.1", 2024,  9400000, false),
        ]);

        // Q4 — lodash yanked version appears; numpy 2.x growth; new vuln on axios
        AddReleases(snapQ4, [
            ("pkg-react",          "19.0.0", 2024, 50000000, false),
            ("pkg-lodash",         "4.17.20",2018,  5000000, true),   // ← yanked old version
            ("pkg-lodash",         "4.17.21",2021, 21000000, false),
            ("pkg-axios",          "1.7.7",  2024, 21000000, false),
            ("pkg-express",        "5.0.0",  2024, 17000000, false),
            ("pkg-webpack",        "5.96.0", 2024, 13500000, false),
            ("pkg-babel-core",     "7.26.0", 2024,  8500000, false),
            ("pkg-chalk",          "5.4.0",  2024,  8400000, false),
            ("pkg-dayjs",          "1.11.13",2024,  9000000, false),
            ("pkg-uuid",           "10.0.0", 2024,  6900000, false),
            ("pkg-requests",       "2.32.3", 2024, 25000000, false),
            ("pkg-numpy",          "2.1.0",  2024, 24000000, false),
            ("pkg-pandas",         "2.2.3",  2024, 21000000, false),
            ("pkg-flask",          "3.1.0",  2024, 14000000, false),
            ("pkg-django",         "5.1.4",  2024, 12000000, false),
            ("pkg-pillow",         "11.0.0", 2024, 11500000, false),
            ("pkg-sqlalchemy",     "2.0.36", 2024, 10500000, false),
            ("pkg-celery",         "5.4.0",  2024,  7700000, false),
            ("pkg-pydantic",       "2.9.2",  2024,  9500000, false),
            ("pkg-httpx",          "0.28.0", 2024,  6000000, false),
            ("pkg-newtonsoft",     "13.0.3", 2022, 27000000, false),
            ("pkg-serilog",        "4.1.0",  2024, 17000000, false),
            ("pkg-polly",          "8.5.0",  2024, 13800000, false),
            ("pkg-autofac",        "8.1.0",  2024,  5800000, false),
            ("pkg-dapper",         "2.1.35", 2024, 10800000, false),
            ("pkg-fluentvalidation","11.11.0",2024, 9800000, false),
            ("pkg-mediatr",        "12.4.0", 2024,  7900000, false),
            ("pkg-nunit",          "4.2.2",  2024,  8700000, false),
            ("pkg-moq",            "4.20.72",2024,  6200000, false),
            ("pkg-log4j-core",     "2.24.1", 2024, 12500000, false),
            ("pkg-slf4j",          "2.0.16", 2024,  9800000, false),
            ("pkg-jackson",        "2.18.2", 2024, 15500000, false),
            ("pkg-spring-core",    "6.2.0",  2024, 13500000, false),
            ("pkg-spring-boot",    "3.4.0",  2024, 11500000, false),
            ("pkg-junit",          "5.11.3", 2024,  8900000, false),
            ("pkg-hibernate",      "6.6.2",  2024,  7100000, false),
            ("pkg-commons-lang",   "3.17.0", 2024,  7600000, false),
            ("pkg-guava",          "33.3.1", 2024,  9800000, false),
        ]);

        // ── Dependencies (selected key chains to make graph interesting) ─────
        // Helper: find a release in a snapshot via the join table
        EfPackageRelease? FindRelease(EfSnapshot snap, string pkgExtIdPrefix)
        {
            var entryReleaseIds = ctx.SnapshotEntries
                .Where(e => e.SnapshotId == snap.Id)
                .Select(e => e.ReleaseId)
                .ToHashSet();
            return ctx.Releases.FirstOrDefault(r =>
                entryReleaseIds.Contains(r.Id) && r.ExternalId.StartsWith(pkgExtIdPrefix));
        }

        void AddDep(EfSnapshot snap, string fromExtId, string toExtId, string depType = "direct")
        {
            var from = FindRelease(snap, fromExtId);
            var to   = FindRelease(snap, toExtId);
            if (from is null || to is null) return;
            if (ctx.SnapshotDeps.Any(d => d.SnapshotId == snap.Id
                                       && d.FromReleaseId == from.Id
                                       && d.ToReleaseId   == to.Id)) return;
            ctx.SnapshotDeps.Add(new EfSnapshotDep
            {
                SnapshotId    = snap.Id,
                FromReleaseId = from.Id,
                ToReleaseId   = to.Id,
                DepType       = depType
            });
        }

        foreach (var snap in new[] { snapQ1, snapQ2, snapQ3, snapQ4 })
        {
            // spring-boot → spring-core → slf4j → log4j-core (the notorious chain)
            AddDep(snap, "pkg-spring-boot", "pkg-spring-core");
            AddDep(snap, "pkg-spring-core", "pkg-slf4j");
            AddDep(snap, "pkg-slf4j",       "pkg-log4j-core");
            // spring-boot → jackson
            AddDep(snap, "pkg-spring-boot", "pkg-jackson");
            // spring-boot → hibernate (only Q2 onwards)
            if (snap.Label != "Q1-2024") AddDep(snap, "pkg-spring-boot", "pkg-hibernate");
            // django → sqlalchemy (dev)
            if (snap.Label != "Q1-2024") AddDep(snap, "pkg-django", "pkg-sqlalchemy", "dev");
            // flask → pydantic
            AddDep(snap, "pkg-flask", "pkg-pydantic");
            // react → axios
            AddDep(snap, "pkg-react", "pkg-axios");
            // react → webpack (dev) 
            AddDep(snap, "pkg-react", "pkg-webpack", "dev");
            // express → chalk
            AddDep(snap, "pkg-express", "pkg-chalk");
            // celery → redis-compat via commons (placeholder)
            AddDep(snap, "pkg-celery", "pkg-requests");
            // guava → commons-lang (only Q2 onwards)
            if (snap.Label != "Q1-2024") AddDep(snap, "pkg-guava", "pkg-commons-lang");
            // jackson → slf4j
            AddDep(snap, "pkg-jackson", "pkg-slf4j");
        }
        ctx.SaveChanges();

        // ── Vuln affects (snapshot-scoped) ───────────────────────────────────
        void AddVuln(EfSnapshot snap, string vulnExtId, string relPrefix)
        {
            var vuln = ctx.Vulnerabilities.First(v => v.ExternalId == vulnExtId);
            var rel  = FindRelease(snap, relPrefix);
            if (rel is null) return;
            if (ctx.VulnAffects.Any(v => v.SnapshotId == snap.Id
                                      && v.VulnerabilityId == vuln.Id
                                      && v.ReleaseId == rel.Id)) return;
            ctx.VulnAffects.Add(new EfSnapVulnAffects
            {
                SnapshotId      = snap.Id,
                VulnerabilityId = vuln.Id,
                ReleaseId       = rel.Id
            });
        }

        // Log4Shell affects log4j-core in Q1 only (fixed in Q2 version)
        AddVuln(snapQ1, "vuln-log4shell",    "pkg-log4j-core");
        // Lodash proto pollution — all quarters (unfixed old version yanked in Q4)
        foreach (var s in new[] { snapQ1, snapQ2, snapQ3, snapQ4 })
            AddVuln(s, "vuln-lodash-proto", "pkg-lodash");
        // Jackson — Q1 and Q2
        AddVuln(snapQ1, "vuln-jackson-poly", "pkg-jackson");
        AddVuln(snapQ2, "vuln-jackson-poly", "pkg-jackson");
        // Pillow — Q1 only
        AddVuln(snapQ1, "vuln-pillow-oob",   "pkg-pillow");
        // Django open redirect — Q2 and Q3
        AddVuln(snapQ2, "vuln-django-open",  "pkg-django");
        AddVuln(snapQ3, "vuln-django-open",  "pkg-django");
        // Axios SSRF — Q3 and Q4
        AddVuln(snapQ3, "vuln-axios-ssrf",   "pkg-axios");
        AddVuln(snapQ4, "vuln-axios-ssrf",   "pkg-axios");

        ctx.SaveChanges();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static EfPackage P(string extId, string name, string eco, string author, string desc)
        => new() { ExternalId = extId, Name = name, Ecosystem = eco, Author = author, Description = desc };

    private static EfVulnerability V(string extId, string cve, string title, string sev, double score)
        => new() { ExternalId = extId, CveId = cve, Title = title, Severity = sev, CvssScore = score };

    private static EfSnapshot Snap(string label, DateTime at)
        => new() { Label = label, TakenAt = at };
}
