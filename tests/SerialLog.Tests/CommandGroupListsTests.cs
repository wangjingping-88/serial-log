using System.Text.Json.Nodes;
using SerialLog.App.ViewModels;
using SerialLog.Core.Configuration;

namespace SerialLog.Tests;

public class CommandGroupListsTests
{
    [Fact]
    public void Shared_members_use_one_group_identity_and_switching_lists_preserves_queue_and_checks()
    {
        using var panel = new CommandPanelViewModel([], _ => { }, (_, _) => true);
        panel.AddCommandGroupListCommand.Execute(null); panel.SelectedCommandGroupList!.Name = "AP";
        panel.AddCommandGroupCommand.Execute(null); var shared = panel.SelectedCommandGroup!;
        shared.Name = "共享命令"; shared.Commands.Add("AT"); shared.IsSelectedForRun = true;
        panel.GroupScheduler.AddToQueue(shared); var queue = panel.GroupScheduler.Queue.Single();
        var original = panel.CommandGroupLists.Single();
        panel.AddCommandGroupListCommand.Execute(null); var second = panel.SelectedCommandGroupList!; second.Name = "IOT";
        Assert.Empty(panel.VisibleCommandGroups);
        shared.ListIds.Add(second.Id);
        Assert.Same(shared, Assert.Single(panel.VisibleCommandGroups));
        Assert.Equal(1, second.Count); Assert.Equal(1, original.Count);
        panel.SelectedCommandGroupList = original;
        Assert.Same(shared, Assert.Single(panel.VisibleCommandGroups)); Assert.True(shared.IsSelectedForRun);
        shared.Commands[0] = "AT+EDIT";
        panel.SelectedCommandGroupList = second;
        Assert.Equal("AT+EDIT", Assert.Single(panel.VisibleCommandGroups).Commands.Single());
        Assert.Same(queue, Assert.Single(panel.GroupScheduler.Queue));
        Assert.Equal(shared.Id, queue.GroupId); Assert.Single(panel.CommandGroups);
        panel.RemoveCurrentListMemberCommand.Execute(null);
        Assert.Empty(panel.VisibleCommandGroups); Assert.Single(panel.CommandGroups); Assert.Single(panel.GroupScheduler.Queue);
        Assert.Equal(new[] { original.Id }, shared.ListIds);
        panel.SelectedCommandGroupList = panel.AllCommandGroupsList;
        Assert.Same(shared, Assert.Single(panel.VisibleCommandGroups));
    }

    [Fact]
    public void Clearing_and_deleting_named_lists_remove_only_memberships_and_keep_orphans_accessible()
    {
        using var panel = new CommandPanelViewModel([], _ => { }, (_, _) => true);
        panel.AddCommandGroupListCommand.Execute(null); panel.SelectedCommandGroupList!.Name = "AP";
        panel.AddCommandGroupCommand.Execute(null); var first = panel.SelectedCommandGroup!;
        panel.DuplicateCommandGroupCommand.Execute(null); var second = panel.SelectedCommandGroup!;
        foreach (var group in panel.CommandGroups) { group.Commands.Add("AT"); panel.GroupScheduler.AddToQueue(group); }
        var defaultList = panel.CommandGroupLists.Single();
        panel.SelectedCommandGroupList = panel.AllCommandGroupsList;
        Assert.False(panel.DeleteCommandGroupListCommand.CanExecute(null));
        panel.SelectedCommandGroupList = defaultList;
        Assert.True(panel.DeleteCommandGroupListCommand.CanExecute(null));
        panel.AddCommandGroupListCommand.Execute(null); var extra = panel.SelectedCommandGroupList!;
        first.ListIds.Add(extra.Id); second.ListIds.Add(extra.Id);
        panel.ClearCommandGroupsCommand.Execute(null);
        Assert.Empty(panel.VisibleCommandGroups); Assert.Equal(2, panel.CommandGroups.Count); Assert.Equal(2, panel.GroupScheduler.Queue.Count);
        first.ListIds.Add(extra.Id);
        panel.SelectedCommandGroupList = defaultList; panel.DeleteCommandGroupListCommand.Execute(null);
        Assert.DoesNotContain(defaultList, panel.CommandGroupLists);
        Assert.Equal(new[] { extra.Id }, first.ListIds); Assert.Empty(second.ListIds);
        Assert.Same(panel.AllCommandGroupsList, panel.SelectedCommandGroupList);
        Assert.Equal(2, panel.VisibleCommandGroups.Count); Assert.Equal(2, panel.GroupScheduler.Queue.Count);
        Assert.False(panel.DeleteCommandGroupListCommand.CanExecute(null));
        panel.SelectedCommandGroupList = extra; panel.DeleteCommandGroupListCommand.Execute(null);
        Assert.Empty(panel.CommandGroupLists); Assert.Empty(first.ListIds);
        Assert.Equal(2, panel.VisibleCommandGroups.Count); Assert.Equal(2, panel.GroupScheduler.Queue.Count);
    }

