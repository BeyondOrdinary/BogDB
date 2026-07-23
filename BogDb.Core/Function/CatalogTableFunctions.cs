using System;
using System.Collections.Generic;
using System.Linq;
using BogDb.Core.Extension;

namespace BogDb.Core.Function;

/// <summary>
/// Catalog introspection table functions — C++ parity with src/function/table/*.cpp.
/// Each function implements <see cref="ITableFunction"/> and returns rows matching the C++ column schema.
/// Registered in BogDatabase initialization alongside extensions.
/// </summary>

// ── SHOW_TABLES ──────────────────────────────────────────────────────────────
// C++ parity: src/function/table/show_tables.cpp
// Columns: id (INT64), name (STRING), type (STRING), database name (STRING), comment (STRING)

internal sealed class ShowTablesTableFunction : ITableFunction
{
    public string Name => "SHOW_TABLES";

    public IReadOnlyList<(string Name, string Type)>? Schema => new (string, string)[]
    {
        ("id", "INT64"), ("name", "STRING"), ("type", "STRING"),
        ("database name", "STRING"), ("comment", "STRING")
    };

    public IEnumerable<Dictionary<string, object?>> Invoke(IReadOnlyList<object?> args)
    {
        var db = TableFunctionRuntimeContext.CurrentDatabase;
        if (db == null) yield break;

        long id = 0;
        foreach (var entry in db.Catalog.GetNodeTableEntries().OrderBy(e => e.Name))
        {
            yield return new Dictionary<string, object?>
            {
                ["id"] = id++, ["name"] = entry.Name, ["type"] = "NODE",
                ["database name"] = "local(bogdb)", ["comment"] = entry.GetComment()
            };
        }

        foreach (var entry in db.Catalog.GetRelTableEntries().OrderBy(e => e.Name))
        {
            yield return new Dictionary<string, object?>
            {
                ["id"] = id++, ["name"] = entry.Name, ["type"] = "REL",
                ["database name"] = "local(bogdb)", ["comment"] = entry.GetComment()
            };
        }

        foreach (var (alias, attached) in db.AttachedDatabases.OrderBy(kvp => kvp.Key))
        {
            yield return new Dictionary<string, object?>
            {
                ["id"] = id++, ["name"] = alias, ["type"] = "ATTACHED",
                ["database name"] = $"{alias}({attached.DbType})",
                ["comment"] = ""
            };
        }
    }
}

// ── TABLE_INFO ───────────────────────────────────────────────────────────────
// C++ parity: src/function/table/table_info.cpp
// Node columns: property id (INT32), name (STRING), type (STRING), default expression (STRING), primary key (BOOL)
// Rel columns:  property id (INT32), name (STRING), type (STRING), default expression (STRING), storage_direction (STRING)

internal sealed class TableInfoTableFunction : ITableFunction
{
    public string Name => "TABLE_INFO";

    // Schema varies by table type — declare the superset and let rows fill relevant columns
    public IReadOnlyList<(string Name, string Type)>? Schema => new (string, string)[]
    {
        ("property id", "INT32"), ("name", "STRING"), ("type", "STRING"),
        ("default expression", "STRING"), ("primary key", "STRING")
    };

    public IEnumerable<Dictionary<string, object?>> Invoke(IReadOnlyList<object?> args)
    {
        var db = TableFunctionRuntimeContext.CurrentDatabase;
        if (db == null || args.Count == 0 || args[0] == null) yield break;

        var tableName = args[0]!.ToString()!;
        var entry = db.Catalog.GetTableCatalogEntry(null, tableName);
        if (entry == null)
            throw new InvalidOperationException($"{tableName} does not exist in catalog.");

        uint propertyId = 0;
        if (entry is Catalog.NodeTableCatalogEntry nodeEntry)
        {
            var primaryKeyPropId = nodeEntry.PrimaryKeyPropertyID;
            foreach (var prop in nodeEntry.GetProperties())
            {
                var isPrimaryKey = propertyId == primaryKeyPropId;
                yield return new Dictionary<string, object?>
                {
                    ["property id"] = (int)propertyId++,
                    ["name"] = prop.Name,
                    ["type"] = prop.DeclaredType,
                    ["default expression"] = prop.DefaultExpressionName ?? "",
                    ["primary key"] = isPrimaryKey ? "True" : "False"
                };
            }
        }
        else if (entry is Catalog.RelGroupCatalogEntry relEntry)
        {
            foreach (var prop in relEntry.GetProperties())
            {
                yield return new Dictionary<string, object?>
                {
                    ["property id"] = (int)propertyId++,
                    ["name"] = prop.Name,
                    ["type"] = prop.DeclaredType,
                    ["default expression"] = prop.DefaultExpressionName ?? "",
                    ["primary key"] = "FWD"  // storage_direction (C++ uses "FWD" default)
                };
            }
        }
    }
}

