using System;
using System.Collections.Generic;
using System.Linq;
using BogDb.Core.Extension;
using BogDb.Core.Main;

namespace BogDb.Extensions.Algo;

/// <summary>
/// Graph algorithms extension — C++ parity with bogdb-master/extension/algo.
/// Surfaces Louvain community detection, SCC, K-Core decomposition,
/// and Spanning Forest as table functions, complementing the in-core GDS
/// (which already provides PageRank, WCC, SSSP, SSSP Path Reconstruction,
/// Delta-Stepping SSSP, K-Hop, and Variable-Length Path).
/// </summary>
public class AlgoExtension : IExtension
{
    public string Name => "algo";

    public void Load(BogDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        database.StandaloneTableFunctionRegistry.Register(new LouvainTableFunction(database));
        database.StandaloneTableFunctionRegistry.Register(new SccTableFunction(database));
        database.StandaloneTableFunctionRegistry.Register(new KCoreTableFunction(database));
        database.StandaloneTableFunctionRegistry.Register(new SpanningForestTableFunction(database));

        // Register aliases matching C++ naming
        database.StandaloneTableFunctionRegistry.Register(
            new AliasTableFunction("STRONGLY_CONNECTED_COMPONENTS", new SccTableFunction(database)));
        database.StandaloneTableFunctionRegistry.Register(
            new AliasTableFunction("K_CORE_DECOMPOSITION", new KCoreTableFunction(database)));
        database.StandaloneTableFunctionRegistry.Register(
            new AliasTableFunction("WEAKLY_CONNECTED_COMPONENTS", new WccAlgoTableFunction(database)));
    }
}

/// <summary>Simple alias wrapper for table functions.</summary>
internal sealed class AliasTableFunction : ITableFunction
{
    private readonly ITableFunction _inner;
    public string Name { get; }
    public IReadOnlyList<(string Name, string Type)>? Schema => _inner.Schema;

    public AliasTableFunction(string name, ITableFunction inner)
    {
        Name = name;
        _inner = inner;
    }

    public IEnumerable<Dictionary<string, object?>> Invoke(IReadOnlyList<object?> args)
        => _inner.Invoke(args);
}

// ── Louvain Community Detection ──────────────────────────────────────────────

internal sealed class LouvainTableFunction : ITableFunction
{
    private readonly BogDatabase _db;
    public LouvainTableFunction(BogDatabase db) { _db = db; }

    public string Name => "LOUVAIN";

    public IReadOnlyList<(string Name, string Type)>? Schema =>
        new[] { ("node_id", "INT64"), ("community_id", "INT64") };

    public IEnumerable<Dictionary<string, object?>> Invoke(IReadOnlyList<object?> args)
    {
        // args[0] = node table, args[1] = rel table
        if (args.Count < 2)
            throw new ArgumentException("LOUVAIN requires (node_table, rel_table) arguments.");

        var nodeTable = args[0]?.ToString() ?? throw new ArgumentException("Node table name required.");
        var relTable = args[1]?.ToString() ?? throw new ArgumentException("Rel table name required.");

        // Build adjacency from the graph
        using var conn = new BogConnection(_db);
        var adj = new Dictionary<long, HashSet<long>>();
        var allNodes = new HashSet<long>();

        // Get nodes
        var nodeResult = conn.Query($"MATCH (n:{nodeTable}) RETURN id(n) AS nid");
        if (!nodeResult.IsSuccess)
            throw new InvalidOperationException($"Failed to query nodes: {nodeResult.ErrorMessage}");
        while (nodeResult.HasNext())
        {
            var nid = Convert.ToInt64(nodeResult.GetNext().GetValue(0));
            allNodes.Add(nid);
            adj[nid] = new HashSet<long>();
        }

        // Get relationships
        var relResult = conn.Query($"MATCH (a:{nodeTable})-[r:{relTable}]->(b:{nodeTable}) RETURN id(a), id(b)");
        if (relResult.IsSuccess)
        {
            while (relResult.HasNext())
            {
                var row = relResult.GetNext();
                var a = Convert.ToInt64(row.GetValue(0));
                var b = Convert.ToInt64(row.GetValue(1));
                if (adj.ContainsKey(a)) adj[a].Add(b);
                if (adj.ContainsKey(b)) adj[b].Add(a); // undirected
            }
        }

        // Simple Louvain: each node starts in its own community,
        // then greedily move to neighbor's community that maximizes modularity gain
        var community = allNodes.ToDictionary(n => n, n => n);
        var changed = true;
        var maxPhases = 10;

        while (changed && maxPhases-- > 0)
        {
            changed = false;
            foreach (var node in allNodes)
            {
                if (!adj.ContainsKey(node) || adj[node].Count == 0) continue;

                // Count neighbor communities
                var commCounts = new Dictionary<long, int>();
                foreach (var neighbor in adj[node])
                {
                    var nc = community[neighbor];
                    commCounts.TryGetValue(nc, out var c);
                    commCounts[nc] = c + 1;
                }

                // Find best community
                var bestComm = community[node];
                var bestCount = 0;
                foreach (var (comm, count) in commCounts)
                {
                    if (count > bestCount)
                    {
                        bestCount = count;
                        bestComm = comm;
                    }
                }

                if (bestComm != community[node])
                {
                    community[node] = bestComm;
                    changed = true;
                }
            }
        }

        // Normalize community IDs to 0..N-1
        var uniqueComms = community.Values.Distinct().OrderBy(c => c).ToList();
        var commMap = uniqueComms.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => (long)x.i);

