using System.Collections.ObjectModel;
using SerialLog.Core.Collaboration;
using SerialLog.Core.Configuration;

namespace SerialLog.App.ViewModels;

public sealed record CommandPackagePeer(string ConnectionId, string Name);
public sealed record ReceivedCommandPackage(string Id, string SourceName, string Json, DateTimeOffset ReceivedAt)
{
    public string DisplayName => $"{SourceName} · {ReceivedAt:HH:mm:ss}";
}

public sealed partial class CommandPanelViewModel
{
    public ObservableCollection<ReceivedCommandPackage> ReceivedCommandPackages { get; } = [];
    public Func<IReadOnlyList<CommandPackagePeer>> CommandPackagePeers { get; set; } = () => [];
    public Func<string, string, Task<string>> SendCommandPackage { get; set; } = (_, _) =>
        Task.FromException<string>(new InvalidOperationException("协作未连接。"));

    public void ImportCommandPackage(string json)
    {
        var package = CommandGroupPackageCodec.PrepareImport(json, CommandGroups.Select(g => g.Name), CommandGroupLists.Select(l => l.Name));
        // 所有解析和引用校验先完成，再追加到编辑副本。
        foreach (var list in package.Lists) CommandGroupLists.Add(new(list));
        GroupListSearchText = string.Empty;
        SelectedCommandGroupList = AllCommandGroupsList;
        foreach (var config in package.Groups)
        {
            var group = new CommandGroupEditorViewModel(config);
            CommandGroups.Add(group); SyncCommandGroupTargets(group); SelectedCommandGroup = group;
        }
        SetStatus($"已导入 {package.Groups.Count} 个命令组、{package.Lists.Count} 个列表；请重新选择目标，保存后生效。");
    }
}

public sealed partial class MainViewModel
{
    private readonly Dictionary<string, TaskCompletionSource<string>> _commandPackageReceipts = new();
    public ObservableCollection<ReceivedCommandPackage> ReceivedWorkspacePackages { get; } = [];
    public Task<string> SendWorkspacePackageAsync(string target, string json) =>
        SendConfigurationPackageAsync(target, json, ConfigurationPackageKind.Workspace);

    private void InitializeCommandPackageSharing()
    {
        CommandPanel.CommandPackagePeers = () => !IsCollaborationRunning ||
            (WorkspaceMode == WorkspaceMode.Client && !_collaborationClient.CanTransferCommandGroups) ? [] :
            SharedDirectory.Where(peer => peer.ConnectionId != LocalConnectionId && peer.SupportsCommandGroupTransfer)
                .Select(peer => new CommandPackagePeer(peer.ConnectionId, $"{peer.PcName} / {peer.WorkspaceName}" )).ToArray();
        CommandPanel.SendCommandPackage = (target, json) => SendConfigurationPackageAsync(target, json, ConfigurationPackageKind.CommandGroups);
        _collaborationHost.CommandGroupTransferReceived += CommandPackageReceived;
        _collaborationClient.CommandGroupTransferReceived += CommandPackageReceived;
    }

    private async Task<string> SendConfigurationPackageAsync(string target, string json, ConfigurationPackageKind kind)
    {
            if (kind == ConfigurationPackageKind.Workspace) WorkspacePackageCodec.Parse(json);
            else CommandGroupPackageCodec.Parse(json);
            if (!CommandPanel.CommandPackagePeers().Any(peer => peer.ConnectionId == target))
                throw new InvalidOperationException("接收工作区未连接或不支持传输，请双方使用新版程序。");
            if (_commandPackageReceipts.Count >= 8) throw new InvalidOperationException("等待回执的任务过多，请稍后再试。");
            var transfer = new CollaborationCommandGroupTransfer(Guid.NewGuid().ToString("N"), LocalConnectionId, target, json, Kind: kind);
            var receipt = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _commandPackageReceipts.Add(transfer.Id, receipt);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_collaborationLifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await SendCommandPackageMessageAsync(transfer, timeout.Token);
                return await receipt.Task.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!_collaborationLifetime.IsCancellationRequested)
            { throw new TimeoutException("未收到接收回执，请先让对方检查“接收配置”，避免重复发送。"); }
            finally { _commandPackageReceipts.Remove(transfer.Id); }
    }

    private Task SendCommandPackageMessageAsync(CollaborationCommandGroupTransfer transfer, CancellationToken token) => WorkspaceMode == WorkspaceMode.Host
        ? _collaborationHost.SendCommandGroupsAsync(transfer, token)
        : _collaborationClient.SendCommandGroupsAsync(transfer, token);

    private void CommandPackageReceived(object? sender, CollaborationCommandGroupTransfer transfer)
    {
        RunCollaborationOnUi(() =>
        {
            if (transfer.TargetConnectionId != LocalConnectionId) return;
            if (transfer.IsReceipt)
            {
                if (_commandPackageReceipts.TryGetValue(transfer.Id, out var receipt))
                {
                    if (transfer.Accepted) receipt.TrySetResult(transfer.Kind == ConfigurationPackageKind.Workspace
                        ? "对方已接收工作区快照，等待其确认新建工作区。" : "对方已接收，等待其在命令组设置中确认导入。");
                    else receipt.TrySetException(new InvalidOperationException(transfer.Detail));
                }
                return;
            }
            var accepted = false; var detail = "";
            try
            {
                var isWorkspace = transfer.Kind == ConfigurationPackageKind.Workspace;
                if (isWorkspace) WorkspacePackageCodec.Parse(transfer.PackageJson ?? "");
                else CommandGroupPackageCodec.Parse(transfer.PackageJson ?? "");
                var inbox = isWorkspace ? ReceivedWorkspacePackages : CommandPanel.ReceivedCommandPackages;
                if (!inbox.Any(item => item.Id == transfer.Id))
                {
                    if (inbox.Count >= (isWorkspace ? 5 : 10)) throw new InvalidOperationException("接收箱已满，请先处理已有配置。");
                    var source = SharedDirectory.FirstOrDefault(peer => peer.ConnectionId == transfer.SourceConnectionId);
                    var name = source is null ? "协作电脑" : $"{source.PcName} / {source.WorkspaceName}";
                    inbox.Add(new(transfer.Id, name, transfer.PackageJson!, DateTimeOffset.Now));
                    StatusText = isWorkspace ? $"收到 {name} 的工作区快照，请在“工作区 → 接收工作区快照”确认新建。"
                        : $"收到 {name} 的命令组配置，请在本工作区“命令组设置 → 接收配置”确认导入。";
                }
                accepted = true;
            }
            catch (Exception ex) { detail = ex.Message; StatusText = "接收配置失败：" + detail; }
            _ = ReplyAsync(transfer, accepted, detail);
        });
    }

    private async Task ReplyAsync(CollaborationCommandGroupTransfer transfer, bool accepted, string detail)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_collaborationLifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await SendCommandPackageMessageAsync(new(transfer.Id, LocalConnectionId, transfer.SourceConnectionId,
            IsReceipt: true, Accepted: accepted, Detail: detail, Kind: transfer.Kind), timeout.Token); }
        catch (Exception ex) { if (!_isDisposed) StatusText = "配置回执发送失败：" + ex.Message; }
    }
}