// ── SHOW_FUNCTIONS ───────────────────────────────────────────────────────────
// C++ parity: src/function/table/show_functions.cpp
// Columns: name (STRING), type (STRING), signature (STRING)

internal sealed class ShowFunctionsTableFunction : ITableFunction
{
    public string Name => "SHOW_FUNCTIONS";

    public IReadOnlyList<(string Name, string Type)>? Schema => new (string, string)[]
    {
        ("name", "STRING"), ("type", "STRING"), ("signature", "STRING")
    };

    public IEnumerable<Dictionary<string, object?>> Invoke(IReadOnlyList<object?> args)
    {
        var db = TableFunctionRuntimeContext.CurrentDatabase;
        if (db == null) yield break;

        // Emit built-in scalar functions
        foreach (var name in FunctionDispatcher.GetRegisteredNames().OrderBy(n => n))
        {
            yield return new Dictionary<string, object?>
            {
                ["name"] = name, ["type"] = "scalar", ["signature"] = $"{name}(...)"
            };
        }

        // Emit extension-registered table functions from FunctionRegistry
        foreach (var name in db.FunctionRegistry.GetRegisteredNames().OrderBy(n => n))
        {
            yield return new Dictionary<string, object?>
            {
                ["name"] = name, ["type"] = "table", ["signature"] = $"{name}(...)"
            };
        }

        // Emit standalone table functions
        foreach (var name in db.StandaloneTableFunctionRegistry.GetRegisteredNames().OrderBy(n => n))
        {
            yield return new Dictionary<string, object?>
            {
                ["name"] = name, ["type"] = "table", ["signature"] = $"{name}(...)"
            };
        }
    }
}

// ── SHOW_INDEXES ─────────────────────────────────────────────────────────────
// C++ parity: src/function/table/show_indexes.cpp
// Columns: table name (STRING), index name (STRING), index type (STRING), column names (STRING)

internal sealed class ShowIndexesTableFunction : ITableFunction
{
    public string Name => "SHOW_INDEXES";

    public IReadOnlyList<(string Name, string Type)>? Schema => new (string, string)[]
    {
        ("table name", "STRING"), ("index name", "STRING"),
        ("index type", "STRING"), ("column names", "STRING")
    };

    public IEnumerable<Dictionary<string, object?>> Invoke(IReadOnlyList<object?> args)
    {
        var db = TableFunctionRuntimeContext.CurrentDatabase;
        if (db == null) yield break;

        foreach (var entry in db.Catalog.GetIndexEntries().OrderBy(e => e.TableName).ThenBy(e => e.PropertyName))
        {
            yield return new Dictionary<string, object?>
            {
                ["table name"] = entry.TableName,
                ["index name"] = $"{entry.TableName}.{entry.PropertyName}",
                ["index type"] = entry.IndexTypeName,
                ["column names"] = entry.PropertyName
            };
        }
    }
}

// ── SHOW_SEQUENCES ───────────────────────────────────────────────────────────
// C++ parity: src/function/table/show_sequences.cpp
// Columns: name (STRING), database name (STRING), start value (INT64), increment (INT64),
//          min value (INT64), max value (INT64), cycle (BOOL)

internal sealed class ShowSequencesTableFunction : ITableFunction
{
    public string Name => "SHOW_SEQUENCES";

