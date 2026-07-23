using System.Collections.Generic;
using BogDb.Core.Main;
using BogDb.Core.Processor.Operator.Scan;
using Xunit;

namespace BogDb.Tests.Processor;

public class IndexScanVisibilityTests
{
    [Fact]
    public void IndexScan_HidesUncommittedInsertFromOlderReader()
    {
        using var db = BogDatabase.CreateInMemory();
        db.NodeTables["Person"] = new NodeTableData();
        db.CreateIndex("Person", "name");
        var table = db.NodeTables["Person"];

        var writer = new BogDb.Core.Transaction.Transaction(
            BogDb.Core.Transaction.TransactionType.WRITE,
            BogDb.Core.Transaction.Transaction.START_TRANSACTION_ID + 500,
            startTS: 0);
        var oldReader = new BogDb.Core.Transaction.Transaction(
            BogDb.Core.Transaction.TransactionType.READ_ONLY,
            id: 1,
            startTS: 0);

        var props = new Dictionary<string, object> { ["name"] = "Alice" };
        table.Upsert(writer, "n1", props);
        db.UpdateNodeIndexes("Person", writer, "n1", props, table);

        var oldScan = new PhysicalIndexScanNode(db, "Person", "name", "Alice", "n", id: 1);
        var oldCtx = new BogDb.Core.Processor.ExecutionContext(oldReader, db.BufferManager);
        Assert.False(oldScan.GetNextTuple(oldCtx));

        table.CommitVersions(writer, commitTS: 5);
        var newReader = new BogDb.Core.Transaction.Transaction(
            BogDb.Core.Transaction.TransactionType.READ_ONLY,
            id: 2,
            startTS: 5);
        var newScan = new PhysicalIndexScanNode(db, "Person", "name", "Alice", "n", id: 2);
        var newCtx = new BogDb.Core.Processor.ExecutionContext(newReader, db.BufferManager);
        Assert.True(newScan.GetNextTuple(newCtx));
        Assert.Equal("n1", newCtx.CurrentNodeId);
    }

    [Fact]
    public void IndexScan_HidesCommittedDeletesForNewReaders()
    {
        using var db = BogDatabase.CreateInMemory();
        db.NodeTables["Person"] = new NodeTableData();
        var table = db.NodeTables["Person"];
        table.Upsert("n1", new Dictionary<string, object> { ["name"] = "Alice" });
        db.CreateIndex("Person", "name");

        var visibleReader = new BogDb.Core.Transaction.Transaction(
            BogDb.Core.Transaction.TransactionType.READ_ONLY,
            id: 3,
            startTS: 0);
        var visibleScan = new PhysicalIndexScanNode(db, "Person", "name", "Alice", "n", id: 3);
        var visibleCtx = new BogDb.Core.Processor.ExecutionContext(visibleReader, db.BufferManager);
        Assert.True(visibleScan.GetNextTuple(visibleCtx));

        var deleter = new BogDb.Core.Transaction.Transaction(
            BogDb.Core.Transaction.TransactionType.WRITE,
            BogDb.Core.Transaction.Transaction.START_TRANSACTION_ID + 501,
            startTS: 0);
        Assert.True(table.Remove(deleter, "n1"));
        table.CommitVersions(deleter, commitTS: 6);

        var afterDeleteReader = new BogDb.Core.Transaction.Transaction(
            BogDb.Core.Transaction.TransactionType.READ_ONLY,
            id: 4,
            startTS: 6);
        var afterDeleteScan = new PhysicalIndexScanNode(db, "Person", "name", "Alice", "n", id: 4);
        var afterDeleteCtx = new BogDb.Core.Processor.ExecutionContext(afterDeleteReader, db.BufferManager);
        Assert.False(afterDeleteScan.GetNextTuple(afterDeleteCtx));
    }