        foreach (var node in allNodes.OrderBy(n => n))
        {
            yield return new Dictionary<string, object?>
            {
                ["node_id"] = node,
                ["community_id"] = commMap[community[node]]
            };
        }
    }
}

// ── Strongly Connected Components (Kosaraju) ─────────────────────────────────

internal sealed class SccTableFunction : ITableFunction
{
    private readonly BogDatabase _db;
    public SccTableFunction(BogDatabase db) { _db = db; }

    public string Name => "SCC";

    public IReadOnlyList<(string Name, string Type)>? Schema =>
        new[] { ("node_id", "INT64"), ("component_id", "INT64") };

    public IEnumerable<Dictionary<string, object?>> Invoke(IReadOnlyList<object?> args)
    {
        if (args.Count < 2)
            throw new ArgumentException("SCC requires (node_table, rel_table) arguments.");

        var nodeTable = args[0]?.ToString()!;
        var relTable = args[1]?.ToString()!;

        using var conn = new BogConnection(_db);

        // Build directed adjacency
        var forward = new Dictionary<long, List<long>>();
        var reverse = new Dictionary<long, List<long>>();
        var allNodes = new List<long>();

        var nodeResult = conn.Query($"MATCH (n:{nodeTable}) RETURN id(n)");
        while (nodeResult.HasNext())
        {
            var nid = Convert.ToInt64(nodeResult.GetNext().GetValue(0));
            allNodes.Add(nid);
            forward[nid] = new List<long>();
            reverse[nid] = new List<long>();
        }

        var relResult = conn.Query($"MATCH (a:{nodeTable})-[:{relTable}]->(b:{nodeTable}) RETURN id(a), id(b)");
        if (relResult.IsSuccess)
        {
            while (relResult.HasNext())
            {
                var row = relResult.GetNext();
                var a = Convert.ToInt64(row.GetValue(0));
                var b = Convert.ToInt64(row.GetValue(1));
                if (forward.ContainsKey(a)) forward[a].Add(b);
                if (reverse.ContainsKey(b)) reverse[b].Add(a);
            }
        }

        // Kosaraju's algorithm
        var visited = new HashSet<long>();
        var finishOrder = new List<long>();

        // Pass 1: DFS on forward graph
        foreach (var node in allNodes)
        {
            if (!visited.Contains(node))
                DfsForward(node, forward, visited, finishOrder);
        }

        // Pass 2: DFS on reverse graph in reverse finish order
        visited.Clear();
        var component = new Dictionary<long, long>();
        long compId = 0;
        for (int i = finishOrder.Count - 1; i >= 0; i--)
        {
            var node = finishOrder[i];
            if (!visited.Contains(node))
            {
                DfsReverse(node, reverse, visited, component, compId);
                compId++;
            }
        }

        foreach (var node in allNodes.OrderBy(n => n))
        {
            yield return new Dictionary<string, object?>
            {
                ["node_id"] = node,
                ["component_id"] = component.GetValueOrDefault(node, 0)
            };
        }
    }