    public IReadOnlyList<(string Name, string Type)>? Schema => new (string, string)[]
    {
        ("name", "STRING"), ("database name", "STRING"), ("start value", "INT64"),
        ("increment", "INT64"), ("min value", "INT64"), ("max value", "INT64"), ("cycle", "STRING")
    };

    public IEnumerable<Dictionary<string, object?>> Invoke(IReadOnlyList<object?> args)
    {
        var db = TableFunctionRuntimeContext.CurrentDatabase;
        if (db == null) yield break;

        // Emit in-memory auto-created sequences from SequenceFunctions
        foreach (var (name, currentValue) in Sequence.SequenceFunctions.GetActiveSequences().OrderBy(s => s.name))
        {
            yield return new Dictionary<string, object?>
            {
                ["name"] = name,
                ["database name"] = "local(bogdb)",
                ["start value"] = 1L,
                ["increment"] = 1L,
                ["min value"] = 1L,
                ["max value"] = long.MaxValue,
                ["cycle"] = "False"
            };
        }
    }
}

// ── SHOW_MACROS ──────────────────────────────────────────────────────────────
// C++ parity: src/function/table/show_macros.cpp
// Columns: name (STRING), parameters (STRING), default (STRING), return type (STRING)

internal sealed class ShowMacrosTableFunction : ITableFunction
{
    public string Name => "SHOW_MACROS";

    public IReadOnlyList<(string Name, string Type)>? Schema => new (string, string)[]
    {
        ("name", "STRING"), ("parameters", "STRING"), ("default", "STRING")
    };

    public IEnumerable<Dictionary<string, object?>> Invoke(IReadOnlyList<object?> args)
    {
        var db = TableFunctionRuntimeContext.CurrentDatabase;
        if (db == null) yield break;

        foreach (var entry in db.Catalog.GetMacroEntries().OrderBy(e => e.Name))
        {
            var paramNames = string.Join(", ", entry.Parameters.Select(p => p.Name));
            var defaults = string.Join(", ", entry.Parameters
                .Where(p => p.DefaultExpression != null)
                .Select(p => $"{p.Name}={p.DefaultExpression!.GetRawName()}"));

            yield return new Dictionary<string, object?>
            {
                ["name"] = entry.Name,
                ["parameters"] = paramNames,
                ["default"] = defaults
            };
        }
    }
}

// ── SHOW_ATTACHED_DATABASES ──────────────────────────────────────────────────
// C++ parity: src/function/table/show_attached_databases.cpp
// Columns: name (STRING), database type (STRING)

internal sealed class ShowAttachedDatabasesTableFunction : ITableFunction
{
    public string Name => "SHOW_ATTACHED_DATABASES";

    public IReadOnlyList<(string Name, string Type)>? Schema => new (string, string)[]
    {
        ("name", "STRING"), ("database type", "STRING")
    };

    public IEnumerable<Dictionary<string, object?>> Invoke(IReadOnlyList<object?> args)
    {
        var db = TableFunctionRuntimeContext.CurrentDatabase;
        if (db == null) yield break;

        foreach (var (alias, attached) in db.AttachedDatabases.OrderBy(kvp => kvp.Key))
        {
            yield return new Dictionary<string, object?>
            {
                ["name"] = alias,
                ["database type"] = attached.DbType
            };
        }
    }
}

// ── SHOW_LOADED_EXTENSIONS ───────────────────────────────────────────────────
// C++ parity: src/function/table/show_loaded_extensions.cpp
// Columns: name (STRING), source (STRING)

internal sealed class ShowLoadedExtensionsTableFunction : ITableFunction
{
    public string Name => "SHOW_LOADED_EXTENSIONS";

    public IReadOnlyList<(string Name, string Type)>? Schema => new (string, string)[]
    {
        ("name", "STRING"), ("source", "STRING")
    };

