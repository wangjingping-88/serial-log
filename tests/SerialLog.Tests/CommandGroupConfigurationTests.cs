using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using SerialLog.App.ViewModels;
using SerialLog.Core.Commands;
using SerialLog.Core.Configuration;

namespace SerialLog.Tests;

public class CommandGroupConfigurationTests
{
    [Fact]
    public void Renaming_window_updates_every_groups_target_in_place_without_changing_selection()
    {
        var window = new SerialWindowViewModel("node", "串口 2");
        using var panel = new CommandPanelViewModel([window], _ => { });
        panel.AddCommandGroupCommand.Execute(null); panel.DuplicateCommandGroupCommand.Execute(null);
        var targets = panel.CommandGroups.Select(g => g.Targets.Single()).ToArray();
        targets[0].IsSelected = true; targets[1].IsSelected = false;
        window.Title = "测试设备中文";
        for (var i = 0; i < targets.Length; i++)
        {
            Assert.Same(targets[i], panel.CommandGroups[i].Targets.Single());
            Assert.Equal("测试设备中文", targets[i].Title);
        }
        Assert.True(targets[0].IsSelected); Assert.False(targets[1].IsSelected);
        window.Dispose();
    }

    [Fact]
    public void Copy_and_cross_workspace_import_are_deep_copies_with_new_identity_and_safe_targets()
    {
        using var panel = new CommandPanelViewModel([new("node", "节点")], _ => { });
        panel.AddCommandGroupCommand.Execute(null);
        var original = panel.SelectedCommandGroup!; original.Name = "测试"; original.Commands.Add("AT");
        original.Targets.Single().IsSelected = true; original.LoopCount = 2; original.InterpretEscapes = false;
        original.IsInfiniteLoop = true;
        panel.DuplicateCommandGroupCommand.Execute(null); var copy = panel.SelectedCommandGroup!;
        Assert.NotEqual(original.Id, copy.Id); Assert.Equal("AT", copy.Commands.Single());
        Assert.True(copy.Targets.Single().IsSelected); Assert.False(copy.InterpretEscapes);
        Assert.True(copy.IsInfiniteLoop);
        copy.Commands[0] = "COPY"; Assert.Equal("AT", original.Commands.Single());
        var source = original.ToConfig(); panel.ImportCommandGroups([source, source]);
        Assert.Equal(4, panel.CommandGroups.Select(g => g.Id).Distinct().Count());
        Assert.Equal(4, panel.CommandGroups.Select(g => g.Name).Distinct().Count());
        foreach (var imported in panel.CommandGroups.Skip(2))
        {
            Assert.Empty(imported.ToConfig().TargetIds); Assert.Equal(2, imported.LoopCount);
            Assert.True(imported.IsInfiniteLoop);
            Assert.False(imported.InterpretEscapes); imported.Commands.Clear();
        }
        Assert.Equal("AT", source.Commands.Single()); Assert.Equal("node", source.TargetIds.Single());
        Assert.Equal("AT", original.Commands.Single());
    }

    [Fact]
    public void Applying_settings_preserves_existing_group_identity_and_reorders_without_losing_queue()
    {
        using var panel = new CommandPanelViewModel([new("node", "节点")], _ => { });
        panel.AddCommandGroupCommand.Execute(null); var a = panel.SelectedCommandGroup!; a.LoopCount = 2;
        a.Targets.Single().IsSelected = true; a.Commands.Add("A");
        panel.DuplicateCommandGroupCommand.Execute(null); var b = panel.SelectedCommandGroup!;
        panel.GroupScheduler.AddToQueue(a);
        var edit = a.ToConfig(); edit.Name = "新名称"; edit.Commands[0] = "EDIT"; edit.IsInfiniteLoop = true;
        panel.ApplyCommandGroupSettings([b.ToConfig(), edit], a.Id);
        Assert.Same(a, panel.SelectedCommandGroup); Assert.Same(b, panel.CommandGroups[0]);
        Assert.Equal("EDIT", a.Commands.Single()); Assert.True(a.Targets.Single().IsSelected);
        Assert.True(a.IsInfiniteLoop);
        Assert.Equal("新名称", panel.GroupScheduler.Queue.Single().Name);
        panel.ApplyCommandGroupSettings([b.ToConfig()], b.Id);
        Assert.Empty(panel.GroupScheduler.Queue); Assert.Same(b, panel.SelectedCommandGroup);
    }