    [Fact]
    public void IndexScan_OldReaderSeesOldVisibleRow_AfterDeleteReinsertCommit()
    {
        using var db = BogDatabase.CreateInMemory();
        db.NodeTables["Person"] = new NodeTableData();
        var table = db.NodeTables["Person"];

        table.Upsert("n1", new Dictionary<string, object> { ["name"] = "Alice" });
        db.CreateIndex("Person", "name");

        var oldReader = new BogDb.Core.Transaction.Transaction(
            BogDb.Core.Transaction.TransactionType.READ_ONLY,
            id: 5,
            startTS: 0);

        var writer = new BogDb.Core.Transaction.Transaction(
            BogDb.Core.Transaction.TransactionType.WRITE,
            BogDb.Core.Transaction.Transaction.START_TRANSACTION_ID + 502,
            startTS: 0);
        Assert.True(table.Remove(writer, "n1"));
        table.Upsert(writer, "n1", new Dictionary<string, object> { ["name"] = "Alice" });
        db.UpdateNodeIndexes("Person", writer, "n1", new Dictionary<string, object> { ["name"] = "Alice" }, table);
        table.CommitVersions(writer, commitTS: 6);

        var oldScan = new PhysicalIndexScanNode(db, "Person", "name", "Alice", "n", id: 5);
        var oldCtx = new BogDb.Core.Processor.ExecutionContext(oldReader, db.BufferManager);
        Assert.True(oldScan.GetNextTuple(oldCtx));
        Assert.Equal("n1", oldCtx.CurrentNodeId);

        var newReader = new BogDb.Core.Transaction.Transaction(
            BogDb.Core.Transaction.TransactionType.READ_ONLY,
            id: 6,
            startTS: 6);
        var newScan = new PhysicalIndexScanNode(db, "Person", "name", "Alice", "n", id: 6);
        var newCtx = new BogDb.Core.Processor.ExecutionContext(newReader, db.BufferManager);
        Assert.True(newScan.GetNextTuple(newCtx));
        Assert.Equal("n1", newCtx.CurrentNodeId);
    }

    [Fact]
    public void IndexScan_Tracks_PropertyUpdate_AcrossReaderSnapshots()
    {
        using var db = BogDatabase.CreateInMemory();
        using var conn = new BogConnection(db);
        using var oldConn = new BogConnection(db);

        conn.BeginWriteTransaction();
        conn.EnsureNodeTable("Person", new Dictionary<string, BogDb.Core.Common.LogicalTypeID>
        {
            ["id"] = BogDb.Core.Common.LogicalTypeID.STRING,
            ["name"] = BogDb.Core.Common.LogicalTypeID.STRING
        });
        conn.Commit();

        conn.UpsertNodeById("Person", "n1", new Dictionary<string, object> { ["name"] = "Alice" });
        conn.CreateIndex("Person", "name");

        oldConn.ClientContext.StartTransaction(BogDb.Core.Transaction.TransactionType.READ_ONLY);
        var oldReader = oldConn.ClientContext.ActiveTransaction!;

        conn.BeginWriteTransaction();
        var setResult = conn.Query("MATCH (p:Person {name:'Alice'}) SET p.name = 'Alicia'");
        Assert.True(setResult.IsSuccess, setResult.ErrorMessage);
        conn.Commit();

        var oldScan = new PhysicalIndexScanNode(db, "Person", "name", "Alice", "n", id: 7);
        var oldCtx = new BogDb.Core.Processor.ExecutionContext(oldReader, db.BufferManager);
        Assert.True(oldScan.GetNextTuple(oldCtx));
        Assert.Equal("n1", oldCtx.CurrentNodeId);
        Assert.Equal("Alice", oldCtx.CurrentNodeProperties!["name"]);

        var newReader = new BogDb.Core.Transaction.Transaction(
            BogDb.Core.Transaction.TransactionType.READ_ONLY,
            id: 8,
            startTS: 100);

        var staleScan = new PhysicalIndexScanNode(db, "Person", "name", "Alice", "n", id: 8);
        var staleCtx = new BogDb.Core.Processor.ExecutionContext(newReader, db.BufferManager);
        Assert.False(staleScan.GetNextTuple(staleCtx));

        var updatedScan = new PhysicalIndexScanNode(db, "Person", "name", "Alicia", "n", id: 9);
        var updatedCtx = new BogDb.Core.Processor.ExecutionContext(newReader, db.BufferManager);
        Assert.True(updatedScan.GetNextTuple(updatedCtx));
        Assert.Equal("n1", updatedCtx.CurrentNodeId);
        Assert.Equal("Alicia", updatedCtx.CurrentNodeProperties!["name"]);
    }

