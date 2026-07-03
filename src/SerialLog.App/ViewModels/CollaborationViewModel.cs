using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SerialLog.App.Infrastructure;
using SerialLog.Core.Configuration;

namespace SerialLog.App.ViewModels;

public sealed class CollaborationViewModel : ObservableObject
{
    private static readonly string[] AutomaticColorPalette =
    [
        "#0B75B7",
        "#16A34A",
        "#7C3AED",
        "#F97316",
        "#DC2626",
        "#0891B2",
        "#475569"
    ];

    private readonly Func<string> _hostAddressProvider;
    private readonly string _automaticPcColor;
    private WorkspaceMode _workspaceMode = WorkspaceMode.Local;
    private string _localPcId;
    private string _localPcName;
    private string _localPcColor;
    private PcColorOption? _selectedPcColorOption;
    private string _hostAddress = "127.0.0.1";
    private int _hostPort = 58730;

    public CollaborationViewModel(Func<string>? hostAddressProvider = null)
    {
        _hostAddressProvider = hostAddressProvider ?? ResolveLocalIpv4Address;
        _localPcId = Environment.MachineName.ToLowerInvariant();
        _localPcName = Environment.MachineName;
        _automaticPcColor = PickAutomaticColor(_localPcId);
        PcColorOptions = CreateColorOptions(_automaticPcColor);
        _selectedPcColorOption = PcColorOptions[0];
        _localPcColor = _selectedPcColorOption.Hex;
    }

    public IReadOnlyList<WorkspaceModeOption> WorkspaceModeOptions { get; } =
    [
        new(WorkspaceMode.Local, "本地"),
        new(WorkspaceMode.Host, "主机"),
        new(WorkspaceMode.Client, "客户端")
    ];

    public IReadOnlyList<PcColorOption> PcColorOptions { get; }

    public WorkspaceMode WorkspaceMode
    {
        get => _workspaceMode;
        set
        {
            if (!SetProperty(ref _workspaceMode, value))
            {
                return;
            }

            if (value == WorkspaceMode.Host)
            {
                HostAddress = _hostAddressProvider();
            }

            OnPropertyChanged(nameof(IsNetworked));
            OnPropertyChanged(nameof(ModeStatusText));
        }
    }

    public string LocalPcId
    {
        get => _localPcId;
        set => SetIdentityProperty(ref _localPcId, value);
    }

    public string LocalPcName
    {
        get => _localPcName;
        set => SetIdentityProperty(ref _localPcName, value);
    }

    public string LocalPcColor
    {
        get => _localPcColor;
        set
        {
            var color = ResolvePresetColor(value);
            if (SetIdentityProperty(ref _localPcColor, color))
            {
                SyncSelectedColorOption();
            }
        }
    }

    public PcColorOption? SelectedPcColorOption
    {
        get => _selectedPcColorOption;
        set
        {
            if (value is null || !SetProperty(ref _selectedPcColorOption, value))
            {
                return;
            }

            LocalPcColor = value.Hex;
        }
    }

    public string HostAddress
    {
        get => _hostAddress;
        set
        {
            if (SetProperty(ref _hostAddress, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(ModeStatusText));
            }
        }
    }

    public int HostPort
    {
        get => _hostPort;
        set
        {
            if (SetProperty(ref _hostPort, Math.Clamp(value, 1, 65535)))
            {
                OnPropertyChanged(nameof(ModeStatusText));
            }
        }
    }

    public bool IsNetworked => WorkspaceMode != WorkspaceMode.Local;

    public string ModeStatusText => WorkspaceMode switch
    {
        WorkspaceMode.Host => "主机模式",
        WorkspaceMode.Client => "客户端模式",
        _ => "本地模式"
    };

    public void LoadFromConfig(WorkspaceConfig config)
    {
        LocalPcId = string.IsNullOrWhiteSpace(config.LocalPcId)
            ? Environment.MachineName.ToLowerInvariant()
            : config.LocalPcId;
        LocalPcName = string.IsNullOrWhiteSpace(config.LocalPcName)
            ? Environment.MachineName
            : config.LocalPcName;
        LocalPcColor = string.IsNullOrWhiteSpace(config.LocalPcColor) ? _automaticPcColor : config.LocalPcColor;
        HostPort = config.HostPort <= 0 ? 58730 : config.HostPort;
        HostAddress = config.WorkspaceMode == WorkspaceMode.Client
            ? (string.IsNullOrWhiteSpace(config.HostAddress) ? "127.0.0.1" : config.HostAddress)
            : _hostAddressProvider();
        WorkspaceMode = config.WorkspaceMode;
    }

    public void SaveToConfig(WorkspaceConfig config)
    {
        config.WorkspaceMode = WorkspaceMode;
        config.LocalPcId = LocalPcId;
        config.LocalPcName = LocalPcName;
        config.LocalPcColor = LocalPcColor;
        config.HostAddress = HostAddress;
        config.HostPort = HostPort;
    }

    public void ApplyOwnership(IEnumerable<SerialWindowViewModel> windows)
    {
        foreach (var window in windows.Where(window => !window.IsRemote))
        {
            ApplyLocalOwner(window);
        }
    }

