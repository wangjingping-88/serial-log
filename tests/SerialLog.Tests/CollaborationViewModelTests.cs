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
        Assert.DoesNotContain(viewModel.PcColorOptions, option => option.Name == "自定义");
    }

    [Fact]
    public void Selecting_preset_color_applies_local_color()
    {
        var viewModel = new CollaborationViewModel(() => "192.168.50.20");
        var greenOption = viewModel.PcColorOptions.Single(option => option.Name == "绿");

        viewModel.SelectedPcColorOption = greenOption;

        Assert.Equal("#16A34A", viewModel.LocalPcColor);
        Assert.Equal(greenOption, viewModel.SelectedPcColorOption);
    }

    [Fact]
    public void Loading_workspace_with_removed_custom_color_falls_back_to_automatic()
    {
        var viewModel = new CollaborationViewModel(() => "192.168.50.20");
        var automaticColor = viewModel.PcColorOptions[0].Hex;

        viewModel.LoadFromConfig(new WorkspaceConfig
        {
            LocalPcColor = "#445566"
        });

        Assert.Equal(automaticColor, viewModel.LocalPcColor);
        Assert.Equal(viewModel.PcColorOptions[0], viewModel.SelectedPcColorOption);
    }
}