    [Fact]
    public void Membership_checkboxes_support_multiple_lists_and_search_does_not_delete_hidden_members()
    {
        using var panel = new CommandPanelViewModel([], _ => { });
        panel.AddCommandGroupListCommand.Execute(null); panel.SelectedCommandGroupList!.Name = "AP";
        panel.AddCommandGroupCommand.Execute(null); var a = panel.SelectedCommandGroup!; a.Name = "AP运行";
        panel.AddCommandGroupCommand.Execute(null); var b = panel.SelectedCommandGroup!; b.Name = "IOT运行";
        panel.AddCommandGroupListCommand.Execute(null); var iot = panel.SelectedCommandGroupList!;
        panel.SelectedCommandGroupList = panel.AllCommandGroupsList; panel.SelectedCommandGroup = b;
        var membership = panel.CommandGroupMemberships.Single(member => member.List == iot);
        membership.IsSelected = true;
        Assert.Equal(2, b.ListIds.Count); Assert.Equal(1, iot.Count);
        panel.GroupListSearchText = "AP";
        Assert.Same(a, Assert.Single(panel.VisibleCommandGroups)); Assert.Equal(2, panel.CommandGroups.Count);
        panel.GroupListSearchText = string.Empty; Assert.Equal(2, panel.VisibleCommandGroups.Count);
        panel.SelectedCommandGroupList = iot;
        Assert.Same(b, Assert.Single(panel.VisibleCommandGroups)); Assert.Empty(panel.GroupListSearchText);
        panel.DuplicateCommandGroupCommand.Execute(null);
        Assert.Equal(b.ListIds, panel.SelectedCommandGroup!.ListIds);
        panel.ImportCommandGroups([a.ToConfig()]);
        Assert.Equal(new[] { iot.Id }, panel.SelectedCommandGroup!.ListIds);
    }

    [Fact]
    public void List_editing_is_a_draft_and_saving_preserves_runtime_group_and_queue_objects()
    {
        using var original = new CommandPanelViewModel([], _ => { });
        original.AddCommandGroupListCommand.Execute(null); original.SelectedCommandGroupList!.Name = "AP";
        original.AddCommandGroupCommand.Execute(null); var group = original.SelectedCommandGroup!;
        group.Commands.Add("AT"); original.GroupScheduler.AddToQueue(group); var queue = original.GroupScheduler.Queue.Single();
        using var draft = new CommandPanelViewModel([], _ => { });
        draft.LoadCommandGroupLists(original.CommandGroupLists.Select(list => list.ToConfig()), string.Empty);
        foreach (var g in original.CommandGroups) draft.CommandGroups.Add(new(g.ToConfig()));
        draft.AddCommandGroupListCommand.Execute(null); var deviceList = draft.SelectedCommandGroupList!; deviceList.Name = "设备中文";
        draft.CommandGroups.Single().ListIds.Add(deviceList.Id);
        Assert.Single(original.CommandGroupLists); Assert.Single(group.ListIds);
        original.ApplyCommandGroupSettings(draft.CommandGroups.Select(g => g.ToConfig()).ToArray(), group.Id,
            draft.CommandGroupLists.Select(list => list.ToConfig()).ToArray(), deviceList.Id);
        Assert.Same(group, Assert.Single(original.VisibleCommandGroups)); Assert.Same(queue, Assert.Single(original.GroupScheduler.Queue));
        Assert.Equal("设备中文", original.SelectedCommandGroupList!.Name); Assert.Equal(2, group.ListIds.Count);
    }

    [Fact]
    public void Catalog_v4_migration_preserves_groups_and_queue_and_backs_up_before_saving_v5()
    {
        WithDirectory(directory =>
        {
            var group = new CommandGroupConfig { Name = "原设备命令", Commands = ["AT"] };
            var workspace = new TestWorkspaceConfig { Configuration = new() { CommandGroups = [group], CommandGroupQueue = [new() { GroupId = group.Id }] } };
            var path = Path.Combine(directory, "workspace.json");
            WorkspaceCatalogStore.Save(path, new() { CurrentWorkspaceId = workspace.Id, Workspaces = [workspace] });
            var json = JsonNode.Parse(File.ReadAllText(path))!; json["FormatVersion"] = 4;
            var config = json["Workspaces"]![0]!["Configuration"]!.AsObject();
            config.Remove("CommandGroupLists"); config.Remove("SelectedCommandGroupListId"); config["CommandGroups"]![0]!.AsObject().Remove("ListIds");
            File.WriteAllText(path, json.ToJsonString()); var before = File.ReadAllBytes(path);
            var migrated = WorkspaceCatalogStore.Load(path); var result = migrated.Workspaces.Single().Configuration;
            Assert.Equal(5, migrated.FormatVersion); Assert.Equal(group.Id, result.CommandGroups.Single().Id);
            Assert.Empty(result.CommandGroupLists); Assert.Empty(result.CommandGroups.Single().ListIds);
            Assert.Equal(group.Id, result.CommandGroupQueue.Single().GroupId);
            Assert.Equal(before, File.ReadAllBytes(Assert.Single(Directory.GetFiles(directory, "*.bak"))));
            WorkspaceCatalogStore.Load(path); Assert.Single(Directory.GetFiles(directory, "*.bak"));
        });
    }