    private static void DfsForward(long node, Dictionary<long, List<long>> adj,
        HashSet<long> visited, List<long> finishOrder)
    {
        var stack = new Stack<(long n, bool processed)>();
        stack.Push((node, false));
        while (stack.Count > 0)
        {
            var (n, processed) = stack.Pop();
            if (processed) { finishOrder.Add(n); continue; }
            if (visited.Contains(n)) continue;
            visited.Add(n);
            stack.Push((n, true));
            if (adj.TryGetValue(n, out var neighbors))
                foreach (var nb in neighbors)
                    if (!visited.Contains(nb))
                        stack.Push((nb, false));
        }
    }

    private static void DfsReverse(long node, Dictionary<long, List<long>> adj,
        HashSet<long> visited, Dictionary<long, long> component, long compId)
    {
        var stack = new Stack<long>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (visited.Contains(n)) continue;
            visited.Add(n);
            component[n] = compId;
            if (adj.TryGetValue(n, out var neighbors))
                foreach (var nb in neighbors)
                    if (!visited.Contains(nb))
                        stack.Push(nb);
        }
    }
}

// ── K-Core Decomposition ─────────────────────────────────────────────────────

internal sealed class KCoreTableFunction : ITableFunction
{
    private readonly BogDatabase _db;
    public KCoreTableFunction(BogDatabase db) { _db = db; }

    public string Name => "KCORE";

    public IReadOnlyList<(string Name, string Type)>? Schema =>
        new[] { ("node_id", "INT64"), ("core_number", "INT64") };

    public IEnumerable<Dictionary<string, object?>> Invoke(IReadOnlyList<object?> args)
    {
        if (args.Count < 2)
            throw new ArgumentException("KCORE requires (node_table, rel_table) arguments.");

        var nodeTable = args[0]?.ToString()!;
        var relTable = args[1]?.ToString()!;

        using var conn = new BogConnection(_db);
        var degree = new Dictionary<long, int>();
        var adj = new Dictionary<long, HashSet<long>>();

        // Build undirected adjacency
        var nodeResult = conn.Query($"MATCH (n:{nodeTable}) RETURN id(n)");
        while (nodeResult.HasNext())
        {
            var nid = Convert.ToInt64(nodeResult.GetNext().GetValue(0));
            degree[nid] = 0;
            adj[nid] = new HashSet<long>();
        }

        var relResult = conn.Query($"MATCH (a:{nodeTable})-[:{relTable}]-(b:{nodeTable}) RETURN id(a), id(b)");
        if (relResult.IsSuccess)
        {
            while (relResult.HasNext())
            {
                var row = relResult.GetNext();
                var a = Convert.ToInt64(row.GetValue(0));
                var b = Convert.ToInt64(row.GetValue(1));
                if (adj.ContainsKey(a) && adj.ContainsKey(b))
                {
                    adj[a].Add(b);
                    adj[b].Add(a);
                }
            }
        }
        foreach (var (n, neighbors) in adj)
            degree[n] = neighbors.Count;

        // Peeling algorithm
        var coreNumber = new Dictionary<long, long>();
        var remaining = new HashSet<long>(degree.Keys);
        long currentK = 0;

        while (remaining.Count > 0)
        {
            var minDeg = remaining.Min(n => degree[n]);
            if (minDeg > currentK) currentK = minDeg;

            var toRemove = remaining.Where(n => degree[n] <= currentK).ToList();
            while (toRemove.Count > 0)
            {
                foreach (var node in toRemove)
                {
                    coreNumber[node] = currentK;
                    remaining.Remove(node);
                    foreach (var nb in adj[node])
                    {
                        if (remaining.Contains(nb))
                            degree[nb]--;
                    }
                }
                toRemove = remaining.Where(n => degree[n] <= currentK).ToList();
            }
            currentK++;
        }

        foreach (var node in coreNumber.Keys.OrderBy(n => n))
        {
            yield return new Dictionary<string, object?>
            {
                ["node_id"] = node,
                ["core_number"] = coreNumber[node]
            };
        }
    }
}

// ── Spanning Forest ──────────────────────────────────────────────────────────

internal sealed class SpanningForestTableFunction : ITableFunction
{
    private readonly BogDatabase _db;
    public SpanningForestTableFunction(BogDatabase db) { _db = db; }

