using System.Collections.ObjectModel;
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using SerialLog.App.ViewModels;
using SerialLog.Core.Firmware;
using SerialLog.Core.Serial;

namespace SerialLog.Tests;

public sealed class YmodemParallelTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ymodem-parallel-" + Guid.NewGuid().ToString("N"));
    private readonly string _file;
    public YmodemParallelTests()
    { Directory.CreateDirectory(_directory); _file = Path.Combine(_directory, "fw.bin"); File.WriteAllBytes(_file, Enumerable.Range(0, 2100).Select(i => (byte)i).ToArray()); }
    private static SerialWindowViewModel Connected(string id)
    {
        var window = new SerialWindowViewModel(id, id, refreshPortsOnCreate: false) { PortName = "COM" + id, IsSelectedForSend = true };
        var session = (SerialPortSession)typeof(SerialWindowViewModel).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        typeof(SerialPortSession).GetField("_isConnected", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, 1);
        return window;
    }
    [Fact]
    public async Task Parallel_targets_receive_identical_file_and_bad_target_does_not_stop_others()
    {
        using var a = Connected("1"); using var b = Connected("2"); using var broken = Connected("3");
        using var unselected = Connected("4"); unselected.IsSelectedForSend = false;
        using var disconnected = new SerialWindowViewModel("5", "离线窗口", refreshPortsOnCreate: false) { IsSelectedForSend = true };
        var windows = new ObservableCollection<SerialWindowViewModel> { a, b, broken, unselected, disconnected };
        using var commands = new CommandPanelViewModel(windows, _ => { }); using var vm = new YmodemViewModel(windows, commands, _ => { }) { FilePath = _file };
        var ra = new YmodemSenderTests.Receiver(); var rb = new YmodemSenderTests.Receiver();
        vm.TransportFactory = (target, _) => target == broken ? throw new IOException("坏端口") : Task.FromResult<IBinarySerialTransport>(target == a ? ra : rb);
        await vm.StartAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(4, vm.TargetProgress.Count); Assert.Equal(ra.Data, rb.Data);
        Assert.Equal(File.ReadAllBytes(_file), ra.Data.Take(2100));
        Assert.Equal(100, vm.TargetProgress[0].Percentage); Assert.Equal(100, vm.TargetProgress[1].Percentage);
        Assert.Contains("坏端口", vm.TargetProgress[2].Status); Assert.Contains("未连接", vm.TargetProgress[3].Status);
        Assert.All(windows, w => Assert.False(w.IsFirmwareBusy)); Assert.False(vm.IsRunning);
    }
    [Fact]
    public async Task Cancel_one_target_leaves_other_running_and_later_checkbox_changes_do_not_retarget_batch()
    {
        using var a = Connected("1"); using var b = Connected("2"); using var extra = Connected("3"); extra.IsSelectedForSend = false;
        var windows = new ObservableCollection<SerialWindowViewModel> { a, b, extra };
        using var commands = new CommandPanelViewModel(windows, _ => { }); using var vm = new YmodemViewModel(windows, commands, _ => { }) { FilePath = _file };
        var ra = new WaitingTransport(); var rb = new WaitingTransport();
        vm.TransportFactory = (window, _) => Task.FromResult<IBinarySerialTransport>(window == a ? ra : rb);
        var batch = vm.StartAsync();
        await Task.WhenAll(ra.Reading.Task, rb.Reading.Task).WaitAsync(TimeSpan.FromSeconds(2));
        extra.IsSelectedForSend = true;
        await vm.CancelAndWaitAsync(a.Id).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(a.IsFirmwareBusy); Assert.True(b.IsFirmwareBusy); Assert.True(vm.IsRunning); Assert.False(batch.IsCompleted);
        Assert.Equal(2, vm.TargetProgress.Count); Assert.True(ra.Disposed); Assert.False(rb.Disposed);
        await vm.CancelAndWaitAsync(); await batch;
        Assert.False(b.IsFirmwareBusy); Assert.True(rb.Disposed); Assert.False(vm.IsRunning);
    }
    [Fact]
    public async Task Completed_result_cannot_be_overwritten_by_late_concurrent_progress_callbacks()
    {
        using var target = Connected("1");
        var row = new YmodemTargetProgress(target, 2100, default) { IsRunning = true };
        var callbacks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 10000; i++) row.ReportWhileRunning(new("过期重传进度", i % 2100, 2100, 123, TimeSpan.FromSeconds(1)));
        })).ToArray();
        row.Complete("文件传输完成，后置规则完成", true);
        await Task.WhenAll(callbacks);
        Assert.Equal("文件传输完成，后置规则完成", row.Status); Assert.Equal(100, row.Percentage);
        Assert.False(row.ReportWhileRunning(new("不能覆盖", 0, 2100, 0, TimeSpan.Zero)));
        row.Cancellation.Dispose();
    }
    [Fact]
    public async Task Custom_parallel_jobs_keep_each_devices_configuration_variables_independent()
    {
        using var a = Connected("1"); using var b = Connected("2"); a.BaudRate = b.BaudRate = 460800;
        var windows = new ObservableCollection<SerialWindowViewModel> { a, b };
        using var commands = new CommandPanelViewModel(windows, _ => { }); using var vm = new YmodemViewModel(windows, commands, _ => { }) { FilePath = _file, Mode = YmodemMode.Custom };
        var ra = new YmodemSenderTests.Receiver(gateway: true); var rb = new YmodemSenderTests.Receiver(gateway: true);
        ra.Configuration[1] = 0x33; rb.Configuration[1] = 0x99;
        vm.TransportFactory = (window, _) => Task.FromResult<IBinarySerialTransport>(window == a ? ra : rb);
        await vm.StartAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0x33, ra.ConfigurationWritten![1]); Assert.Equal(0x99, rb.ConfigurationWritten![1]);
        Assert.Equal(2, ra.ConfigurationWritten[0]); Assert.Equal(2, rb.ConfigurationWritten[0]);
        Assert.All(vm.TargetProgress, row => { Assert.Equal(100, row.Percentage); Assert.Contains("后置规则完成", row.Status); });
    }
    [Fact]
    public async Task Active_job_uses_rule_snapshot_not_later_profile_edits()
    {
        using var a = Connected("1"); a.BaudRate = 460800;
        var windows = new ObservableCollection<SerialWindowViewModel> { a };
        using var commands = new CommandPanelViewModel(windows, _ => { }); using var vm = new YmodemViewModel(windows, commands, _ => { }) { FilePath = _file, Mode = YmodemMode.Custom };
        var profile = new YmodemRuleProfile { Before = [new() { Name = "开始等待", Kind = YmodemRuleKind.Wait, Receive = new() { Kind = RuleDataKind.Text, Value = "ready" } },
            new() { Name = "发送原值", Kind = YmodemRuleKind.Send, Send = new() { Kind = RuleDataKind.Hex, Value = "55" } }] };
        vm.LoadProfiles([profile], profile.Id);
        var transport = new GatedReceiver(); vm.TransportFactory = (_, _) => Task.FromResult<IBinarySerialTransport>(transport);
        var batch = vm.StartAsync(); await transport.Reading.Task.WaitAsync(TimeSpan.FromSeconds(2));
        vm.Profiles[0].Before[1].Send.Value = "66";
        Assert.Equal("55", profile.Before[1].Send.Value);
        transport.Ready();
        await batch.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new byte[] { 0x55 }, transport.PreCommand);
        Assert.True(transport.Disposed); Assert.Equal(100, Assert.Single(vm.TargetProgress).Percentage);
    }
    private sealed class GatedReceiver : IBinarySerialTransport
    {
        private readonly Channel<byte> _ready = Channel.CreateUnbounded<byte>();
        private readonly YmodemSenderTests.Receiver _receiver = new();
        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[]? PreCommand;
        public bool Disposed;
        public void Ready() { foreach (var b in Encoding.ASCII.GetBytes("ready")) _ready.Writer.TryWrite(b); }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token)
        {
            if (PreCommand is not null) return await _receiver.ReadAsync(buffer, token);
            Reading.TrySetResult(); var b = await _ready.Reader.ReadAsync(token); buffer.Span[0] = b; return 1;
        }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token)
        {
            if (PreCommand is null) { PreCommand = buffer.ToArray(); return ValueTask.CompletedTask; }
            return _receiver.WriteAsync(buffer, token);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class WaitingTransport : IBinarySerialTransport
    {
        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed;
        public async ValueTask<int> ReadAsync(Memory<byte> data, CancellationToken token)
        { Reading.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return 0; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    public void Dispose() => Directory.Delete(_directory, true);
}