    [Fact]
    public void IndexScan_DoesNotMatchNodeId_WhenScanningByDifferentProperty()
    {
        using var db = BogDatabase.CreateInMemory();
        using var conn = new BogConnection(db);

        conn.BeginWriteTransaction();
        conn.EnsureNodeTable("Person", new Dictionary<string, BogDb.Core.Common.LogicalTypeID>
        {
            ["id"] = BogDb.Core.Common.LogicalTypeID.STRING,
            ["name"] = BogDb.Core.Common.LogicalTypeID.STRING
        });
        conn.UpsertNode("Person", "alice", new Dictionary<string, object>
        {
            ["id"] = "alice",
            ["name"] = "Alicia"
        });
        conn.Commit();
        conn.CreateIndex("Person", "name");

        var reader = new BogDb.Core.Transaction.Transaction(
            BogDb.Core.Transaction.TransactionType.READ_ONLY,
            id: 10,
            startTS: 100);

        var scan = new PhysicalIndexScanNode(db, "Person", "name", "alice", "p", id: 10);
        var ctx = new BogDb.Core.Processor.ExecutionContext(reader, db.BufferManager);
        Assert.False(scan.GetNextTuple(ctx));
    }

    [Fact]
    public void IndexScan_NonUnique_ReturnsAllMatches()
    {
        using var db = BogDatabase.CreateInMemory();
        using var conn = new BogConnection(db);

        conn.BeginWriteTransaction();
        conn.EnsureNodeTable("Person", new Dictionary<string, BogDb.Core.Common.LogicalTypeID>
        {
            ["id"] = BogDb.Core.Common.LogicalTypeID.STRING,
            ["name"] = BogDb.Core.Common.LogicalTypeID.STRING
        });
        conn.UpsertNode("Person", "p1", new Dictionary<string, object>
        {
            ["id"] = "p1",
            ["name"] = "Alice"
        });
        conn.UpsertNode("Person", "p2", new Dictionary<string, object>
        {
            ["id"] = "p2",
            ["name"] = "Alice"
        });
        conn.UpsertNode("Person", "p3", new Dictionary<string, object>
        {
            ["id"] = "p3",
            ["name"] = "Bob"
        });
        conn.Commit();
        conn.CreateIndex("Person", "name");

        var reader = new BogDb.Core.Transaction.Transaction(
            BogDb.Core.Transaction.TransactionType.READ_ONLY,
            id: 20,
            startTS: 100);

        var scan = new PhysicalIndexScanNode(db, "Person", "name", "Alice", "p", id: 20);
        var ctx = new BogDb.Core.Processor.ExecutionContext(reader, db.BufferManager);

        // Should get exactly 2 matches for "Alice"
        var matchedIds = new List<object>();
        while (scan.GetNextTuple(ctx))
        {
            matchedIds.Add(ctx.CurrentNodeId!);
        }

        Assert.Equal(2, matchedIds.Count);
        Assert.Contains("p1", matchedIds);
        Assert.Contains("p2", matchedIds);
    }

    [Fact]
    public void IndexScan_NonUnique_ViaQuery_ReturnsAllMatches()
    {
        using var db = BogDatabase.CreateInMemory();
        using var conn = new BogConnection(db);

        conn.BeginWriteTransaction();
        conn.EnsureNodeTable("City", new Dictionary<string, BogDb.Core.Common.LogicalTypeID>
        {
            ["id"] = BogDb.Core.Common.LogicalTypeID.STRING,
            ["country"] = BogDb.Core.Common.LogicalTypeID.STRING
        });
        conn.UpsertNode("City", "nyc", new Dictionary<string, object>
        {
            ["id"] = "nyc",
            ["country"] = "USA"
        });
        conn.UpsertNode("City", "la", new Dictionary<string, object>
        {
            ["id"] = "la",
            ["country"] = "USA"
        });
        conn.UpsertNode("City", "london", new Dictionary<string, object>
        {
            ["id"] = "london",
            ["country"] = "UK"
        });
        conn.Commit();
        conn.CreateIndex("City", "country");

        // Query should find both US cities via index scan
        var result = conn.Query("MATCH (c:City {country: 'USA'}) RETURN c.id AS id ORDER BY id");
        Assert.True(result.IsSuccess, result.ErrorMessage);

        var ids = new List<string>();
        while (result.HasNext())
        {
            ids.Add(result.GetNext().GetString("id")!);
        }

        Assert.Equal(2, ids.Count);
        Assert.Equal("la", ids[0]);
        Assert.Equal("nyc", ids[1]);
    }