    [Fact]
    public void Persistence_and_workspace_copy_keep_shared_memberships_but_remap_list_and_group_ids()
    {
        WithDirectory(directory =>
        {
            var a = new CommandGroupListConfig { Name = "AP" }; var b = new CommandGroupListConfig { Name = "其他设备" };
            var group = new CommandGroupConfig { ListIds = [a.Id, b.Id], Commands = ["AT"] };
            var source = new TestWorkspaceConfig { Configuration = new() { CommandGroupLists = [a, b], SelectedCommandGroupListId = b.Id, CommandGroups = [group] } };
            var path = Path.Combine(directory, "workspace.json"); WorkspaceCatalogStore.Save(path, new() { CurrentWorkspaceId = source.Id, Workspaces = [source] });
            var restored = WorkspaceCatalogStore.Load(path).Workspaces.Single();
            Assert.Equal(b.Id, restored.Configuration.SelectedCommandGroupListId);
            var copy = WorkspaceCatalogStore.Clone(restored, "副本"); var config = copy.Configuration;
            Assert.Empty(config.CommandGroupLists.Select(list => list.Id).Intersect(new[] { a.Id, b.Id }));
            Assert.Equal(config.CommandGroupLists.Select(list => list.Id), config.CommandGroups.Single().ListIds);
            Assert.Equal(config.CommandGroupLists[1].Id, config.SelectedCommandGroupListId);
            config.CommandGroups.Single().ListIds.Clear(); config.CommandGroupLists[0].Name = "修改";
            Assert.Equal(2, restored.Configuration.CommandGroups.Single().ListIds.Count); Assert.Equal("AP", a.Name);
        });
    }

