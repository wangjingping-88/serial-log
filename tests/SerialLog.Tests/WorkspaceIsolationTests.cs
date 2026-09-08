using System.Reflection;
using System.Text.Json;
using SerialLog.App.ViewModels;
using SerialLog.Core.Collaboration;
using SerialLog.Core.Configuration;
using SerialLog.Core.Logging;

namespace SerialLog.Tests;

public sealed class WorkspaceIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "workspace-isolation-" + Guid.NewGuid().ToString("N"));
    private readonly TestWorkspaceManager _manager;
    public WorkspaceIsolationTests()
    {
        Directory.CreateDirectory(_root);
        _manager = new TestWorkspaceManager(Path.Combine(_root, "workspace.json"), false);
        _manager.Active.ViewModel.LogRootDirectory = _root;
    }

    [Fact]
    public async Task Delete_availability_tracks_create_copy_and_delete_and_protects_last_workspace()
    {
        var states = new List<bool>();
        _manager.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TestWorkspaceManager.CanDeleteWorkspace)) states.Add(_manager.CanDeleteWorkspace);
        };
        var original = _manager.Active;
        Assert.False(_manager.CanDeleteWorkspace);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _manager.DeleteAsync(original));
        Assert.Same(original, Assert.Single(_manager.Workspaces));
        _manager.Create("新建", copy: false);
        Assert.True(_manager.CanDeleteWorkspace);
        await _manager.DeleteAsync(_manager.Active);
        Assert.False(_manager.CanDeleteWorkspace);
        _manager.Create("复制", copy: true);
        Assert.True(_manager.CanDeleteWorkspace);
        await _manager.DeleteAsync(_manager.Active);
        Assert.False(_manager.CanDeleteWorkspace);
        Assert.Equal(new[] { true, false, true, false }, states);
        Assert.Same(original, _manager.Active);
    }

    [Fact]
    public async Task Menu_operations_only_mutate_their_own_runtime()
    {
        var a = _manager.Active;
        a.ViewModel.AddWindowCommand.Execute(null);
        _manager.Create("B", copy: true);
        var b = _manager.Active;
        var bWindow = Assert.Single(b.ViewModel.SerialWindows);
        b.ViewModel.CommandText = "B only";
        b.ViewModel.NewLogSessionCommand.Execute(null);
        var batch = b.ViewModel.CurrentLogSessionDirectory;
        var configBefore = JsonSerializer.Serialize(b.Export());
        _manager.Active = a;
        a.ViewModel.ThemeColor = "#123456";
        a.ViewModel.SetShortcutBindings([new() { ActionId = "test-action", Gesture = "Ctrl+Alt+Q" }]);
        a.ViewModel.MaxLogFileSizeMegabytes = 200;
        a.ViewModel.ReceiveSilenceReconnectSeconds = 600;
        a.ViewModel.CommandText = "A only";
        a.ViewModel.CommandHistory.Add("A history");
        a.ViewModel.AddPageCommand.Execute(null);
        a.ViewModel.AddWindowCommand.Execute(null);
        a.ViewModel.CommandPanelDock = CommandPanelDock.Right;
        a.ViewModel.IsCommandPanelFloating = true;
        a.ViewModel.IsCommandPanelHidden = true;
        a.ViewModel.SetAllLocalSharing(true);
        a.ViewModel.NewLogSessionCommand.Execute(null);
        a.ViewModel.DisconnectAllCommand.Execute(null);
        await a.ViewModel.StopWorkspaceAsync();
        Assert.Equal(configBefore, JsonSerializer.Serialize(b.Export()));
        Assert.Same(bWindow, Assert.Single(b.ViewModel.SerialWindows));
        Assert.Equal(batch, b.ViewModel.CurrentLogSessionDirectory);
        Assert.Contains(b.Id, batch);
        Assert.Contains(a.Id, a.ViewModel.CurrentLogSessionDirectory);
        _manager.Active = b;
        Assert.Equal("B only", _manager.Active.ViewModel.CommandText);
    }

    [Fact]
    public async Task Switching_does_not_stop_background_command_loop()
    {
        var a = _manager.Active;
        var sent = new System.Collections.Concurrent.ConcurrentBag<string>();
        var source = new CollaborationClientSnapshot("remote", "remote", "#123456", []);
        var remote = SerialWindowViewModel.CreateRemote(source,
            new CollaborationWindowSnapshot("node", "node", "COM7", 115200, true, 0, true),
            (_, payload, _) => { sent.Add(payload); return Task.CompletedTask; });
        remote.AutoSaveEnabled = false;
        remote.IsSelectedForSend = true;
        a.ViewModel.SerialWindows.Add(remote);
        a.ViewModel.CommandText = "AT_A";
        a.ViewModel.SingleCommandLoopIntervalMilliseconds = 20;
        a.ViewModel.ToggleSingleCommandLoopCommand.Execute(null);
        _manager.Create("B", copy: false);
        _manager.Active.ViewModel.CommandText = "AT_B";
        await Task.Delay(120);
        Assert.True(a.ViewModel.CommandPanel.IsSingleCommandLoopRunning);
        Assert.True(sent.Count >= 2);
        Assert.All(sent, payload => Assert.StartsWith("AT_A", payload));
        await a.ViewModel.CommandPanel.StopLoopsAsync(remote.Id);
        var count = sent.Count;
        await Task.Delay(60);
        Assert.Equal(count, sent.Count);
        Assert.False(a.ViewModel.CommandPanel.IsSingleCommandLoopRunning);
    }

    [Fact]
    public async Task Subscriptions_are_opt_in_and_unshare_retains_history()
    {
        var a = _manager.Active.ViewModel;
        var source = new CollaborationClientSnapshot("other", "电脑", "#123456",
            [new("w1", "窗口", "COM7", 460800, true, 0, true)], "workspace-other", "测试");
        Upsert(a, source);
        Assert.Empty(a.SerialWindows);
        await a.SetSubscriptionsAsync([new() { Source = source, Window = source.Windows[0], AutoSaveEnabled = false }]);
        var remote = Assert.Single(a.SerialWindows);
        remote.AppendRemoteLine(new ReceivedLogLine(DateTimeOffset.Now, "历史"));
        _manager.Create("B", false);
        Upsert(_manager.Active.ViewModel, source);
        Assert.Empty(_manager.Active.ViewModel.SerialWindows);
        Upsert(a, source with { Windows = [] });
        Assert.Same(remote, Assert.Single(a.SerialWindows));
        Assert.Contains("停止共享", remote.StatusText);
        Assert.False(remote.IsConnected);
        Upsert(a, source);
        Assert.Same(remote, Assert.Single(a.SerialWindows));
        Assert.True(remote.IsConnected);
        Assert.False(remote.AutoSaveEnabled);
        await a.SetSubscriptionsAsync([]);
        Assert.Empty(a.SerialWindows);
    }

    [Fact]
    public async Task Delete_retains_logs_and_restart_restores_selection_without_running()
    {
        var a = _manager.Active;
        a.ViewModel.NewLogSessionCommand.Execute(null);
        var logs = a.ViewModel.CurrentLogSessionDirectory!;
        _manager.Create("B", false);
        var b = _manager.Active;
        b.ViewModel.ThemeColor = "#123456";
        await _manager.DeleteAsync(a);
        Assert.True(Directory.Exists(logs));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _manager.DeleteAsync(b));
        _manager.Save();
        using var restored = new TestWorkspaceManager(Path.Combine(_root, "workspace.json"), false);
        Assert.Equal(b.Id, restored.Active.Id);
        Assert.Equal("#123456", restored.Active.ViewModel.ThemeColor);
        Assert.False(restored.Active.ViewModel.HasRunningTests);
        Assert.Empty(restored.Active.ViewModel.SerialWindows);
    }

    private static void Upsert(MainViewModel vm, CollaborationClientSnapshot snapshot) =>
        typeof(MainViewModel).GetMethod("UpsertRemoteClientSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [snapshot]);

    [Fact]
    public async Task Application_shutdown_stops_all_collaboration_and_waits_for_pending_loop_send()
    {
        await using var host = new CollaborationHostService();
        await host.StartAsync(System.Net.IPAddress.Loopback, 0);
        var first = _manager.Active;
        _manager.Create("后台测试", false);
        foreach (var workspace in _manager.Workspaces)
        {
            var vm = workspace.ViewModel;
            vm.WorkspaceMode = WorkspaceMode.Client;
            vm.HostAddress = "127.0.0.1";
            vm.HostPort = host.Port;
            var start = (Task)typeof(MainViewModel).GetMethod("StartCollaborationAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, null)!;
            await start.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(vm.IsCollaborationRunning);
        }
        var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new CollaborationClientSnapshot("source", "发送测试", "#123456", []);
        var target = SerialWindowViewModel.CreateRemote(source,
            new("node", "节点", "COM7", 115200, true, 0, true),
            async (_, _, token) =>
            {
                sending.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { released.TrySetResult(); }
            });
        target.AutoSaveEnabled = false;
        target.IsSelectedForSend = true;
        first.ViewModel.SerialWindows.Add(target);
        first.ViewModel.CommandText = "AT";
        first.ViewModel.ToggleSingleCommandLoopCommand.Execute(null);
        await sending.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var shutdown = _manager.ShutdownAsync();
        Assert.Same(shutdown, _manager.ShutdownAsync());
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(released.Task.IsCompleted);
        Assert.All(_manager.Workspaces, w => Assert.False(w.ViewModel.HasRunningTests));
        _manager.Dispose(); // 重复释放不会再次访问已关闭的协作资源。
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Subscribed_window_uses_visible_space_or_next_page_when_local_window_is_expanded(bool fullPage, bool savedPosition)
    {
        var vm = _manager.Active.ViewModel;
        vm.IsCommandPanelHidden = true;
        for (var index = 0; index < 3; index++) vm.AddWindowCommand.Execute(null);
        var first = vm.SerialWindows[0];
        vm.Layout.ToggleWindowExpansionCommand.Execute(vm.Layout.CurrentPageWindows.Single(s => s.Window == first));
        Assert.False(vm.Layout.IsPagePositionFree(0, 3));
        if (fullPage)
        {
            vm.AddWindowCommand.Execute(null);
            vm.AddWindowCommand.Execute(null);
            Assert.Equal(5, vm.SerialWindows.Count);
            Assert.False(vm.Layout.CurrentPageHasFreeSlot);
        }
        var source = new CollaborationClientSnapshot("host-other", "主机", "#123456",
            [new("remote", "主机窗口", "COM7", 115200, true, 0, true)], "test", "测试");
        Upsert(vm, source);
        await vm.SetSubscriptionsAsync([new()
        {
            Source = source, Window = source.Windows[0], AutoSaveEnabled = false,
            PageIndex = savedPosition ? 0 : -1, PagePosition = savedPosition ? 3 : -1
        }]);
        var remote = Assert.Single(vm.SerialWindows.Where(w => w.IsRemote));
        Assert.Equal(fullPage ? 1 : 0, remote.PageIndex);
        Assert.Equal(fullPage ? 0 : 4, remote.PagePosition);
        Assert.Equal(0, vm.CurrentPageIndex);
        Assert.True(vm.Layout.CurrentPageWindows.Single(s => s.Window == first).IsExpanded);
        vm.CurrentPageIndex = remote.PageIndex;
        Assert.Contains(vm.Layout.CurrentPageWindows, s => s.Window == remote);
        Assert.Equal(remote.PagePosition, vm.ExportConfiguration().Subscriptions.Single().PagePosition);
    }

    [Fact]
    public void Local_window_also_moves_to_next_page_when_expansion_occupies_remaining_space()
    {
        var vm = _manager.Active.ViewModel;
        vm.IsCommandPanelHidden = true;
        for (var index = 0; index < 3; index++) vm.AddWindowCommand.Execute(null);
        vm.Layout.ToggleWindowExpansionCommand.Execute(vm.Layout.CurrentPageWindows.First(s => s.Window is not null));
        for (var index = 0; index < 3; index++) vm.AddWindowCommand.Execute(null);
        Assert.Equal(1, vm.SerialWindows.Last().PageIndex);
        Assert.Equal(1, vm.CurrentPageIndex);
    }

    public void Dispose() { _manager.Dispose(); Directory.Delete(_root, recursive: true); }
}
