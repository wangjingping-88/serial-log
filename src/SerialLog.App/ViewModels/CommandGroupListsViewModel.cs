using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using SerialLog.App.Infrastructure;
using SerialLog.Core.Configuration;

namespace SerialLog.App.ViewModels;

public sealed class CommandGroupListViewModel(CommandGroupListConfig config) : ObservableObject
{
    private string _name = config.Name;
    private int _count;
    public string Id { get; } = config.Id;
    public string Name { get => _name; set { if (SetProperty(ref _name, value)) OnPropertyChanged(nameof(Header)); } }
    public int Count { get => _count; internal set { if (SetProperty(ref _count, value)) OnPropertyChanged(nameof(Header)); } }
    public string Header => $"{Name}（{Count}）";
    public CommandGroupListConfig ToConfig() => new() { Id = Id, Name = Name.Trim() };
}

public sealed class CommandGroupMembershipViewModel(CommandGroupListViewModel list, CommandGroupEditorViewModel group) : ObservableObject
{
    public CommandGroupListViewModel List { get; } = list;
    internal void Refresh() => OnPropertyChanged(nameof(IsSelected));
    public bool IsSelected
    {
        get => group.ListIds.Contains(List.Id);
        set
        {
            if (value == IsSelected) return;
            if (value) group.ListIds.Add(List.Id); else group.ListIds.Remove(List.Id);
            OnPropertyChanged();
        }
    }
}

public sealed partial class CommandPanelViewModel
{
    private readonly ObservableCollection<CommandGroupEditorViewModel> _visibleCommandGroups = [];
    private readonly HashSet<CommandGroupListViewModel> _observedGroupLists = [];
    private CommandGroupListViewModel? _selectedCommandGroupList;
    private string _groupListSearchText = string.Empty;
    private bool _refreshingGroupLists;
    public ObservableCollection<CommandGroupListViewModel> CommandGroupLists { get; } = [];
    public CommandGroupListViewModel AllCommandGroupsList { get; } = new(new() { Id = string.Empty, Name = "全部" });
    public ObservableCollection<CommandGroupListViewModel> CommandGroupListOptions { get; } = [];
    public ObservableCollection<CommandGroupMembershipViewModel> CommandGroupMemberships { get; } = [];
    public ObservableCollection<CommandGroupEditorViewModel> VisibleCommandGroups => _visibleCommandGroups;
    private string[] NewGroupListIds => SelectedCommandGroupList is { Id.Length: > 0 } list ? [list.Id] : [];
    public bool CanEditCommandGroupList => SelectedCommandGroupList is { Id.Length: > 0 };
    public string CurrentListRemoveLabel => CanEditCommandGroupList ? "从当前列表移除" : "删除";
    public string CurrentListHeader => $"{SelectedCommandGroupList?.Name ?? "全部"}（{VisibleCommandGroups.Count}）";
    public CommandGroupListViewModel? SelectedCommandGroupList
    {
        get => _selectedCommandGroupList;
        set
        {
            if (value is null || !CommandGroupListOptions.Contains(value) || !SetProperty(ref _selectedCommandGroupList, value)) return;
            GroupListSearchText = string.Empty;
            RefreshCommandGroupLists();
        }
    }
    public string GroupListSearchText
    {
        get => _groupListSearchText;
        set { if (SetProperty(ref _groupListSearchText, value)) RefreshCommandGroupLists(); }
    }
    public RelayCommand AddCommandGroupListCommand { get; private set; } = null!;
    public RelayCommand DeleteCommandGroupListCommand { get; private set; } = null!;
    public RelayCommand MoveCommandGroupListUpCommand { get; private set; } = null!;
    public RelayCommand MoveCommandGroupListDownCommand { get; private set; } = null!;
    public RelayCommand StopCurrentListGroupsCommand { get; private set; } = null!;
    public RelayCommand RemoveCurrentListMemberCommand { get; private set; } = null!;
    public RelayCommand EditCommandGroupListMembersCommand { get; private set; } = null!;