    [Fact]
    public void Smart_grouping_only_changes_draft_until_save_and_persists_without_reordering_queue()
    {
        WithDirectory(directory =>
        {
            using var a = new SerialWindowViewModel("a", "同名目标", refreshPortsOnCreate: false);
            using var b = new SerialWindowViewModel("b", "同名目标", refreshPortsOnCreate: false);
            using var original = new CommandPanelViewModel([a, b], _ => { });
            foreach (var (name, id) in new[] { ("A1", "a"), ("B1", "b"), ("A2", "a") })
            {
                var group = new CommandGroupEditorViewModel(new() { Name = name, Commands = [name], TargetIds = [id] });
                original.CommandGroups.Add(group);
            }
            original.SyncCommandGroupTargets();
            foreach (var group in original.CommandGroups) original.GroupScheduler.AddToQueue(group);
            var queue = original.GroupScheduler.Queue.ToArray();
            var unchangedConfigs = original.CommandGroups.ToDictionary(g => g.Id, g => System.Text.Json.JsonSerializer.Serialize(g.ToConfig()));
            using var draft = new CommandPanelViewModel(original.SerialWindows, _ => { });
            foreach (var group in original.CommandGroups) draft.CommandGroups.Add(new(group.ToConfig()));
            draft.SyncCommandGroupTargets(); draft.SelectedCommandGroup = draft.CommandGroups[2];
            var selectedId = draft.SelectedCommandGroup.Id;
            draft.GroupScheduler.SmartGroupCommand.Execute(null);
            Assert.Equal(new[] { "A1", "A2", "B1" }, draft.CommandGroups.Select(g => g.Name));
            Assert.Equal(new[] { "A1", "B1", "A2" }, original.CommandGroups.Select(g => g.Name));
            original.ApplyCommandGroupSettings(draft.CommandGroups.Select(g => g.ToConfig()).ToArray(), selectedId);
            Assert.Equal(new[] { "A1", "A2", "B1" }, original.CommandGroups.Select(g => g.Name));
            Assert.Equal(selectedId, original.SelectedCommandGroup!.Id); Assert.Equal(queue, original.GroupScheduler.Queue);
            foreach (var group in original.CommandGroups)
                Assert.Equal(unchangedConfigs[group.Id], System.Text.Json.JsonSerializer.Serialize(group.ToConfig()));
            var path = Path.Combine(directory, "workspace.json");
            WorkspaceConfigStore.Save(path, new() { CommandGroups = original.CommandGroups.Select(g => g.ToConfig()).ToList(),
                CommandGroupQueue = queue.Select(q => q.ToConfig()).ToList(), SelectedCommandGroupId = selectedId });
            var restored = WorkspaceConfigStore.Load(path);
            Assert.Equal(new[] { "A1", "A2", "B1" }, restored.CommandGroups.Select(g => g.Name));
            Assert.Equal(queue.Select(q => q.Id), restored.CommandGroupQueue.Select(q => q.Id));
        });
    }

