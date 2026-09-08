using System.Collections.ObjectModel;
using SerialLog.App.Infrastructure;
using SerialLog.Core.Configuration;
using SerialLog.Core.Serial;
using System.Windows;

namespace SerialLog.App.ViewModels;

/// <summary>只切换活动引用；每个工作区的采集、循环及协作生命周期互不依赖。</summary>
public sealed class TestWorkspaceManager : ObservableObject, IDisposable
{
    private readonly string _path;
    private TestWorkspaceRuntime _active = null!;
    private bool _loading = true;
    private readonly bool _startTimers;
    private Task? _shutdownTask;
    private readonly SerialPortOwnership _ports = new();
    private readonly Dictionary<SerialWindowViewModel, WindowPortOwner> _owners = [];
    private readonly Dictionary<SerialWindowViewModel, (ISerialPortOwner Previous, bool Approved)> _batchChoices = [];
    public ObservableCollection<TestWorkspaceRuntime> Workspaces { get; } = [];
    public bool CanDeleteWorkspace => Workspaces.Count > 1;
    public TestWorkspaceRuntime Active
    {
        get => _active;
        set
        {
            if (!Workspaces.Contains(value)) throw new ArgumentException("工作区不属于当前应用");
            if (SetProperty(ref _active, value) && !_loading)
            {
                try { Save(); }
                catch (Exception exception) { value.ViewModel.StatusText = $"保存工作区失败，当前运行状态已保留：{exception.Message}"; }
            }
        }
    }

