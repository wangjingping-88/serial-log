using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SerialLog.App.Views;
using SerialLog.Core.Configuration;

namespace SerialLog.App;

public partial class MainWindow
{
    private void WorkspacePackage_Click(object sender, RoutedEventArgs e)
    {
        var workspace = _workspaces.Active;
        var viewModel = workspace.ViewModel;
        CloseOpenTitleBarMenus();
        try
        {
            switch ((sender as FrameworkElement)?.Tag as string)
            {
                case "export":
                {
                    var json = WorkspacePackageCodec.Export(workspace.Export());
                    var picker = new SaveFileDialog { Title = "导出工作区配置快照", Filter = "工作区快照 (*.json)|*.json", FileName = "workspace-snapshot.json" };
                    if (picker.ShowDialog(this) != true) return;
                    var temporary = picker.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try { File.WriteAllText(temporary, json); File.Move(temporary, picker.FileName, true); }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    viewModel.StatusText = "已导出工作区配置快照，不包含日志文件、COM 映射和运行状态。";
                    break;
                }
                case "import":
                {
                    var picker = new OpenFileDialog { Title = "从配置快照新建工作区", Filter = "工作区快照 (*.json)|*.json" };
                    if (picker.ShowDialog(this) != true) return;
                    if (new FileInfo(picker.FileName).Length > WorkspacePackageCodec.MaxBytes) throw new InvalidDataException("工作区快照超过 4 MiB。");
                    var json = File.ReadAllText(picker.FileName);
                    var snapshot = WorkspacePackageCodec.Parse(json);
                    var confirmation = new DeleteConfirmationWindow("从快照新建工作区",
                        $"创建“{snapshot.Name}”的独立副本（{snapshot.Configuration.SerialWindows.Count} 个窗口）？\n已有工作区不变。请重新选择 COM 口，不会自动连接或执行。", "新建") { Owner = this };
                    ((Button)confirmation.FindName("DeleteButton")).SetResourceReference(StyleProperty, "CompactPrimaryButton");
                    if (confirmation.ShowDialog() != true) return;
                    var added = _workspaces.ImportSnapshot(json);
                    viewModel.StatusText = $"已新建工作区“{added.Name}”，可通过工作区选择框切换；请先配置 COM 口。";
                    break;
                }
                case "send":
                {
                    var peers = viewModel.CommandPanel.CommandPackagePeers();
                    if (peers.Count == 0) throw new InvalidOperationException("没有支持配置传输的协作工作区，请连接协作并确认双方及主机使用新版程序。");
                    var dialog = new CommandPackageSendWindow(peers, WorkspacePackageCodec.Export(workspace.Export()), viewModel.SendWorkspacePackageAsync, workspace: true) { Owner = this };
                    if (dialog.ShowDialog() == true) viewModel.StatusText = dialog.ResultText;
                    break;
                }
                case "receive":
                {
                    var dialog = new CommandPackageInboxWindow(viewModel.ReceivedWorkspacePackages, [], workspace: true) { Owner = this };
                    if (dialog.ShowDialog() != true) return;
                    foreach (var item in dialog.SelectedPackages)
                    {
                        var added = _workspaces.ImportSnapshot(item.Json);
                        viewModel.ReceivedWorkspacePackages.Remove(item);
                        viewModel.StatusText = $"已新建工作区“{added.Name}”，原工作区保持运行；请切换后配置 COM 口。";
                    }
                    break;
                }
            }
        }
        catch (Exception ex) { viewModel.StatusText = "工作区配置交换失败：" + ex.Message; }
    }
}
