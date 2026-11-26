using System.Windows.Controls;
using System.Windows.Data;
using SerialLog.App.Infrastructure;
using SerialLog.App.ViewModels;
using SerialLog.App.Views;
using SerialLog.Core.Configuration;
using SerialLog.Core.Serial;

namespace SerialLog.Tests;

public sealed class PortInventoryTests
{
    [Fact]
    public void First_snapshot_is_silent_and_removal_requires_two_successful_missing_samples()
    {
        var inventory = new PortInventory();
        var now = DateTimeOffset.UtcNow;
        static string Label(LocalPortInfo p) => p.Name;
        Assert.Empty(inventory.ApplyNames(["com10", "COM3"], now, Label));
        Assert.Equal(["COM3", "COM10"], inventory.Ports.Select(p => p.Name));
        Assert.Empty(inventory.ApplyNames(["COM3"], now, Label));
        inventory.ReportFailure("读取失败");
        Assert.Equal(2, inventory.Ports.Count);
        Assert.Empty(inventory.ApplyNames(["COM3"], now, Label));
        Assert.False(Assert.Single(inventory.ApplyNames(["COM3"], now, Label)).Arrived);
        Assert.True(Assert.Single(inventory.ApplyNames(["COM3", "COM10"], now, Label)).Arrived);
        Assert.Equal(2, inventory.History.Count);
        Assert.Null(inventory.ScanError);
    }

    [Fact]
    public void Late_details_do_not_reinsert_absent_ports_and_reused_com_does_not_inherit_device_alias()
    {
        var inventory = new PortInventory();
        var settings = new PortInventorySettings { Aliases = { ["device-a"] = "网关甲" } };
        var now = DateTimeOffset.UtcNow;
        inventory.ApplyNames(["COM7"], now, settings.Label);
        inventory.ApplyDetails([new("COM7", "device-a")], settings.Label);
        Assert.Equal("COM7 · 网关甲", settings.Label(inventory.Find("COM7")!));
        inventory.ApplyNames([], now, settings.Label);
        var removed = Assert.Single(inventory.ApplyNames([], now, settings.Label));
        Assert.Equal("COM7 · 网关甲", removed.Label);
        Assert.False(inventory.ApplyDetails([new("COM7", "device-a")]));
        Assert.Null(inventory.Find("COM7"));

        inventory.ApplyNames(["COM7"], now, settings.Label);
        inventory.ApplyDetails([new("COM7", "device-b")], settings.Label);
        Assert.Equal("COM7", inventory.History[0].Label);
        Assert.Equal("device-b", inventory.History[0].Port.DeviceId);
    }

    [Fact]
    public void History_is_bounded_even_with_popup_disabled()
    {
        var settings = new PortInventorySettings { ShowNotifications = false };
        var inventory = new PortInventory();
        var now = DateTimeOffset.UtcNow;
        inventory.ApplyNames([], now, settings.Label);
        inventory.ApplyNames(Enumerable.Range(1, 100).Select(n => $"COM{n}"), now, settings.Label);
        Assert.Equal(80, inventory.History.Count);
        Assert.Equal("COM100", inventory.History[0].Port.Name);
        inventory.ClearHistory();
        Assert.Empty(inventory.History);
        Assert.Equal(100, inventory.Ports.Count);
    }