    [Fact]
    public void Query_InList_ReturnsMatchingNodes()
    {
        using var db = BogDatabase.CreateInMemory();
        using var conn = new BogConnection(db);

        conn.BeginWriteTransaction();
        conn.EnsureNodeTable("Fruit", new Dictionary<string, BogDb.Core.Common.LogicalTypeID>
        {
            ["id"] = BogDb.Core.Common.LogicalTypeID.STRING,
            ["name"] = BogDb.Core.Common.LogicalTypeID.STRING
        });
        conn.UpsertNode("Fruit", "a", new Dictionary<string, object> { ["id"] = "a", ["name"] = "Apple" });
        conn.UpsertNode("Fruit", "b", new Dictionary<string, object> { ["id"] = "b", ["name"] = "Banana" });
        conn.UpsertNode("Fruit", "c", new Dictionary<string, object> { ["id"] = "c", ["name"] = "Cherry" });
        conn.UpsertNode("Fruit", "d", new Dictionary<string, object> { ["id"] = "d", ["name"] = "Date" });
        conn.Commit();

        var result = conn.Query("MATCH (f:Fruit) WHERE f.name IN ['Apple', 'Cherry'] RETURN f.name AS name ORDER BY name");
        Assert.True(result.IsSuccess, result.ErrorMessage);

        var names = new List<string>();
        while (result.HasNext())
            names.Add(result.GetNext().GetString("name")!);

        Assert.Equal(2, names.Count);
        Assert.Equal("Apple", names[0]);
        Assert.Equal("Cherry", names[1]);
    }

    [Fact]
    public void Query_StartsWith_ReturnsMatchingNodes()
    {
        using var db = BogDatabase.CreateInMemory();
        using var conn = new BogConnection(db);

        conn.BeginWriteTransaction();
        conn.EnsureNodeTable("City", new Dictionary<string, BogDb.Core.Common.LogicalTypeID>
        {
            ["id"] = BogDb.Core.Common.LogicalTypeID.STRING,
            ["name"] = BogDb.Core.Common.LogicalTypeID.STRING
        });
        conn.UpsertNode("City", "c1", new Dictionary<string, object> { ["id"] = "c1", ["name"] = "San Francisco" });
        conn.UpsertNode("City", "c2", new Dictionary<string, object> { ["id"] = "c2", ["name"] = "San Jose" });
        conn.UpsertNode("City", "c3", new Dictionary<string, object> { ["id"] = "c3", ["name"] = "Seattle" });
        conn.UpsertNode("City", "c4", new Dictionary<string, object> { ["id"] = "c4", ["name"] = "Portland" });
        conn.Commit();

        var result = conn.Query("MATCH (c:City) WHERE c.name STARTS WITH 'San' RETURN c.name AS name ORDER BY name");
        Assert.True(result.IsSuccess, result.ErrorMessage);

        var names = new List<string>();
        while (result.HasNext())
            names.Add(result.GetNext().GetString("name")!);

        Assert.Equal(2, names.Count);
        Assert.Equal("San Francisco", names[0]);
        Assert.Equal("San Jose", names[1]);
    }

