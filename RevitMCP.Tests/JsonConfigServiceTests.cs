using RevitMCP.Addin.Configuration;
using System.Text.Json.Nodes;
using Xunit;

namespace RevitMCP.Tests;

public class JsonConfigServiceTests
{
    private static string TempFile(string? content = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rkmcp_cfg_{Guid.NewGuid():N}.json");
        if (content != null) File.WriteAllText(path, content);
        return path;
    }

    private static JsonConfigService CreateSvc() => new();

    // ── Read ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Read_MissingFile_CreateIfMissingFalse_ReturnsError()
    {
        var svc = CreateSvc();
        var path = Path.Combine(Path.GetTempPath(), $"rkmcp_missing_{Guid.NewGuid():N}.json");
        var (config, error) = svc.Read(path, createIfMissing: false);

        Assert.Null(config);
        Assert.NotNull(error);
        Assert.Contains("not found", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_MissingFile_CreateIfMissingTrue_CreatesEmptyObjectFile()
    {
        var path = TempFile(); // creates empty file
        File.Delete(path);
        try
        {
            var svc = CreateSvc();
            var (config, error) = svc.Read(path, createIfMissing: true);

            Assert.Null(error);
            Assert.NotNull(config);
            Assert.True(File.Exists(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Read_ValidJsonObject_ReturnsObject()
    {
        var path = TempFile("{\"key\":\"value\"}");
        try
        {
            var svc = CreateSvc();
            var (config, error) = svc.Read(path);

            Assert.Null(error);
            Assert.NotNull(config);
            Assert.True(config!.ContainsKey("key"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Read_InvalidJson_ReturnsError()
    {
        var path = TempFile("not valid json {{{");
        try
        {
            var svc = CreateSvc();
            var (config, error) = svc.Read(path);

            Assert.Null(config);
            Assert.NotNull(error);
        }
        finally { File.Delete(path); }
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Write_ValidJson_WritesSucessfully()
    {
        var path = TempFile();
        File.Delete(path);
        try
        {
            var svc = CreateSvc();
            var (success, error, backup) = svc.Write(path, "{\"a\":1}", backupBeforeOverwrite: false);

            Assert.True(success);
            Assert.Null(error);
            Assert.Null(backup);
            Assert.True(File.Exists(path));
            Assert.Contains("\"a\"", File.ReadAllText(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Write_InvalidJson_ReturnsError()
    {
        var path = TempFile("{\"existing\":1}");
        try
        {
            var svc = CreateSvc();
            var (success, error, backup) = svc.Write(path, "{{invalid", backupBeforeOverwrite: false);

            Assert.False(success);
            Assert.NotNull(error);
            // Original file must be unchanged
            Assert.Contains("existing", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Write_WithBackupBeforeOverwrite_CreatesBackupFile()
    {
        var path = TempFile("{\"original\":true}");
        try
        {
            var svc = CreateSvc();
            var (success, error, backupPath) = svc.Write(path, "{\"replaced\":true}", backupBeforeOverwrite: true);

            Assert.True(success);
            Assert.NotNull(backupPath);
            Assert.True(File.Exists(backupPath!));
            Assert.Contains("original", File.ReadAllText(backupPath!));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            var dir = Path.GetDirectoryName(path)!;
            var stem = Path.GetFileNameWithoutExtension(path);
            foreach (var f in Directory.GetFiles(dir, $"{stem}_backup_*.json"))
                try { File.Delete(f); } catch { }
        }
    }

    // ── Update ────────────────────────────────────────────────────────────────

    [Fact]
    public void Update_SingleTopLevelProperty_UpdatesValue()
    {
        var path = TempFile("{\"key\":\"old\"}");
        try
        {
            var svc = CreateSvc();
            var (success, error, _, applied) = svc.Update(
                path,
                new Dictionary<string, JsonNode?> { ["key"] = JsonValue.Create("new") },
                backupBeforeOverwrite: false);

            Assert.True(success);
            Assert.Null(error);
            Assert.Contains("key", applied);

            var (config, _) = svc.Read(path);
            Assert.Equal("new", config!["key"]!.GetValue<string>());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Update_DotPath_CreatesNestedProperty()
    {
        var path = TempFile("{}");
        try
        {
            var svc = CreateSvc();
            var (success, error, _, _) = svc.Update(
                path,
                new Dictionary<string, JsonNode?> { ["$.excel.defaultBackupBeforeSave"] = JsonValue.Create("true") },
                backupBeforeOverwrite: false);

            Assert.True(success);
            var (config, _) = svc.Read(path);
            Assert.NotNull(config!["excel"]);
            Assert.Equal("true", config["excel"]!["defaultBackupBeforeSave"]!.GetValue<string>());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Update_CreateIfMissingFalse_MissingFile_ReturnsError()
    {
        var svc = CreateSvc();
        var path = Path.Combine(Path.GetTempPath(), $"rkmcp_missing_{Guid.NewGuid():N}.json");
        var (success, error, _, _) = svc.Update(
            path,
            new Dictionary<string, JsonNode?> { ["key"] = JsonValue.Create("val") },
            backupBeforeOverwrite: false,
            createIfMissing: false);

        Assert.False(success);
        Assert.NotNull(error);
    }

    [Fact]
    public void AtomicWrite_InvalidJsonDoesNotCorruptExistingFile()
    {
        var path = TempFile("{\"safe\":true}");
        try
        {
            var svc = CreateSvc();
            // Write invalid JSON — should fail without touching the original
            var (success, error, _) = svc.Write(path, "corrupt {{{{", backupBeforeOverwrite: false);

            Assert.False(success);
            // Temp file must be gone
            Assert.False(File.Exists(path + ".tmp"));
            // Original must be intact
            var content = File.ReadAllText(path);
            Assert.Contains("safe", content);
        }
        finally { File.Delete(path); }
    }

    // ── JSON type preservation ────────────────────────────────────────────────

    [Fact]
    public void Update_PreservesBooleanType()
    {
        var path = TempFile("{}");
        try
        {
            var svc = CreateSvc();
            var (success, _, _, _) = svc.Update(
                path,
                new Dictionary<string, JsonNode?> { ["$.excel.defaultBackupBeforeSave"] = JsonValue.Create(true) },
                backupBeforeOverwrite: false);

            Assert.True(success);
            var (config, _) = svc.Read(path);
            Assert.True(config!["excel"]!["defaultBackupBeforeSave"]!.GetValue<bool>());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Update_PreservesIntegerType()
    {
        var path = TempFile("{}");
        try
        {
            var svc = CreateSvc();
            var (success, _, _, _) = svc.Update(
                path,
                new Dictionary<string, JsonNode?> { ["$.limits.maxRows"] = JsonValue.Create(5000) },
                backupBeforeOverwrite: false);

            Assert.True(success);
            var (config, _) = svc.Read(path);
            Assert.Equal(5000, config!["limits"]!["maxRows"]!.GetValue<int>());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Update_PreservesNullValue()
    {
        var path = TempFile("{\"key\":\"old\"}");
        try
        {
            var svc = CreateSvc();
            var (success, _, _, _) = svc.Update(
                path,
                new Dictionary<string, JsonNode?> { ["key"] = null },
                backupBeforeOverwrite: false);

            Assert.True(success);
            var (config, _) = svc.Read(path);
            Assert.True(config!.ContainsKey("key"));
            Assert.Null(config["key"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Update_PreservesArray()
    {
        var path = TempFile("{}");
        try
        {
            var svc = CreateSvc();
            var arr = JsonNode.Parse("[\"EL\",\"EN\",\"EA\"]");
            var (success, _, _, _) = svc.Update(
                path,
                new Dictionary<string, JsonNode?> { ["$.naming.allowedDisciplines"] = arr },
                backupBeforeOverwrite: false);

            Assert.True(success);
            var (config, _) = svc.Read(path);
            var node = config!["naming"]!["allowedDisciplines"] as JsonArray;
            Assert.NotNull(node);
            Assert.Equal(3, node!.Count);
            Assert.Equal("EL", node[0]!.GetValue<string>());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Update_PreservesNestedObject()
    {
        var path = TempFile("{}");
        try
        {
            var svc = CreateSvc();
            var obj = JsonNode.Parse("{\"enabled\":true,\"count\":2}");
            var (success, _, _, _) = svc.Update(
                path,
                new Dictionary<string, JsonNode?> { ["$.nested.object"] = obj },
                backupBeforeOverwrite: false);

            Assert.True(success);
            var (config, _) = svc.Read(path);
            var nested = config!["nested"]!["object"] as JsonObject;
            Assert.NotNull(nested);
            Assert.True(nested!["enabled"]!.GetValue<bool>());
            Assert.Equal(2, nested["count"]!.GetValue<int>());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Update_MultipleTypes_AllPreserved()
    {
        var path = TempFile("{}");
        try
        {
            var svc = CreateSvc();
            var (success, _, _, applied) = svc.Update(
                path,
                new Dictionary<string, JsonNode?>
                {
                    ["$.excel.defaultBackupBeforeSave"] = JsonValue.Create(true),
                    ["$.limits.maxRows"] = JsonValue.Create(5000),
                    ["$.naming.prefix"] = JsonValue.Create("RK"),
                },
                backupBeforeOverwrite: false);

            Assert.True(success);
            Assert.Equal(3, applied.Count);
            var (config, _) = svc.Read(path);
            Assert.True(config!["excel"]!["defaultBackupBeforeSave"]!.GetValue<bool>());
            Assert.Equal(5000, config["limits"]!["maxRows"]!.GetValue<int>());
            Assert.Equal("RK", config["naming"]!["prefix"]!.GetValue<string>());
        }
        finally { File.Delete(path); }
    }
}
