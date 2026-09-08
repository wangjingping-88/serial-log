using System.Text.Json;
using SerialLog.Core.Configuration;

namespace SerialLog.Tests;

public sealed class WorkspaceCatalogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "workspace-catalog-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "workspace.json");
    public WorkspaceCatalogTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Legacy_migration_backs_up_exact_bytes_and_preserves_configuration()
    {
        WorkspaceConfigStore.Save(FilePath, new WorkspaceConfig
        {
            LocalPcId = "pc", ThemeColor = "#123456", PageCount = 3, CommandHistory = ["AT+TEST"],
            SerialWindows = [new() { Id = "original", Title = "网关", PortName = "COM7", BaudRate = 460800, AutoSaveEnabled = true }],
            CommandGroups = [new() { TargetIds = ["original"], Commands = ["AT"] }],
            ShortcutBindings = [new() { ActionId = "test", Gesture = "Ctrl+Q" }]
        });
        var original = File.ReadAllBytes(FilePath);
        var migrated = WorkspaceCatalogStore.Load(FilePath);
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(Directory.GetFiles(_directory, "*.bak"))));
        var workspace = Assert.Single(migrated.Workspaces);
        Assert.Equal("默认测试", workspace.Name);
        Assert.Equal(workspace.Id, migrated.CurrentWorkspaceId);
        Assert.Equal("original", workspace.Configuration.SerialWindows.Single().Id);
        Assert.False(workspace.Configuration.SerialWindows.Single().IsShared);
        Assert.Equal("#123456", workspace.Configuration.ThemeColor);
        Assert.Equal("AT+TEST", workspace.Configuration.CommandHistory.Single());
        Assert.Equal("original", workspace.Configuration.CommandGroups.Single().TargetIds.Single());
        Assert.Equal(3, workspace.Configuration.PageCount);
        Assert.Equal(workspace.Id, WorkspaceCatalogStore.Load(FilePath).CurrentWorkspaceId);
        Assert.Single(Directory.GetFiles(_directory, "*.bak"));
    }

    [Fact]
    public void Clone_has_independent_ids_targets_and_mutable_configuration()
    {
        var original = new TestWorkspaceConfig { Configuration = new()
        {
            SerialWindows = [new() { Id = "first", IsShared = true, Title = "node" }],
            ExpandedWindowIds = ["first"], CommandGroups = [new() { TargetIds = ["first"], Commands = ["AT"] }],
            CommandHistory = ["history"]
        } };
        var clone = WorkspaceCatalogStore.Clone(original, "复制");
        Assert.NotEqual(original.Id, clone.Id);
        var window = Assert.Single(clone.Configuration.SerialWindows);
        Assert.NotEqual("first", window.Id);
        Assert.False(window.IsShared);
        Assert.Equal(window.Id, clone.Configuration.CommandGroups.Single().TargetIds.Single());
        Assert.Equal(window.Id, clone.Configuration.ExpandedWindowIds.Single());
        clone.Configuration.CommandGroups[0].Commands.Clear();
        clone.Configuration.CommandHistory.Clear();
        Assert.Single(original.Configuration.CommandGroups[0].Commands);
        Assert.Single(original.Configuration.CommandHistory);
    }

    [Fact]
    public void Invalid_or_future_config_never_overwrites_original()
    {
        File.WriteAllText(FilePath, "{ broken json");
        Assert.ThrowsAny<JsonException>(() => WorkspaceCatalogStore.Load(FilePath));
        Assert.Equal("{ broken json", File.ReadAllText(FilePath));
        File.WriteAllText(FilePath, "{\"FormatVersion\":999,\"Workspaces\":[]}");
        Assert.Throws<InvalidDataException>(() => WorkspaceCatalogStore.Load(FilePath));
        Assert.Contains("999", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Failed_atomic_migration_retains_original_and_backup()
    {
        WorkspaceConfigStore.Save(FilePath, new WorkspaceConfig { CommandHistory = ["keep"] });
        var original = File.ReadAllBytes(FilePath);
        using (var locked = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<IOException>(() => WorkspaceCatalogStore.Load(FilePath));
        Assert.Equal(original, File.ReadAllBytes(FilePath));
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(Directory.GetFiles(_directory, "*.bak"))));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void New_catalog_is_one_blank_page_and_invalid_save_is_rejected()
    {
        var catalog = WorkspaceCatalogStore.Load(FilePath);
        Assert.Empty(Assert.Single(catalog.Workspaces).Configuration.SerialWindows);
        Assert.Equal(1, catalog.Workspaces[0].Configuration.PageCount);
        WorkspaceCatalogStore.Save(FilePath, catalog);
        var before = File.ReadAllText(FilePath);
        catalog.CurrentWorkspaceId = "missing";
        Assert.Throws<InvalidDataException>(() => WorkspaceCatalogStore.Save(FilePath, catalog));
        Assert.Equal(before, File.ReadAllText(FilePath));
    }
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