    public void ApplyLocalOwner(SerialWindowViewModel window)
    {
        if (window.IsRemote)
        {
            return;
        }

        if (!IsNetworked)
        {
            window.OwnerPcId = string.Empty;
            window.OwnerPcName = string.Empty;
            window.OwnerPcColor = LocalPcColor;
            return;
        }

        window.OwnerPcId = LocalPcId;
        window.OwnerPcName = LocalPcName;
        window.OwnerPcColor = LocalPcColor;
    }

    private static IReadOnlyList<PcColorOption> CreateColorOptions(string automaticColor)
    {
        return
        [
            new("自动", automaticColor),
            new("蓝", "#0B75B7"),
            new("绿", "#16A34A"),
            new("紫", "#7C3AED"),
            new("橙", "#F97316"),
            new("红", "#DC2626"),
            new("青", "#0891B2"),
            new("灰", "#475569")
        ];
    }

    private string ResolvePresetColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return _automaticPcColor;
        }

        var color = NormalizeHexColor(value);
        return PcColorOptions.Any(option => string.Equals(option.Hex, color, StringComparison.OrdinalIgnoreCase))
            ? color
            : _automaticPcColor;
    }

    private static string NormalizeHexColor(string value)
    {
        var color = value.Trim();
        if (!color.StartsWith("#", StringComparison.Ordinal))
        {
            color = "#" + color;
        }

        return color.Length == 7 && color.Skip(1).All(Uri.IsHexDigit)
            ? color.ToUpperInvariant()
            : string.Empty;
    }

    private void SyncSelectedColorOption()
    {
        var matchedOption = PcColorOptions.FirstOrDefault(option =>
            string.Equals(option.Hex, LocalPcColor, StringComparison.OrdinalIgnoreCase)) ?? PcColorOptions[0];
        if (!Equals(_selectedPcColorOption, matchedOption))
        {
            _selectedPcColorOption = matchedOption;
            OnPropertyChanged(nameof(SelectedPcColorOption));
        }
    }

    private static string PickAutomaticColor(string seed)
    {
        var hash = 2166136261u;
        foreach (var character in seed)
        {
            hash ^= char.ToUpperInvariant(character);
            hash *= 16777619;
        }

        return AutomaticColorPalette[hash % AutomaticColorPalette.Length];
    }

    private static string ResolveLocalIpv4Address()
    {
        var networkAddress = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .Where(adapter => adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Where(adapter => adapter.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .Where(adapter => !IsVirtualAdapter(adapter))
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses.Select(address => new
            {
                Adapter = adapter,
                Address = address.Address
            }))
            .Where(candidate => IsUsableIpv4Address(candidate.Address))
            .OrderBy(candidate => GetAdapterPriority(candidate.Adapter))
            .ThenBy(candidate => GetIpv4Priority(candidate.Address))
            .Select(candidate => candidate.Address.ToString())
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(networkAddress))
        {
            return networkAddress;
        }

        return Dns.GetHostEntry(Dns.GetHostName())
            .AddressList
            .Where(IsUsableIpv4Address)
            .OrderBy(GetIpv4Priority)
            .Select(address => address.ToString())
            .FirstOrDefault() ?? "127.0.0.1";
    }

    private static bool IsUsableIpv4Address(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes is not [169, 254, _, _];
    }

    private static bool IsVirtualAdapter(NetworkInterface adapter)
    {
        var name = $"{adapter.Name} {adapter.Description}".ToLowerInvariant();
        return name.Contains("virtual", StringComparison.Ordinal)
            || name.Contains("vethernet", StringComparison.Ordinal)
            || name.Contains("hyper-v", StringComparison.Ordinal)
            || name.Contains("wsl", StringComparison.Ordinal)
            || name.Contains("docker", StringComparison.Ordinal)
            || name.Contains("vmware", StringComparison.Ordinal)
            || name.Contains("virtualbox", StringComparison.Ordinal)
            || name.Contains("xray", StringComparison.Ordinal)
            || name.Contains("tunnel", StringComparison.Ordinal)
            || name.Contains("bluetooth", StringComparison.Ordinal);
    }

    private static int GetAdapterPriority(NetworkInterface adapter)
    {
        return adapter.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => 0,
            NetworkInterfaceType.Ethernet => 0,
            NetworkInterfaceType.GigabitEthernet => 0,
            NetworkInterfaceType.FastEthernetFx => 0,
            NetworkInterfaceType.FastEthernetT => 0,
            _ => 1
        };
    }

    private static int GetIpv4Priority(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes switch
        {
            [10, _, _, _] => 0,
            [172, >= 16 and <= 31, _, _] => 0,
            [192, 168, _, _] => 0,
            _ => 1
        };
    }

    private bool SetIdentityProperty(ref string field, string? value)
    {
        if (SetProperty(ref field, value ?? string.Empty))
        {
            OnPropertyChanged(nameof(ModeStatusText));
            return true;
        }

        return false;
    }
}