    [Fact]
    public void Invalid_membership_or_duplicate_list_names_do_not_overwrite_configuration_or_mutate_runtime()
    {
        WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "workspace.json"); var config = new WorkspaceConfig(); WorkspaceConfigStore.Save(path, config);
            var before = File.ReadAllBytes(path); config.CommandGroups.Add(new() { ListIds = [Guid.NewGuid().ToString("N")] });
            Assert.Throws<InvalidDataException>(() => WorkspaceConfigStore.Save(path, config)); Assert.Equal(before, File.ReadAllBytes(path));
            using var panel = new CommandPanelViewModel([], _ => { }); panel.AddCommandGroupCommand.Execute(null);
            var lists = new CommandGroupListConfig[] { new() { Name = "设备" }, new() { Name = " 设备 " } };
            Assert.Throws<InvalidOperationException>(() => panel.ApplyCommandGroupSettings([], null, lists));
            Assert.Empty(panel.CommandGroupLists); Assert.Single(panel.CommandGroups);
        });
    }

    private static void WithDirectory(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "group-lists-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try { action(directory); } finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Creating_with_search_and_deleting_from_filtered_list_never_selects_a_hidden_other_device_group()
    {
        using var panel = new CommandPanelViewModel([], _ => { }, (_, _) => true);
        panel.AddCommandGroupListCommand.Execute(null); panel.SelectedCommandGroupList!.Name = "AP";
        panel.AddCommandGroupCommand.Execute(null); var a = panel.SelectedCommandGroup!; a.Name = "AP 1";
        panel.AddCommandGroupListCommand.Execute(null); var iot = panel.SelectedCommandGroupList!;
        panel.AddCommandGroupCommand.Execute(null); var b = panel.SelectedCommandGroup!; b.Name = "IOT";
        panel.SelectedCommandGroupList = panel.CommandGroupLists[0];
        panel.GroupListSearchText = "AP"; panel.AddCommandGroupCommand.Execute(null);
        var c = panel.SelectedCommandGroup!; c.Name = "AP 2";
        Assert.Empty(panel.GroupListSearchText); Assert.Contains(c, panel.VisibleCommandGroups);
        panel.DeleteCommandGroupCommand.Execute(null);
        Assert.Same(a, panel.SelectedCommandGroup); Assert.DoesNotContain(b, panel.VisibleCommandGroups);
        panel.SelectedCommandGroupList = iot; Assert.Same(b, panel.SelectedCommandGroup);
    }

    [Fact]
    public void Fresh_workspace_and_zero_categories_keep_all_groups_without_recreating_default_list()
    {
        using var panel = new CommandPanelViewModel([], _ => { }, (_, _) => true);
        Assert.Empty(panel.CommandGroupLists); Assert.Same(panel.AllCommandGroupsList, Assert.Single(panel.CommandGroupListOptions));
        panel.AddCommandGroupCommand.Execute(null); var group = panel.SelectedCommandGroup!; Assert.Empty(group.ListIds);
        panel.DuplicateCommandGroupCommand.Execute(null); Assert.Empty(panel.SelectedCommandGroup!.ListIds);
        panel.ImportCommandGroups([group.ToConfig()]); Assert.Empty(panel.SelectedCommandGroup!.ListIds);
        panel.ApplyCommandGroupSettings(panel.CommandGroups.Select(g => g.ToConfig()).ToArray(), group.Id, [], string.Empty);
        Assert.Equal(3, panel.VisibleCommandGroups.Count); Assert.Empty(panel.CommandGroupLists);
        panel.AddCommandGroupListCommand.Execute(null); var created = panel.SelectedCommandGroupList!;
        Assert.True(panel.DeleteCommandGroupListCommand.CanExecute(null)); panel.DeleteCommandGroupListCommand.Execute(null);
        Assert.Empty(panel.CommandGroupLists); Assert.DoesNotContain(created, panel.CommandGroupListOptions);
        Assert.Equal(3, panel.VisibleCommandGroups.Count);
    }

    [Fact]
    public void Existing_auto_default_category_is_removed_with_exact_backup_and_no_group_or_queue_loss()
    {
        WithDirectory(directory =>
        {
            var auto = CommandGroupListConfig.CreateDefault(); var custom = new CommandGroupListConfig { Name = "IOT" };
            var group = new CommandGroupConfig { Name = "命令", Commands = ["AT"], ListIds = [auto.Id, custom.Id] };
            var workspace = new TestWorkspaceConfig { Configuration = new() { CommandGroupLists = [auto, custom],
                SelectedCommandGroupListId = auto.Id, CommandGroups = [group], CommandGroupQueue = [new() { GroupId = group.Id }] } };
            var path = Path.Combine(directory, "workspace.json");
            WorkspaceCatalogStore.Save(path, new() { CurrentWorkspaceId = workspace.Id, Workspaces = [workspace] });
            var before = File.ReadAllBytes(path); var loaded = WorkspaceCatalogStore.Load(path).Workspaces.Single().Configuration;
            Assert.Equal(custom.Id, loaded.CommandGroupLists.Single().Id); Assert.Equal(new[] { custom.Id }, loaded.CommandGroups.Single().ListIds);
            Assert.Equal(string.Empty, loaded.SelectedCommandGroupListId); Assert.Equal("AT", loaded.CommandGroups.Single().Commands.Single());
            Assert.Equal(group.Id, loaded.CommandGroupQueue.Single().GroupId);
            Assert.Equal(before, File.ReadAllBytes(Assert.Single(Directory.GetFiles(directory, "*.bak"))));
            WorkspaceCatalogStore.Load(path); Assert.Single(Directory.GetFiles(directory, "*.bak"));
        });
    }

    [Fact]
    public void Renamed_legacy_category_and_user_created_default_name_are_preserved_and_empty_lists_round_trip()
    {
        WithDirectory(directory =>
        {
            var renamed = CommandGroupListConfig.CreateDefault(); renamed.Name = "AP";
            var custom = new CommandGroupListConfig { Name = "默认列表" };
            var group = new CommandGroupConfig { ListIds = [renamed.Id, custom.Id] };
            var path = Path.Combine(directory, "workspace.json");
            WorkspaceConfigStore.Save(path, new() { CommandGroupLists = [renamed, custom], CommandGroups = [group] });
            var loaded = WorkspaceConfigStore.Load(path);
            Assert.Equal(new[] { "AP", "默认列表" }, loaded.CommandGroupLists.Select(list => list.Name)); Assert.Empty(Directory.GetFiles(directory, "*.bak"));
            WorkspaceConfigStore.Save(path, new() { CommandGroups = [new() { Name = "未分类组" }] });
            loaded = WorkspaceConfigStore.Load(path); Assert.Empty(loaded.CommandGroupLists); Assert.Empty(loaded.CommandGroups.Single().ListIds);
        });
    }
}