    [Fact]
    public void Catalog_v3_migration_assigns_stable_group_ids_and_preserves_exact_backup()
    {
        WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "workspace.json");
            var workspace = new TestWorkspaceConfig { Configuration = new() { SelectedCommandGroupName = "原命令组", CommandGroups = [new() { Name = "原命令组", Commands = [@"AT\r\n中文"], TargetIds = ["node"] }] } };
            var catalog = new WorkspaceCatalog { CurrentWorkspaceId = workspace.Id, Workspaces = [workspace] };
            WorkspaceCatalogStore.Save(path, catalog);
            var json = JsonNode.Parse(File.ReadAllText(path))!; json["FormatVersion"] = 3;
            var config = json["Workspaces"]![0]!["Configuration"]!;
            config["CommandGroups"]![0]!.AsObject().Remove("Id"); config.AsObject().Remove("SelectedCommandGroupId");
            config.AsObject().Remove("InterpretCommandEscapes"); config["CommandGroups"]![0]!.AsObject().Remove("InterpretEscapes");
            File.WriteAllText(path, json.ToJsonString()); var original = File.ReadAllBytes(path);
            var migrated = WorkspaceCatalogStore.Load(path); var current = migrated.Workspaces.Single().Configuration;
            Assert.Equal(5, migrated.FormatVersion);
            Assert.Equal(current.CommandGroups.Single().Id, current.SelectedCommandGroupId);
            Assert.True(current.InterpretCommandEscapes && current.CommandGroups.Single().InterpretEscapes);
            Assert.Equal(original, File.ReadAllBytes(Assert.Single(Directory.GetFiles(directory, "*.bak"))));
            Assert.Equal(current.CommandGroups.Single().Id, WorkspaceCatalogStore.Load(path).Workspaces.Single().Configuration.CommandGroups.Single().Id);
            Assert.Single(Directory.GetFiles(directory, "*.bak"));
        });
    }

    [Fact]
    public void Queue_and_selection_persist_by_id_and_workspace_copy_remaps_all_identities()
    {
        WithDirectory(directory =>
        {
            var group = new CommandGroupConfig { Name = "测试", Commands = ["AT"], TargetIds = ["node"], LoopCount = 2, IsInfiniteLoop = true, InterpretEscapes = false };
            var workspace = new TestWorkspaceConfig { Configuration = new()
            {
                SerialWindows = [new() { Id = "node" }], CommandGroups = [group], SelectedCommandGroupId = group.Id,
                CommandGroupQueue = [new() { GroupId = group.Id }, new() { GroupId = group.Id }],
                ShowSentCommands = false, InterpretCommandEscapes = false, SingleCommandInfiniteLoop = true
            } };
            var path = Path.Combine(directory, "workspace.json");
            WorkspaceCatalogStore.Save(path, new() { CurrentWorkspaceId = workspace.Id, Workspaces = [workspace] });
            var loaded = WorkspaceCatalogStore.Load(path).Workspaces.Single();
            Assert.Equal(group.Id, loaded.Configuration.SelectedCommandGroupId);
            Assert.True(loaded.Configuration.CommandGroups.Single().IsInfiniteLoop && loaded.Configuration.SingleCommandInfiniteLoop);
            Assert.Equal(workspace.Configuration.CommandGroupQueue.Select(q => q.Id), loaded.Configuration.CommandGroupQueue.Select(q => q.Id));
            var clone = WorkspaceCatalogStore.Clone(loaded, "副本"); var clonedGroup = clone.Configuration.CommandGroups.Single();
            Assert.NotEqual(group.Id, clonedGroup.Id); Assert.Equal(clonedGroup.Id, clone.Configuration.SelectedCommandGroupId);
            Assert.True(clonedGroup.IsInfiniteLoop && clone.Configuration.SingleCommandInfiniteLoop);
            Assert.All(clone.Configuration.CommandGroupQueue, q => Assert.Equal(clonedGroup.Id, q.GroupId));
            Assert.Empty(clone.Configuration.CommandGroupQueue.Select(q => q.Id).Intersect(loaded.Configuration.CommandGroupQueue.Select(q => q.Id)));
            Assert.Equal(clone.Configuration.SerialWindows.Single().Id, clonedGroup.TargetIds.Single());
            Assert.False(clone.Configuration.ShowSentCommands || clone.Configuration.InterpretCommandEscapes || clonedGroup.InterpretEscapes);
            clonedGroup.Commands.Clear(); Assert.Single(group.Commands); Assert.Single(loaded.Configuration.CommandGroups.Single().Commands);
        });
    }

    [Fact]
    public void Invalid_queue_reference_does_not_replace_saved_configuration()
    {
        WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "workspace.json"); var config = new WorkspaceConfig();
            WorkspaceConfigStore.Save(path, config); var before = File.ReadAllBytes(path);
            config.CommandGroupQueue.Add(new() { GroupId = Guid.NewGuid().ToString("N") });
            Assert.Throws<InvalidDataException>(() => WorkspaceConfigStore.Save(path, config));
            Assert.Equal(before, File.ReadAllBytes(path)); Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        });
    }

    private static void WithDirectory(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "group-config-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try { action(directory); } finally { Directory.Delete(directory, true); }
    }
}