    public string Name => "SPANNING_FOREST";

    public IReadOnlyList<(string Name, string Type)>? Schema =>
        new[] { ("src_id", "INT64"), ("dst_id", "INT64") };

    public IEnumerable<Dictionary<string, object?>> Invoke(IReadOnlyList<object?> args)
    {
        if (args.Count < 2)
            throw new ArgumentException("SPANNING_FOREST requires (node_table, rel_table) arguments.");

        var nodeTable = args[0]?.ToString()!;
        var relTable = args[1]?.ToString()!;

        using var conn = new BogConnection(_db);
        var adj = new Dictionary<long, List<long>>();

        var nodeResult = conn.Query($"MATCH (n:{nodeTable}) RETURN id(n)");
        while (nodeResult.HasNext())
        {
            var nid = Convert.ToInt64(nodeResult.GetNext().GetValue(0));
            adj[nid] = new List<long>();
        }

        var relResult = conn.Query($"MATCH (a:{nodeTable})-[:{relTable}]-(b:{nodeTable}) RETURN id(a), id(b)");
        if (relResult.IsSuccess)
        {
            while (relResult.HasNext())
            {
                var row = relResult.GetNext();
                var a = Convert.ToInt64(row.GetValue(0));
                var b = Convert.ToInt64(row.GetValue(1));
                if (adj.ContainsKey(a)) adj[a].Add(b);
                if (adj.ContainsKey(b)) adj[b].Add(a);
            }
        }

        // BFS spanning forest
        var visited = new HashSet<long>();
        foreach (var root in adj.Keys.OrderBy(n => n))
        {
            if (visited.Contains(root)) continue;
            visited.Add(root);
            var queue = new Queue<long>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                foreach (var nb in adj[node])
                {
                    if (visited.Contains(nb)) continue;
                    visited.Add(nb);
                    queue.Enqueue(nb);
                    yield return new Dictionary<string, object?>
                    {
                        ["src_id"] = node,
                        ["dst_id"] = nb
                    };
                }
            }
        }
    }
}

// ── WCC as algo extension wrapper (delegates to in-core) ─────────────────────

internal sealed class WccAlgoTableFunction : ITableFunction
{
    private readonly BogDatabase _db;
    public WccAlgoTableFunction(BogDatabase db) { _db = db; }

    public string Name => "WCC";

    public IReadOnlyList<(string Name, string Type)>? Schema =>
        new[] { ("node_id", "INT64"), ("component_id", "INT64") };

    public IEnumerable<Dictionary<string, object?>> Invoke(IReadOnlyList<object?> args)
    {
        if (args.Count < 2)
            throw new ArgumentException("WCC requires (node_table, rel_table) arguments.");

        var nodeTable = args[0]?.ToString()!;
        var relTable = args[1]?.ToString()!;

        using var conn = new BogConnection(_db);
        var parent = new Dictionary<long, long>();

        long Find(long x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        void Union(long a, long b)
        {
            a = Find(a); b = Find(b);
            if (a != b) parent[a] = b;
        }

        var nodeResult = conn.Query($"MATCH (n:{nodeTable}) RETURN id(n)");
        while (nodeResult.HasNext())
        {
            var nid = Convert.ToInt64(nodeResult.GetNext().GetValue(0));
            parent[nid] = nid;
        }

        var relResult = conn.Query($"MATCH (a:{nodeTable})-[:{relTable}]-(b:{nodeTable}) RETURN id(a), id(b)");
        if (relResult.IsSuccess)
        {
            while (relResult.HasNext())
            {
                var row = relResult.GetNext();
                Union(Convert.ToInt64(row.GetValue(0)), Convert.ToInt64(row.GetValue(1)));
            }
        }

        // Normalize component IDs
        var roots = parent.Keys.Select(n => Find(n)).Distinct().OrderBy(r => r).ToList();
        var compMap = roots.Select((r, i) => (r, (long)i)).ToDictionary(x => x.r, x => x.Item2);

        foreach (var node in parent.Keys.OrderBy(n => n))
        {
            yield return new Dictionary<string, object?>
            {
                ["node_id"] = node,
                ["component_id"] = compMap[Find(node)]
            };
        }
    }
}
