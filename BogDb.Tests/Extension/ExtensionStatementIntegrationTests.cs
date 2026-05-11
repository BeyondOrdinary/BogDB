using System;
using System.Collections.Generic;
using System.IO;
using BogDb.Core.Extension;
using BogDb.Core.Main;
using Xunit;

namespace BogDb.Tests.Extension;

public class ExtensionStatementIntegrationTests
{
    private static string GetExtensionAssemblyPath(string projectName)
    {
        var targetDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = targetDirectory.Parent?.Name ?? "Debug";
        var solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

        return Path.Combine(solutionRoot, projectName, "bin", configuration, "net9.0", $"{projectName}.dll");
    }

    private sealed class StandaloneHelloFunction : ITableFunction
    {
        public string Name => "hello_ext";

        public IReadOnlyList<(string Name, string Type)>? Schema =>
            new List<(string Name, string Type)> { ("greeting", "STRING") };

        public IEnumerable<Dictionary<string, object?>> Invoke(IReadOnlyList<object?> args)
        {
            yield return new Dictionary<string, object?> { ["greeting"] = "hello" };
        }
    }

    [Fact]
    public void LoadExtensionStatement_LoadsJsonExtensionByName()
    {
        using var db = BogDatabase.CreateInMemory();
        using var conn = new BogConnection(db);

        Assert.False(db.FunctionRegistry.Contains("scan_json_array"));

        var result = conn.Query("LOAD EXTENSION json");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.True(db.FunctionRegistry.Contains("scan_json_array"));
        Assert.True(db.ExtensionManager.IsLoaded("json"));
    }

    [Fact]
    public void InstallExtensionStatement_LoadsJsonExtensionByName()
    {
        using var db = BogDatabase.CreateInMemory();
        using var conn = new BogConnection(db);

        var result = conn.Query("INSTALL json");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.True(db.FunctionRegistry.Contains("scan_json_array"));
    }

    [Fact]
    public void LoadExtensionStatement_LoadsExtensionByPath()
    {
        using var db = BogDatabase.CreateInMemory();
        using var conn = new BogConnection(db);

        var demoDllPath = GetExtensionAssemblyPath("BogDb.Extensions.Demo");
        var cypherPath = demoDllPath.Replace('\\', '/');

        var result = conn.Query($"LOAD EXTENSION '{cypherPath}'");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.True(db.ExtensionManager.IsLoaded("Demo"));
    }

    [Fact]
    public void UninstallExtensionStatement_RemovesJsonRegistrations()
    {
        using var db = BogDatabase.CreateInMemory();
        using var conn = new BogConnection(db);

        Assert.True(conn.Query("LOAD EXTENSION json").IsSuccess);
        Assert.True(db.FunctionRegistry.Contains("scan_json_array"));
        Assert.True(db.StandaloneTableFunctionRegistry.Contains("scan_json_array"));
        Assert.True(db.ScalarFunctionRegistry.Contains("json_valid"));

        var result = conn.Query("UNINSTALL json");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.False(db.ExtensionManager.IsLoaded("json"));
        Assert.False(db.FunctionRegistry.Contains("scan_json_array"));
        Assert.False(db.StandaloneTableFunctionRegistry.Contains("scan_json_array"));
        Assert.False(db.ScalarFunctionRegistry.Contains("json_valid"));
    }

    [Fact]
    public void CallResolvesStandaloneTableFunctionRegistry()
    {
        using var db = BogDatabase.CreateInMemory();
        db.StandaloneTableFunctionRegistry.Register(new StandaloneHelloFunction());
        using var conn = new BogConnection(db);

        var result = conn.Query("CALL hello_ext() RETURN *");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.True(result.HasNext());
        var row = result.GetNext();
        Assert.Equal("hello", row.GetString(0));
    }
}