    public TestWorkspaceManager(string path, bool startTimers = true)
    {
        _path = path;
        _startTimers = startTimers;
        Workspaces.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CanDeleteWorkspace));
        var catalog = WorkspaceCatalogStore.Load(path);
        // 电脑身份在同一安装中一致，工作区身份独立。
        var pcId = catalog.Workspaces.Select(w => w.Configuration.LocalPcId).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id))
            ?? Guid.NewGuid().ToString("N");
        foreach (var entry in catalog.Workspaces)
        {
            entry.Configuration.LocalPcId = pcId;
            AddRuntime(entry, startTimers);
        }
        Active = Workspaces.Single(w => w.Id == catalog.CurrentWorkspaceId);
        _loading = false;
    }

    private TestWorkspaceRuntime AddRuntime(TestWorkspaceConfig entry, bool startTimers = true)
    {
        var runtime = new TestWorkspaceRuntime(entry.Id, entry.Name,
            new MainViewModel(_path, startTimers, configuration: entry.Configuration,
                workspaceId: entry.Id, workspaceName: entry.Name, saveCatalog: Save));
        Workspaces.Add(runtime);
        runtime.ViewModel.ConfigurePortOwnership = window =>
        {
            var owner = new WindowPortOwner(runtime, window);
            _owners[window] = owner;
            window.StopTargetLoopsAsync = runtime.ViewModel.CommandPanel.StopLoopsAsync;
            window.AcquirePortAsync = (port, automatic, token) =>
            {
                // 批量确认只授权本次请求，不留给后续手动连接或自动重连使用。
                _batchChoices.Remove(window, out var decision);
                return _ports.AcquireAsync(port, owner, automatic, previous =>
                {
                    if (!automatic && ReferenceEquals(decision.Previous, previous)) return decision.Approved;
                    return MessageBox.Show($"{port} 当前属于 {previous.DisplayName}。\n\n转移到 {owner.DisplayName}？\n选择“否”保留原连接。",
                        "串口占用冲突", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
                }, token);
            };
        };
        foreach (var window in runtime.ViewModel.SerialWindows.Where(w => !w.IsRemote)) runtime.ViewModel.ConfigurePortOwnership(window);
        runtime.ViewModel.PrepareBatchConnections = windows =>
        {
            _batchChoices.Clear();
            var candidates = windows.Where(w => !string.IsNullOrWhiteSpace(w.PortName) && !w.IsConnected).ToArray();
            var duplicates = candidates.GroupBy(w => w.PortName!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var allowed = windows.Select(w => w.Id).ToHashSet();
            var conflicts = candidates.Select(w => (Window: w, Previous: _ports.FindOwner(w.PortName!)))
                .Where(c => duplicates.Contains(c.Window.PortName!.Trim()) ||
                    (c.Previous is not null && c.Previous.WantsConnection && !ReferenceEquals(c.Previous, _owners.GetValueOrDefault(c.Window))))
                .ToArray();
            if (conflicts.Length == 0) return allowed;
            var dialog = new Views.PortConflictsWindow(conflicts.Select(c => (c.Window.Id, c.Window.PortName!.Trim(),
                $"{c.Window.PortName}：{c.Previous?.DisplayName ?? "本批次重复端口"} → {runtime.Name} / {c.Window.Title}")).ToArray()) { Owner = Application.Current?.MainWindow };
            var confirmed = dialog.ShowDialog() == true;
            foreach (var conflict in conflicts)
            {
                if (!confirmed || !dialog.Approved.Contains(conflict.Window.Id)) allowed.Remove(conflict.Window.Id);
                else if (conflict.Previous is not null) _batchChoices[conflict.Window] = (conflict.Previous, true);
            }
            return allowed;
        };
        return runtime;
    }

    public void Create(string name, bool copy)
    {
        var entry = copy ? WorkspaceCatalogStore.Clone(Active.Export(), name) : new TestWorkspaceConfig { Name = name };
        entry.Configuration.LocalPcId = Active.ViewModel.LocalPcId;
        Active = AddRuntime(entry, _startTimers);
    }

    public void Rename(TestWorkspaceRuntime workspace, string name)
    {
        workspace.Name = name;
        Save();
    }

    public async Task DeleteAsync(TestWorkspaceRuntime workspace)
    {
        if (!CanDeleteWorkspace) throw new InvalidOperationException("至少保留一个工作区");
        await workspace.ViewModel.StopWorkspaceAsync();
        if (Active == workspace) Active = Workspaces.First(w => w != workspace);
        Workspaces.Remove(workspace);
        workspace.Dispose();
        Save();
    }

    public void Save()
    {
        if (_loading) return;
        WorkspaceCatalogStore.Save(_path, new WorkspaceCatalog
        {
            CurrentWorkspaceId = Active.Id,
            Workspaces = Workspaces.Select(w => w.Export()).ToList()
        });
    }

    public string RunningTestsDescription => string.Join("\n", Workspaces.Where(w => w.ViewModel.HasRunningTests).Select(w => w.DisplayName));

    // 仅供整个应用退出使用；普通工作区菜单不能调用跨工作区关闭。
    public Task ShutdownAsync() => _shutdownTask ??= ShutdownCoreAsync();

    private async Task ShutdownCoreAsync()
    {
        foreach (var workspace in Workspaces.ToArray())
        {
            try { await workspace.ViewModel.StopWorkspaceAsync(); }
            catch (Exception exception) { Diagnostics.CrashLogWriter.Write($"退出时停止工作区：{workspace.Name}", exception); }
            try { workspace.Dispose(); }
            catch (Exception exception) { Diagnostics.CrashLogWriter.Write($"退出时释放工作区：{workspace.Name}", exception); }
        }
    }

    public void Dispose() { foreach (var workspace in Workspaces) workspace.Dispose(); }

    private sealed class WindowPortOwner(TestWorkspaceRuntime workspace, SerialWindowViewModel window) : ISerialPortOwner
    {
        public string DisplayName => $"{workspace.Name} / {window.Title}";
        public bool WantsConnection => window.WantsConnection;
        public Task ReleaseForTransferAsync(string destination) => window.ReleaseForTransferAsync(destination);
    }
}

public sealed class TestWorkspaceRuntime : ObservableObject, IDisposable
{
    private string _name;
    public string Id { get; }
    public MainViewModel ViewModel { get; }
    public string Name
    {
        get => _name;
        set { if (SetProperty(ref _name, value)) { ViewModel.WorkspaceName = value; OnPropertyChanged(nameof(DisplayName)); } }
    }
    public string DisplayName => $"{Name} · 连接 {ViewModel.SerialWindows.Count(w => w.IsConnected && !w.IsRemote)}" +
        (ViewModel.CommandPanel.IsSingleCommandLoopRunning || ViewModel.CommandPanel.IsCommandGroupLoopRunning ? " · 循环中" : "");

    public TestWorkspaceRuntime(string id, string name, MainViewModel viewModel)
    {
        Id = id; _name = name; ViewModel = viewModel;
        ViewModel.PropertyChanged += Changed;
        ViewModel.CommandPanel.PropertyChanged += Changed;
    }
    private void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => OnPropertyChanged(nameof(DisplayName));
    public TestWorkspaceConfig Export() => new() { Id = Id, Name = Name, Configuration = ViewModel.ExportConfiguration() };
    public void Dispose()
    {
        ViewModel.PropertyChanged -= Changed;
        ViewModel.CommandPanel.PropertyChanged -= Changed;
        ViewModel.Dispose();
    }
}
