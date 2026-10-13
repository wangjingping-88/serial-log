using System.Text.Json.Nodes;
using SerialLog.Core.Configuration;
using SerialLog.Core.Firmware;

namespace SerialLog.Tests;

public class YmodemMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ymodem-migration-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_root, "workspace.json");
    public YmodemMigrationTests() => Directory.CreateDirectory(_root);
    private byte[] WriteV2(string mode = "Gateway")
    {
        var workspace = new TestWorkspaceConfig { Configuration = new WorkspaceConfig
        { SerialWindows = [new() { Id = "COM7", IsSelectedForSend = true }], YmodemFilePath = "中文固件.bin", CommandHistory = ["历史"] } };
        WorkspaceCatalogStore.Save(FilePath, new WorkspaceCatalog { CurrentWorkspaceId = workspace.Id, Workspaces = [workspace] });
        var json = JsonNode.Parse(File.ReadAllText(FilePath))!; json["FormatVersion"] = 2;
        var config = json["Workspaces"]![0]!["Configuration"]!;
        config.AsObject().Remove("YmodemProfiles"); config.AsObject().Remove("YmodemSelectedProfileId");
        config["YmodemMode"] = mode; config["YmodemTargetId"] = "COM7";
        File.WriteAllText(FilePath, json.ToJsonString()); return File.ReadAllBytes(FilePath);
    }
    [Theory]
    [InlineData("Gateway", YmodemMode.Custom)]
    [InlineData("Standard", YmodemMode.Standard)]
    public void V2_migration_backs_up_exact_config_and_preserves_selection_and_mode(string oldMode, YmodemMode expected)
    {
        var bytes = WriteV2(oldMode); var catalog = WorkspaceCatalogStore.Load(FilePath);
        Assert.Equal(3, catalog.FormatVersion);
        var config = catalog.Workspaces[0].Configuration;
        Assert.Equal(expected, config.YmodemMode); Assert.True(config.SerialWindows[0].IsSelectedForSend);
        Assert.Equal("中文固件.bin", config.YmodemFilePath); Assert.Equal("历史", Assert.Single(config.CommandHistory));
        Assert.Equal("UCCHIP-小网关", Assert.Single(config.YmodemProfiles).Name);
        Assert.Equal(config.YmodemProfiles[0].Id, config.YmodemSelectedProfileId);
        Assert.Equal(bytes, File.ReadAllBytes(Assert.Single(Directory.GetFiles(_root, "*.bak"))));
        WorkspaceCatalogStore.Load(FilePath); Assert.Single(Directory.GetFiles(_root, "*.bak"));
    }
    [Fact]
    public void Failed_migration_retains_original_and_backup()
    {
        var bytes = WriteV2();
        using (var locked = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<IOException>(() => WorkspaceCatalogStore.Load(FilePath));
        Assert.Equal(bytes, File.ReadAllBytes(FilePath));
        Assert.Equal(bytes, File.ReadAllBytes(Assert.Single(Directory.GetFiles(_root, "*.bak"))));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }
    [Fact]
    public void Removing_all_profiles_or_editing_example_is_not_undone_on_restart()
    {
        var workspace = new TestWorkspaceConfig(); workspace.Configuration.YmodemProfiles.Clear(); workspace.Configuration.YmodemSelectedProfileId = null;
        var catalog = new WorkspaceCatalog { CurrentWorkspaceId = workspace.Id, Workspaces = [workspace] };
        WorkspaceCatalogStore.Save(FilePath, catalog);
        var restored = WorkspaceCatalogStore.Load(FilePath).Workspaces[0].Configuration;
        Assert.Empty(restored.YmodemProfiles); Assert.Null(restored.YmodemSelectedProfileId);
        workspace.Configuration.YmodemProfiles.Add(YmodemRuleProfiles.CreateUcchipExample()); workspace.Configuration.YmodemProfiles[0].Name = "用户方案";
        WorkspaceCatalogStore.Save(FilePath, catalog);
        Assert.Equal("用户方案", WorkspaceCatalogStore.Load(FilePath).Workspaces[0].Configuration.YmodemProfiles[0].Name);
    }
    [Fact]
    public void Flat_gateway_config_also_migrates_atomically_and_invalid_profile_save_preserves_file()
    {
        File.WriteAllText(FilePath, "{\"YmodemMode\":\"Gateway\",\"YmodemFilePath\":\"old.bin\"}");
        var old = File.ReadAllBytes(FilePath); var config = WorkspaceConfigStore.Load(FilePath);
        Assert.Equal(YmodemMode.Custom, config.YmodemMode); Assert.Equal("old.bin", config.YmodemFilePath);
        Assert.Equal(old, File.ReadAllBytes(Assert.Single(Directory.GetFiles(_root, "*.bak"))));
        var good = File.ReadAllBytes(FilePath); config.YmodemProfiles[0].Before[0].Send.TemplateId = "missing";
        Assert.Throws<InvalidDataException>(() => WorkspaceConfigStore.Save(FilePath, config));
        Assert.Equal(good, File.ReadAllBytes(FilePath));
    }
    public void Dispose() => Directory.Delete(_root, true);
}