    public IEnumerable<Dictionary<string, object?>> Invoke(IReadOnlyList<object?> args)
    {
        var db = TableFunctionRuntimeContext.CurrentDatabase;
        if (db == null) yield break;

        foreach (var name in db.ExtensionManager.LoadedExtensionNames.OrderBy(n => n))
        {
            yield return new Dictionary<string, object?>
            {
                ["name"] = name,
                ["source"] = "runtime"
            };
        }
    }
}

// ── CLEAR_WARNINGS ───────────────────────────────────────────────────────────
// C++ parity: src/function/table/clear_warnings.cpp
// Side-effect function: clears warnings buffer, returns one confirmation row.

internal sealed class ClearWarningsTableFunction : ITableFunction
{
    public string Name => "CLEAR_WARNINGS";

    public IReadOnlyList<(string Name, string Type)>? Schema => new (string, string)[]
    {
        ("status", "STRING")
    };

    public IEnumerable<Dictionary<string, object?>> Invoke(IReadOnlyList<object?> args)
    {
        // BogDB does not currently accumulate warnings; this is a no-op placeholder
        // matching the C++ interface contract.
        yield return new Dictionary<string, object?> { ["status"] = "OK" };
    }
}

// ── SHOW_WARNINGS ────────────────────────────────────────────────────────────
// C++ parity: src/function/table/show_warnings.cpp
// Columns: query (STRING), message (STRING)

internal sealed class ShowWarningsTableFunction : ITableFunction
{
    public string Name => "SHOW_WARNINGS";

    public IReadOnlyList<(string Name, string Type)>? Schema => new (string, string)[]
    {
        ("query", "STRING"), ("message", "STRING")
    };

    public IEnumerable<Dictionary<string, object?>> Invoke(IReadOnlyList<object?> args)
    {
        // BogDB does not currently accumulate warnings; returns empty.
        yield break;
    }
}

// ── CREATE_INDEX ─────────────────────────────────────────────────────────────
// Creates a secondary (non-primary-key) property index so the planner can serve
// equality/prefix predicates via INDEX_SCAN instead of a full SCAN_NODE_PROPERTY.
// Query-based counterpart to BogDatabase.CreateIndex; idempotent so re-runnable
// schema setup is safe. For file-backed databases the index is disk-backed and
// restored on reopen. Columns: table (STRING), property (STRING), status (STRING).

internal sealed class CreateIndexTableFunction : ITableFunction
{
    private readonly BogDb.Core.Main.BogDatabase _database;

    public CreateIndexTableFunction(BogDb.Core.Main.BogDatabase database)
    {
        _database = database;
    }

    public string Name => "CREATE_INDEX";

    public IReadOnlyList<(string Name, string Type)>? Schema => new (string, string)[]
    {
        ("table", "STRING"), ("property", "STRING"), ("status", "STRING")
    };

    public IEnumerable<Dictionary<string, object?>> Invoke(IReadOnlyList<object?> args)
    {
        if (args.Count != 2)
            throw new ArgumentException("create_index requires a table name and a property name.");
        if (args[0] is not string tableName || string.IsNullOrWhiteSpace(tableName))
            throw new ArgumentException("create_index requires the first argument to be a node table name.");
        if (args[1] is not string propertyName || string.IsNullOrWhiteSpace(propertyName))
            throw new ArgumentException("create_index requires the second argument to be a property name.");

        if (_database.Catalog.GetTableCatalogEntry(null, tableName, useInternal: false)
                is not BogDb.Core.Catalog.NodeTableCatalogEntry entry)
            throw new ArgumentException($"create_index requires an existing node table: {tableName}");
        if (!entry.ContainsProperty(propertyName))
            throw new ArgumentException($"Property '{propertyName}' does not exist on table '{tableName}'.");

        // Idempotent: creating an index that already exists is a no-op (safe for re-runnable schema setup).
        if (_database.Catalog.ContainsIndexEntry(tableName, propertyName))
        {
            yield return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["table"] = tableName,
                ["property"] = propertyName,
                ["status"] = "exists"
            };
            yield break;
        }

        _database.CreateIndex(tableName, propertyName);
        yield return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["table"] = tableName,
            ["property"] = propertyName,
            ["status"] = "created"
        };
    }
}