    [Fact]
    public void Query_Contains_ReturnsMatchingNodes()
    {
        using var db = BogDatabase.CreateInMemory();
        using var conn = new BogConnection(db);

        conn.BeginWriteTransaction();
        conn.EnsureNodeTable("Item", new Dictionary<string, BogDb.Core.Common.LogicalTypeID>
        {
            ["id"] = BogDb.Core.Common.LogicalTypeID.STRING,
            ["description"] = BogDb.Core.Common.LogicalTypeID.STRING
        });
        conn.UpsertNode("Item", "i1", new Dictionary<string, object> { ["id"] = "i1", ["description"] = "red widget" });
        conn.UpsertNode("Item", "i2", new Dictionary<string, object> { ["id"] = "i2", ["description"] = "blue gadget" });
        conn.UpsertNode("Item", "i3", new Dictionary<string, object> { ["id"] = "i3", ["description"] = "red gadget" });
        conn.Commit();

        var result = conn.Query("MATCH (i:Item) WHERE i.description CONTAINS 'gadget' RETURN i.id AS id ORDER BY id");
        Assert.True(result.IsSuccess, result.ErrorMessage);

        var ids = new List<string>();
        while (result.HasNext())
            ids.Add(result.GetNext().GetString("id")!);

        Assert.Equal(2, ids.Count);
        Assert.Equal("i2", ids[0]);
        Assert.Equal("i3", ids[1]);
    }

    [Fact]
    public void Query_EndsWith_ReturnsMatchingNodes()
    {
        using var db = BogDatabase.CreateInMemory();
        using var conn = new BogConnection(db);

        conn.BeginWriteTransaction();
        conn.EnsureNodeTable("File", new Dictionary<string, BogDb.Core.Common.LogicalTypeID>
        {
            ["id"] = BogDb.Core.Common.LogicalTypeID.STRING,
            ["name"] = BogDb.Core.Common.LogicalTypeID.STRING
        });
        conn.UpsertNode("File", "f1", new Dictionary<string, object> { ["id"] = "f1", ["name"] = "readme.md" });
        conn.UpsertNode("File", "f2", new Dictionary<string, object> { ["id"] = "f2", ["name"] = "data.csv" });
        conn.UpsertNode("File", "f3", new Dictionary<string, object> { ["id"] = "f3", ["name"] = "notes.md" });
        conn.Commit();

        var result = conn.Query("MATCH (f:File) WHERE f.name ENDS WITH '.md' RETURN f.id AS id ORDER BY id");
        Assert.True(result.IsSuccess, result.ErrorMessage);

        var ids = new List<string>();
        while (result.HasNext())
            ids.Add(result.GetNext().GetString("id")!);

        Assert.Equal(2, ids.Count);
        Assert.Equal("f1", ids[0]);
        Assert.Equal("f3", ids[1]);
    }

    [Fact]
    public void Query_InList_WithIndex_UsesIndexScan()
    {
        using var db = BogDatabase.CreateInMemory();
        using var conn = new BogConnection(db);

        conn.BeginWriteTransaction();
        conn.EnsureNodeTable("Animal", new Dictionary<string, BogDb.Core.Common.LogicalTypeID>
        {
            ["id"] = BogDb.Core.Common.LogicalTypeID.STRING,
            ["species"] = BogDb.Core.Common.LogicalTypeID.STRING
        });

        conn.UpsertNode("Animal", "a1", new Dictionary<string, object> { ["id"] = "a1", ["species"] = "Cat" });
        conn.UpsertNode("Animal", "a2", new Dictionary<string, object> { ["id"] = "a2", ["species"] = "Dog" });
        conn.UpsertNode("Animal", "a3", new Dictionary<string, object> { ["id"] = "a3", ["species"] = "Cat" });
        conn.UpsertNode("Animal", "a4", new Dictionary<string, object> { ["id"] = "a4", ["species"] = "Bird" });
        conn.UpsertNode("Animal", "a5", new Dictionary<string, object> { ["id"] = "a5", ["species"] = "Dog" });
        conn.Commit();

        // Create index AFTER data exists so Rebuild captures all rows
        db.CreateIndex("Animal", "species");

        // Query with IN predicate on indexed property
        var result = conn.Query("MATCH (a:Animal) WHERE a.species IN ['Cat', 'Dog'] RETURN a.id AS id ORDER BY id");
        Assert.True(result.IsSuccess, $"Query failed: {result.ErrorMessage}");

        var ids = new List<string>();
        while (result.HasNext())
            ids.Add(result.GetNext().GetString("id")!);

        Assert.Equal(4, ids.Count);
        Assert.Equal("a1", ids[0]);
        Assert.Equal("a2", ids[1]);
        Assert.Equal("a3", ids[2]);
        Assert.Equal("a5", ids[3]);
    }

