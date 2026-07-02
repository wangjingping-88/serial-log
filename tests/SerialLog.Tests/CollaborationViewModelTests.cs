using SerialLog.App.ViewModels;
using SerialLog.Core.Configuration;

namespace SerialLog.Tests;

public class CollaborationViewModelTests
{
    [Fact]
    public void Host_mode_uses_real_ipv4_address_provider()
    {
        var viewModel = new CollaborationViewModel(() => "192.168.50.20")
        {
            HostAddress = "127.0.0.1"
        };

        viewModel.WorkspaceMode = WorkspaceMode.Host;

        Assert.Equal("192.168.50.20", viewModel.HostAddress);
        Assert.Equal("主机模式", viewModel.ModeStatusText);
    }

    [Fact]
    public void Loading_host_workspace_refreshes_loopback_address()
    {
        var viewModel = new CollaborationViewModel(() => "192.168.50.21");

        viewModel.LoadFromConfig(new WorkspaceConfig
        {
            WorkspaceMode = WorkspaceMode.Host,
            HostAddress = "127.0.0.1",
            HostPort = 58730
        });

        Assert.Equal("192.168.50.21", viewModel.HostAddress);
    }

    [Fact]
    public void Loading_local_workspace_refreshes_stale_host_address()
    {
        var viewModel = new CollaborationViewModel(() => "192.168.50.22");

        viewModel.LoadFromConfig(new WorkspaceConfig
        {
            WorkspaceMode = WorkspaceMode.Local,
            HostAddress = "172.18.64.1",
            HostPort = 58730
        });

        Assert.Equal("192.168.50.22", viewModel.HostAddress);
    }

    [Fact]
    public void Color_options_start_with_automatic_swatch()
    {
        var viewModel = new CollaborationViewModel(() => "192.168.50.20");

        Assert.Equal("自动", viewModel.PcColorOptions[0].Name);
        Assert.StartsWith("#", viewModel.PcColorOptions[0].Hex);
        Assert.Equal(7, viewModel.PcColorOptions[0].Hex.Length);
    }
}