    private void InitializeCommandGroupLists()
    {
        AddCommandGroupListCommand = new RelayCommand(() =>
        {
            var name = "新列表"; var number = 2;
            while (CommandGroupLists.Any(list => string.Equals(list.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))) name = $"新列表 {number++}";
            var list = new CommandGroupListViewModel(new() { Name = name });
            CommandGroupLists.Add(list); SelectedCommandGroupList = list;
        });
        DeleteCommandGroupListCommand = new RelayCommand(DeleteCommandGroupList, () => SelectedCommandGroupList is { Id.Length: > 0 } list && CommandGroupLists.Contains(list));
        MoveCommandGroupListUpCommand = new RelayCommand(() => MoveCommandGroupList(-1), () => SelectedCommandGroupList is not null && CommandGroupLists.IndexOf(SelectedCommandGroupList) > 0);
        MoveCommandGroupListDownCommand = new RelayCommand(() => MoveCommandGroupList(1), () => SelectedCommandGroupList is { Id.Length: > 0 } && CommandGroupLists.IndexOf(SelectedCommandGroupList) < CommandGroupLists.Count - 1);
        StopCurrentListGroupsCommand = new RelayCommand(() =>
        {
            foreach (var group in VisibleCommandGroups.Where(g => g.IsRunning).ToArray())
                GroupScheduler.StopGroupCommand.Execute(group);
        }, () => VisibleCommandGroups.Any(g => g.IsRunning));
        RemoveCurrentListMemberCommand = new RelayCommand(RemoveCurrentListMember, () => SelectedCommandGroup is not null &&
            (SelectedCommandGroupList?.Id.Length > 0 || CanDeleteCommandGroup(SelectedCommandGroup.Id)));
        EditCommandGroupListMembersCommand = new RelayCommand(() =>
        {
            if (SelectedCommandGroupList is not { Id.Length: > 0 } list) return;
            var window = new Views.CommandGroupListMembersWindow(list, CommandGroups)
            { Owner = System.Windows.Application.Current?.Windows.OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive) };
            if (window.ShowDialog() != true) return;
            foreach (var group in CommandGroups)
            {
                var include = window.SelectedGroupIds.Contains(group.Id);
                if (include && !group.ListIds.Contains(list.Id)) group.ListIds.Add(list.Id);
                else if (!include) group.ListIds.Remove(list.Id);
            }
        }, () => CanEditCommandGroupList);
        GroupScheduler.GroupScope = () => VisibleCommandGroups;
        GroupScheduler.ConfigurationChanged += GroupLists_ConfigurationChanged;
        GroupScheduler.PropertyChanged += GroupLists_SchedulerChanged;
        CommandGroupLists.CollectionChanged += GroupLists_Changed;
        LoadCommandGroupLists([], string.Empty);
    }

    public void LoadCommandGroupLists(IEnumerable<CommandGroupListConfig> lists, string? selectedId)
    {
        var snapshot = lists.Select(list => new CommandGroupListViewModel(list)).ToArray();
        _refreshingGroupLists = true;
        try
        {
            CommandGroupLists.Clear();
            foreach (var list in snapshot) CommandGroupLists.Add(list);
            _selectedCommandGroupList = snapshot.FirstOrDefault(list => list.Id == selectedId) ?? AllCommandGroupsList;
        }
        finally { _refreshingGroupLists = false; }
        OnPropertyChanged(nameof(SelectedCommandGroupList)); RefreshCommandGroupLists();
    }

    private void DeleteCommandGroupList()
    {
        if (SelectedCommandGroupList is not { Id.Length: > 0 } list || !CommandGroupLists.Contains(list)) return;
        var members = CommandGroups.Where(group => group.ListIds.Contains(list.Id)).ToArray();
        if (!ConfirmDelete("删除命令组列表", $"确定删除列表“{list.Name}”吗？只移除 {members.Length} 个成员的分类关系；原命令组仍可在“全部”找到，其他列表、运行任务及队列不受影响。")) return;
        foreach (var group in members) group.ListIds.Remove(list.Id);
        CommandGroupLists.Remove(list); SelectedCommandGroupList = AllCommandGroupsList;
    }

    private void MoveCommandGroupList(int offset)
    {
        if (SelectedCommandGroupList is not { Id.Length: > 0 } list) return;
        var index = CommandGroupLists.IndexOf(list); var next = index + offset;
        if (next < 0 || next >= CommandGroupLists.Count) return;
        CommandGroupLists.Move(index, next);
        SelectedCommandGroupList = list;
    }

    private void GroupLists_Changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var list in _observedGroupLists) list.PropertyChanged -= GroupList_PropertyChanged;
        _observedGroupLists.Clear();
        foreach (var list in CommandGroupLists) { _observedGroupLists.Add(list); list.PropertyChanged += GroupList_PropertyChanged; }
        if (!_refreshingGroupLists) RefreshCommandGroupLists();
    }
    private void GroupList_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CommandGroupListViewModel.Name)) { OnPropertyChanged(nameof(CommandGroupLists)); OnPropertyChanged(nameof(CurrentListHeader)); }
    }
    private void GroupLists_ConfigurationChanged(object? sender, EventArgs e) => RefreshCommandGroupLists();
    private void GroupLists_SchedulerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CommandGroupSchedulerViewModel.HasRunningTasks)) StopCurrentListGroupsCommand.RaiseCanExecuteChanged();
        if (e.PropertyName == nameof(CommandGroupSchedulerViewModel.SelectedGroup)) RefreshMemberships();
    }
    private void RefreshCommandGroupLists()
    {
        if (_refreshingGroupLists) return;
        _refreshingGroupLists = true;
        try
        {
            SyncCollection(CommandGroupListOptions, new[] { AllCommandGroupsList }.Concat(CommandGroupLists).ToArray());
            AllCommandGroupsList.Count = CommandGroups.Count;
            foreach (var list in CommandGroupLists) list.Count = CommandGroups.Count(group => group.ListIds.Contains(list.Id));
            var visible = CommandGroups.Where(group => (SelectedCommandGroupList?.Id.Length == 0 || group.ListIds.Contains(SelectedCommandGroupList?.Id ?? string.Empty)) &&
                (string.IsNullOrWhiteSpace(GroupListSearchText) || group.Name.Contains(GroupListSearchText.Trim(), StringComparison.OrdinalIgnoreCase))).ToArray();
            foreach (var removed in _visibleCommandGroups.Except(visible).ToArray()) _visibleCommandGroups.Remove(removed);
            for (var index = 0; index < visible.Length; index++)
            {
                var previous = _visibleCommandGroups.IndexOf(visible[index]);
                if (previous < 0) _visibleCommandGroups.Insert(index, visible[index]);
                else if (previous != index) _visibleCommandGroups.Move(previous, index);
            }
            if (SelectedCommandGroup is null || !_visibleCommandGroups.Contains(SelectedCommandGroup))
                SelectedCommandGroup = _visibleCommandGroups.FirstOrDefault();
            OnPropertyChanged(nameof(VisibleCommandGroups));
            OnPropertyChanged(nameof(CommandGroupLists));
            OnPropertyChanged(nameof(CanEditCommandGroupList)); OnPropertyChanged(nameof(CurrentListRemoveLabel)); OnPropertyChanged(nameof(CurrentListHeader));
            DeleteCommandGroupListCommand?.RaiseCanExecuteChanged();
            MoveCommandGroupListUpCommand?.RaiseCanExecuteChanged(); MoveCommandGroupListDownCommand?.RaiseCanExecuteChanged();
            StopCurrentListGroupsCommand?.RaiseCanExecuteChanged(); ClearCommandGroupsCommand?.RaiseCanExecuteChanged();
            RemoveCurrentListMemberCommand?.RaiseCanExecuteChanged();
            EditCommandGroupListMembersCommand?.RaiseCanExecuteChanged();
            RefreshMemberships();
            GroupScheduler.RefreshScope();
        }
        finally { _refreshingGroupLists = false; }
    }

    private CommandGroupEditorViewModel? _membershipGroup;
    private void RefreshMemberships()
    {
        if (ReferenceEquals(_membershipGroup, SelectedCommandGroup) &&
            CommandGroupMemberships.Select(member => member.List).SequenceEqual(CommandGroupLists))
        { foreach (var member in CommandGroupMemberships) member.Refresh(); return; }
        _membershipGroup = SelectedCommandGroup;
        CommandGroupMemberships.Clear();
        if (_membershipGroup is not null)
            foreach (var list in CommandGroupLists) CommandGroupMemberships.Add(new(list, _membershipGroup));
    }

    private static void SyncCollection<T>(ObservableCollection<T> collection, IReadOnlyList<T> items)
    {
        foreach (var removed in collection.Except(items).ToArray()) collection.Remove(removed);
        for (var index = 0; index < items.Count; index++)
        {
            var previous = collection.IndexOf(items[index]);
            if (previous < 0) collection.Insert(index, items[index]); else if (previous != index) collection.Move(previous, index);
        }
    }

    private void RemoveCurrentListMember()
    {
        if (SelectedCommandGroup is not { } group) return;
        if (SelectedCommandGroupList is not { Id.Length: > 0 } list) { DeleteCommandGroupCommand.Execute(null); return; }
        group.ListIds.Remove(list.Id);
        SetStatus($"已从“{list.Name}”移除“{group.Name}”；原命令组、其他分类和队列不变。");
    }

    private void ClearCurrentListMembers()
    {
        if (SelectedCommandGroupList is not { Id.Length: > 0 } list) return;
        var members = VisibleCommandGroups.ToArray();
        if (!ConfirmDelete("清空列表成员", $"确定从“{list.Name}”移除 {members.Length} 个成员吗？仅移除分类关系，不删除命令组，不停止任务，队列不变。")) return;
        foreach (var group in members) group.ListIds.Remove(list.Id);
        SetStatus($"已清空“{list.Name}”的成员；原命令组、其他列表和队列不变。");
    }

    private void DisposeCommandGroupLists()
    {
        CommandGroupLists.CollectionChanged -= GroupLists_Changed;
        GroupScheduler.ConfigurationChanged -= GroupLists_ConfigurationChanged;
        GroupScheduler.PropertyChanged -= GroupLists_SchedulerChanged;
        foreach (var list in _observedGroupLists) list.PropertyChanged -= GroupList_PropertyChanged;
    }
}