    [Fact]
    public void Query_StartsWith_WithIndex_ReturnsMatchingNodes()
    {
        // Pattern A: STARTS WITH on indexed property uses prefix scan
        using var db = BogDatabase.CreateInMemory();
        using var conn = new BogConnection(db);

        conn.BeginWriteTransaction();
        conn.EnsureNodeTable("City", new Dictionary<string, BogDb.Core.Common.LogicalTypeID>
        {
            ["id"] = BogDb.Core.Common.LogicalTypeID.STRING,
            ["name"] = BogDb.Core.Common.LogicalTypeID.STRING
        });
        conn.UpsertNode("City", "c1", new Dictionary<string, object> { ["id"] = "c1", ["name"] = "New York" });
        conn.UpsertNode("City", "c2", new Dictionary<string, object> { ["id"] = "c2", ["name"] = "New Orleans" });
        conn.UpsertNode("City", "c3", new Dictionary<string, object> { ["id"] = "c3", ["name"] = "Newark" });
        conn.UpsertNode("City", "c4", new Dictionary<string, object> { ["id"] = "c4", ["name"] = "Los Angeles" });
        conn.UpsertNode("City", "c5", new Dictionary<string, object> { ["id"] = "c5", ["name"] = "Nashville" });
        conn.Commit();

        db.CreateIndex("City", "name");

        var result = conn.Query("MATCH (c:City) WHERE c.name STARTS WITH 'New' RETURN c.id AS id ORDER BY id");
        Assert.True(result.IsSuccess, $"Query failed: {result.ErrorMessage}");

        var ids = new List<string>();
        while (result.HasNext())
            ids.Add(result.GetNext().GetString("id")!);

        // c1 (New York), c2 (New Orleans), c3 (Newark) — 3 results
        Assert.Equal(3, ids.Count);
        Assert.Equal("c1", ids[0]);
        Assert.Equal("c2", ids[1]);
        Assert.Equal("c3", ids[2]);
    }

    [Fact]
    public void Query_WhereWithInlineProps_ReturnsCorrectResults()
    {
        // Pattern B: WHERE predicate on indexed property when node also has inline properties
        using var db = BogDatabase.CreateInMemory();
        using var conn = new BogConnection(db);

        conn.BeginWriteTransaction();
        conn.EnsureNodeTable("Person", new Dictionary<string, BogDb.Core.Common.LogicalTypeID>
        {
            ["id"] = BogDb.Core.Common.LogicalTypeID.STRING,
            ["name"] = BogDb.Core.Common.LogicalTypeID.STRING,
            ["age"] = BogDb.Core.Common.LogicalTypeID.INT64
        });
        conn.UpsertNode("Person", "p1", new Dictionary<string, object> { ["id"] = "p1", ["name"] = "Alice", ["age"] = 30L });
        conn.UpsertNode("Person", "p2", new Dictionary<string, object> { ["id"] = "p2", ["name"] = "Alice", ["age"] = 25L });
        conn.UpsertNode("Person", "p3", new Dictionary<string, object> { ["id"] = "p3", ["name"] = "Bob", ["age"] = 30L });
        conn.Commit();

        db.CreateIndex("Person", "name");

        // MATCH (p:Person {age: 30}) WHERE p.name = 'Alice' — should use index on name
        var result = conn.Query("MATCH (p:Person {age: 30}) WHERE p.name = 'Alice' RETURN p.id AS id");
        Assert.True(result.IsSuccess, $"Query failed: {result.ErrorMessage}");

        var ids = new List<string>();
        while (result.HasNext())
            ids.Add(result.GetNext().GetString("id")!);

        // Only p1 matches both constraints
        Assert.Single(ids);
        Assert.Equal("p1", ids[0]);
    }
}