    [Fact]
    public void Notification_defaults_on_and_off_setting_survives_reload_and_backup_recovery()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "port-inventory.json");
        Assert.True(PortInventorySettingsStore.Load(path, out _).ShowNotifications);
        PortInventorySettingsStore.Save(path, new() { ShowNotifications = false });
        Assert.False(PortInventorySettingsStore.Load(path, out _).ShowNotifications);
        PortInventorySettingsStore.Save(path, new());
        File.WriteAllText(path, "{不完整");
        var restored = PortInventorySettingsStore.Load(path, out var warning);
        Assert.NotNull(warning);
        Assert.False(restored.ShowNotifications);
        PortInventorySettingsStore.Save(path, restored);
        Assert.False(PortInventorySettingsStore.Load(path, out _).ShowNotifications);
        Assert.Contains("false", File.ReadAllText(path + ".bak"));
    }

    [Fact]
    public void Unsupported_version_is_not_downgraded_even_with_backup()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "port-inventory.json");
        PortInventorySettingsStore.Save(path, new() { ShowNotifications = false });
        PortInventorySettingsStore.Save(path, new());
        File.WriteAllText(path, """{"schemaVersion":42}""");
        using var monitor = new LocalPortMonitor(path);
        Assert.False(monitor.CanSaveSettings);
        Assert.Throws<InvalidDataException>(() => monitor.Save(new()));
        Assert.Contains("42", File.ReadAllText(path));
    }

    [Fact]
    public void Legacy_import_preserves_order_and_supports_explicit_conflict_resolution()
    {
        using var temp = new TemporaryDirectory();
        var aliases = Path.Combine(temp.Path, "port-aliases.json");
        var groups = Path.Combine(temp.Path, "port-groups.json.bak");
        var source = """{"schemaVersion":1,"aliases":{"device-a":"原网关","port:COM4":"测试串口"}}""";
        File.WriteAllText(aliases, source);
        File.WriteAllText(groups, """
            {"schemaVersion":1,"groups":[{"id":"group-a","name":"网关","color":"#abcdef"}],
             "assignments":{"device-a":"group-a"},"orders":{"all":["device-a","port:COM4"]}}
            """);
        var incoming = PortInventorySettingsStore.ReadLegacy([aliases, groups]);
        var current = new PortInventorySettings { ShowNotifications = false, Aliases = { ["DEVICE-A"] = "现有网关" } };
        var port = new LocalPortInfo("COM8", "device-a");
        var (kept, count) = PortInventorySettingsStore.Merge(current, incoming, preferImported: false);
        Assert.Equal(1, count);
        Assert.Equal("现有网关", kept.Alias(port));
        Assert.Equal("group-a", kept.GroupId(port));
        Assert.Equal("#ABCDEF", Assert.Single(kept.Groups).Color);
        Assert.Equal(["device-a", "port:COM4"], kept.Orders["all"]);
        var imported = PortInventorySettingsStore.Merge(current, incoming, preferImported: true).Settings;
        Assert.Equal("原网关", imported.Alias(port));
        Assert.False(imported.ShowNotifications);
        Assert.Equal(source, File.ReadAllText(aliases));
    }

    [Fact]
    public void Device_override_can_clear_a_legacy_com_alias_without_affecting_other_devices()
    {
        var settings = new PortInventorySettings { Aliases = { ["port:COM7"] = "旧名", ["pnp-a"] = "" },
            Groups = [new("group", "网关", "#000000")], Assignments = { ["port:COM7"] = "group", ["pnp-a"] = "" } };
        settings = PortInventorySettingsStore.Clone(settings);
        Assert.Equal("", settings.Alias(new("COM7", "PNP-A")));
        Assert.Equal("", settings.GroupId(new("COM7", "PNP-A")));
        Assert.Equal("旧名", settings.Alias(new("COM7", "pnp-b")));
        Assert.Equal("group", settings.GroupId(new("COM7", "pnp-b")));
    }

    [Fact]
    public void Search_checks_each_field_and_returns_feedback_for_invalid_regex()
    {
        var rows = new[] { PortInventoryViewModel.CreateRow(new("COM7"), true, new(), []),
            PortInventoryViewModel.CreateRow(new("COM8"), true, new(), []) };
        Assert.Equal("COM7", Assert.Single(PortInventoryViewModel.Filter(rows, "/com7$/i", out var error)).PortName);
        Assert.Null(error);
        Assert.Empty(PortInventoryViewModel.Filter(rows, "/[/", out error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Native_discovery_can_read_device_metadata_without_claiming_a_port()
    {
        var ports = WindowsPortDiscovery.ReadDetails();
        Assert.All(ports, port =>
        {
            Assert.Matches("^COM[0-9]+$", port.Name);
            Assert.False(string.IsNullOrWhiteSpace(port.DeviceId));
        });
    }

    [Fact]
    public void Toggling_the_manager_checkbox_persists_preference_and_keeps_recent_events()
    {
        RunSta(() =>
        {
            using var temp = new TemporaryDirectory();
            var path = Path.Combine(temp.Path, "inventory.json");
            using var monitor = new LocalPortMonitor(path);
            using var manager = new TestWorkspaceManager(Path.Combine(temp.Path, "workspace.json"), false, monitor);
            monitor.Inventory.ApplyNames([], DateTimeOffset.UtcNow, monitor.Settings.Label);
            monitor.Inventory.ApplyNames(["COM999"], DateTimeOffset.UtcNow, monitor.Settings.Label);
            var dialog = new PortInventoryWindow(monitor, manager, _ => { });
            try
            {
                var toggle = Assert.IsType<CheckBox>(dialog.FindName("NotificationToggle"));
                Assert.True(toggle.IsChecked);
                toggle.IsChecked = false;
                Assert.False(monitor.Settings.ShowNotifications);
                Assert.False(PortInventorySettingsStore.Load(path, out _).ShowNotifications);
                Assert.Single(monitor.Inventory.History);
                toggle.IsChecked = true;
                Assert.True(PortInventorySettingsStore.Load(path, out _).ShowNotifications);
            }
            finally { dialog.Close(); }
        });
    }

    [Fact]
    public void Drop_down_keeps_configured_com_and_same_item_when_a_port_disappears_or_alias_changes()
    {
        RunSta(() =>
        {
            string[] names = ["COM3", "COM7"];
            var alias = "COM7 · 网关";
            using var window = new SerialWindowViewModel("port", "窗口", portNameProvider: () => names,
                portLabelProvider: port => port == "COM7" ? alias : port);
            window.PortName = "COM7";
            var combo = new ComboBox { ItemsSource = window.PortChoices, SelectedValuePath = "Name" };
            combo.SetBinding(ComboBox.SelectedValueProperty, new Binding(nameof(window.PortName)) { Source = window, Mode = BindingMode.TwoWay });
            var selected = Assert.IsType<SerialPortChoice>(combo.SelectedItem);
            names = ["COM3", "COM4"];
            alias = "COM7 · 未检测到";
            window.AutoRefreshPorts();
            Assert.Same(selected, combo.SelectedItem);
            Assert.Equal("COM7", combo.SelectedValue);
            Assert.Equal("COM7", window.PortName);
            Assert.Equal(alias, selected.Label);
        });
    }

    [Fact]
    public void Adding_a_detected_port_configures_only_the_requested_workspace_without_opening_it()
    {
        RunSta(() =>
        {
            using var temp = new TemporaryDirectory();
            using var monitor = new LocalPortMonitor(Path.Combine(temp.Path, "inventory.json"));
            monitor.Inventory.ApplyNames(["COM999"], DateTimeOffset.UtcNow, monitor.Settings.Label);
            using var manager = new TestWorkspaceManager(Path.Combine(temp.Path, "workspace.json"), false, monitor);
            var original = manager.Active;
            var oldCount = original.ViewModel.SerialWindows.Count;
            manager.Create("新工作区", copy: false);
            var added = manager.Active.ViewModel.AddInventoryWindow("COM999", "网关");
            Assert.Equal(oldCount, original.ViewModel.SerialWindows.Count);
            Assert.Equal("COM999", added.PortName);
            Assert.Equal("网关", added.Title);
            Assert.False(added.IsConnected);
            Assert.False(added.WantsConnection);
            Assert.False(added.IsSelectedForSend);
            var use = Assert.Single(PortInventoryViewModel.FindUses(manager.Workspaces, "COM999"));
            Assert.Equal("仅配置", use.State);
            Assert.Same(manager.Active, use.Workspace);
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "串口管理界面测试超时。");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
