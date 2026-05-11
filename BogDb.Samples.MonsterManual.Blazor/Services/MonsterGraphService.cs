using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using BogDb.Core.Common;
using BogDb.Core.Main;

namespace BogDb.Samples.MonsterManual.Blazor.Services;

public record QueryResponse(bool IsSuccess, string Error, List<string> Columns, List<Dictionary<string, object?>> Rows, long ElapsedMs);

public class MonsterGraphService
{
    private readonly BogDatabase _db;
    private readonly BogConnection _conn;
    private readonly IHttpClientFactory _httpFactory;
    private bool _schemaInitialized;
    private bool _isBuilding;

    public bool IsBuilding => _isBuilding;

    public MonsterGraphService(IHttpClientFactory httpFactory)
    {
        _db = BogDatabase.CreateInMemory();
        _conn = new BogConnection(_db);
        _httpFactory = httpFactory;
    }

    // ── Public API ───────────────────────────────────────────────────────────

    public QueryResponse Execute(string cypher)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = _conn.Query(cypher);
        sw.Stop();

        if (!r.IsSuccess)
            return new QueryResponse(false, r.ErrorMessage ?? "Query failed", [], [], sw.ElapsedMilliseconds);

        var cols = r.ColumnNames.ToList();
        var rows = new List<Dictionary<string, object?>>();
        while (r.HasNext())
        {
            var row = r.GetNext();
            rows.Add(row.GetAsDictionary().ToDictionary(kv => kv.Key, kv => (object?)kv.Value));
        }
        return new QueryResponse(true, string.Empty, cols, rows, sw.ElapsedMilliseconds);
    }

    public (int Monsters, int Dungeons, int Connections) GetStats()
    {
        if (!_schemaInitialized) return (0, 0, 0);
        try {
            var m = Execute("MATCH (m:Monster) RETURN count(m) AS c");
            var d = Execute("MATCH (d:Dungeon) RETURN count(d) AS c");
            var c = Execute("MATCH ()-[r:IN_DUNGEON]->() RETURN count(r) AS c");

            int mCount = m.IsSuccess && m.Rows.Count > 0 ? Convert.ToInt32(m.Rows[0]["c"]) : 0;
            int dCount = d.IsSuccess && d.Rows.Count > 0 ? Convert.ToInt32(d.Rows[0]["c"]) : 0;
            int cCount = c.IsSuccess && c.Rows.Count > 0 ? Convert.ToInt32(c.Rows[0]["c"]) : 0;

            return (mCount, dCount, cCount);
        } catch {
            return (0, 0, 0);
        }
    }

    // ── Schema Setup ─────────────────────────────────────────────────────────

    private void SetupSchema()
    {
        if (_schemaInitialized) return;
        _conn.BeginWriteTransaction();

        // Monster Node
        _conn.EnsureNodeTable("Monster", new()
        {
            ["id"] = LogicalTypeID.STRING,
            ["name"] = LogicalTypeID.STRING,
            ["size"] = LogicalTypeID.STRING,
            ["type"] = LogicalTypeID.STRING,
            ["alignment"] = LogicalTypeID.STRING,
            ["ac"] = LogicalTypeID.INT32,
            ["hp"] = LogicalTypeID.INT32,
            ["cr"] = LogicalTypeID.STRING,
            ["xp"] = LogicalTypeID.INT32,
            ["str"] = LogicalTypeID.INT32,
            ["dex"] = LogicalTypeID.INT32,
            ["con"] = LogicalTypeID.INT32,
            ["intel"] = LogicalTypeID.INT32,  // 'int' is a keyword, use 'intel'
            ["wis"] = LogicalTypeID.INT32,
            ["cha"] = LogicalTypeID.INT32,
            ["image"] = LogicalTypeID.STRING
        });

        // Dungeon Node
        _conn.EnsureNodeTable("Dungeon", new()
        {
            ["id"] = LogicalTypeID.STRING,
            ["name"] = LogicalTypeID.STRING
        });

        // Add to Dungeon Edge
        _conn.EnsureRelTable("IN_DUNGEON", "Monster", "Dungeon", new());

        _conn.Commit();
        _schemaInitialized = true;
    }

    // ── Build Database from GitHub ──────────────────────────────────────────

    public async Task BuildDatabaseAsync(Action<int, int> progressCallback)
    {
        if (_isBuilding) return;
        _isBuilding = true;
        SetupSchema();

        // Use a short-lived HttpClient from the factory (safe inside a singleton)
        var http = _httpFactory.CreateClient("MonsterApiClient");

        var files = new List<GitHubContent>();
        try
        {
            var apiUrl = "https://api.github.com/repos/theoperatore/dnd-monster-api/contents/src/db/data";
            files = await http.GetFromJsonAsync<List<GitHubContent>>(apiUrl) ?? new();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"GitHub directory API failed ({ex.Message}), using hardcoded fallback.");
            // Hardcoded fallback list (covers GitHub rate-limit scenarios)
            var names = new[] {
                "0-aarakocra","1-abjurer","10-adult_brass_dragon","100-black_guard_drake",
                "101-black_pudding","106-blood_hawk","116-bulette","2-aboleth",
                "20-air_elemental","200-drow_elite_warrior","21-allosaurus","24-androsphinx",
                "3-abominable_yeti","30-archmage","40-awakened_shrub","50-banderhobb",
                "60-banshee","70-basilisk","80-behir","90-berserker"
            };
            foreach (var n in names)
                files.Add(new GitHubContent {
                    name = n + ".json",
                    download_url = $"https://raw.githubusercontent.com/theoperatore/dnd-monster-api/master/src/db/data/{n}.json"
                });
        }

        if (files == null || files.Count == 0) { _isBuilding = false; return; }

        // Limit to 40 monsters for the sample to prevent extreme rate limiting/wait times
        var targetFiles = files.Where(f => f.name.EndsWith(".json")).Take(40).ToList();
        
        int processed = 0;
        progressCallback(processed, targetFiles.Count);

        _conn.BeginWriteTransaction();

        foreach (var file in targetFiles)
        {
            try
            {
                var jsonStr = await http.GetStringAsync(file.download_url);
                var monster = JsonSerializer.Deserialize<MonsterModel>(jsonStr,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (monster != null)
                    IngestMonster(monster);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching {file.name}: {ex.Message}");
            }

            processed++;
            progressCallback(processed, targetFiles.Count);

            // Small delay to be a good GitHub API citizen
            await Task.Delay(100);
        }

        // Seed a default dungeon
        _conn.UpsertNodeById("Dungeon", "dungeon-1", new() { ["id"] = "dungeon-1", ["name"] = "Tomb of Horrors" });

        _conn.Commit();
        _isBuilding = false;
    }

    private void IngestMonster(MonsterModel m)
    {
        var id = m.name?.Replace(" ", "-").ToLower() ?? Guid.NewGuid().ToString();
        var ac = ParseInt(m.armorClass);
        var hp = m.hitPoints?.average ?? 0;
        
        // Example "1/4 (50 XP)" => extract 50.
        int xp = ExtractXp(m.challengeRating);

        _conn.UpsertNodeById("Monster", id, new()
        {
            ["id"] = id,
            ["name"] = m.name ?? "Unknown",
            ["size"] = m.size ?? "",
            ["type"] = m.type ?? m.race ?? "",
            ["alignment"] = m.alignment ?? "",
            ["ac"] = ac,
            ["hp"] = hp,
            ["cr"] = m.challengeRating ?? "0",
            ["xp"] = xp,
            ["str"] = m.abilityScores?.STR?.score ?? 10,
            ["dex"] = m.abilityScores?.DEX?.score ?? 10,
            ["con"] = m.abilityScores?.CON?.score ?? 10,
            ["intel"] = m.abilityScores?.INT?.score ?? 10,
            ["wis"] = m.abilityScores?.WIS?.score ?? 10,
            ["cha"] = m.abilityScores?.CHA?.score ?? 10,
            ["image"] = m.image ?? ""
        });
    }

    private int ParseInt(string? val)
    {
        if (string.IsNullOrWhiteSpace(val)) return 0;
        var digits = new string(val.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out int x) ? x : 0;
    }

    private int ExtractXp(string? cr)
    {
        if (string.IsNullOrWhiteSpace(cr)) return 0;
        var match = System.Text.RegularExpressions.Regex.Match(cr, @"\((\d+)\s*XP\)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out int xp))
            return xp;
        return 0;
    }

    public void AddToDungeon(string monsterId, string dungeonId)
    {
        _conn.BeginWriteTransaction();
        _conn.UpsertRelationshipById("IN_DUNGEON", monsterId, dungeonId, new());
        _conn.Commit();
    }
    
    public void RemoveFromDungeon(string monsterId, string dungeonId)
    {
        // For sample simplicity, we can just delete all IN_DUNGEON relations for this monster, or use Cypher
        _conn.Query($"MATCH (m:Monster {{id: '{monsterId}'}})-[r:IN_DUNGEON]->(d:Dungeon {{id: '{dungeonId}'}}) DELETE r");
    }
}

// ── Helpers ─────────────────────────────────────────────────────────────

public class GitHubContent {
    public string name { get; set; } = "";
    public string download_url { get; set; } = "";
}

public class MonsterModel {
    public string? name { get; set; }
    public string? size { get; set; }
    public string? type { get; set; }
    public string? race { get; set; }
    public string? alignment { get; set; }
    public string? armorClass { get; set; }
    public HitPoints? hitPoints { get; set; }
    public string? challengeRating { get; set; }
    // The actual stat-block field in the API is "abilityScores" (not "abilities")
    public AbilityScores? abilityScores { get; set; }
    public string? image { get; set; }
}

public class HitPoints {
    public int average { get; set; }
}

public class AbilityScores {
    public AbilityScore? STR { get; set; }
    public AbilityScore? DEX { get; set; }
    public AbilityScore? CON { get; set; }
    public AbilityScore? INT { get; set; }
    public AbilityScore? WIS { get; set; }
    public AbilityScore? CHA { get; set; }
}

public class AbilityScore {
    public int score { get; set; }
}
