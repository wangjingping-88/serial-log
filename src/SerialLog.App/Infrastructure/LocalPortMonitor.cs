using System.IO;
using System.IO.Ports;
using System.Windows.Threading;
using SerialLog.Core.Configuration;
using SerialLog.Core.Serial;

namespace SerialLog.App.Infrastructure;

/// <summary>应用级监听。后台读取系统清单，所有状态与 UI 通知在同一个 Dispatcher 提交。</summary>
public sealed class LocalPortMonitor : IDisposable
{
    private readonly string _settingsPath;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly DispatcherTimer _confirm = new() { Interval = TimeSpan.FromMilliseconds(330) };
    private bool _running, _readingNames, _readingDetails, _scanAgain, _detailsPending;
    private int _inventoryRevision;
    private DateTimeOffset _nextDetails;
    private nint _notification;

    public LocalPortMonitor(string settingsPath)
    {
        _settingsPath = settingsPath;
        try { Settings = PortInventorySettingsStore.Load(settingsPath, out var warning); SettingsWarning = warning; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            Settings = new();
            SettingsWarning = ex.Message;
            CanSaveSettings = false;
        }
        _poll.Tick += (_, _) => Refresh();
        _settle.Tick += (_, _) => { _settle.Stop(); Refresh(); };
        _confirm.Tick += (_, _) => { _confirm.Stop(); Refresh(); };
    }

    public PortInventory Inventory { get; } = new();
    public PortInventorySettings Settings { get; private set; }
    public bool CanSaveSettings { get; } = true;
    public string? SettingsWarning { get; private set; }
    public string? DetailsError { get; private set; }
    public event Action? Changed;
    public event Action<IReadOnlyList<LocalPortEvent>>? EventsDetected;
    public string[] PortNames() => Inventory.Ports.Select(p => p.Name).ToArray();
    public string PortLabel(string name) => Inventory.Find(name) is { } port ? Settings.Label(port)
        : Inventory.Initialized ? $"{name} · 未检测到" : name;

    public void Start(nint windowHandle)
    {
        if (_running) return;
        _running = true;
        if (windowHandle != 0) _notification = WindowsPortDiscovery.RegisterNotifications(windowHandle);
        _poll.Start();
        Refresh(includeDetails: true);
    }

    public void DeviceChanged()
    {
        if (!_running) return;
        _inventoryRevision++;
        _detailsPending = true;
        _confirm.Stop();
        _settle.Stop();
        _settle.Start();
    }

    public async void Refresh(bool includeDetails = false)
    {
        if (!_running) return;
        if (includeDetails) _detailsPending = true;
        if (_readingNames) { _scanAgain = true; return; }
        _readingNames = true;
        try
        {
            var revision = _inventoryRevision;
            var names = await Task.Run(SerialPort.GetPortNames);
            if (!_running || revision != _inventoryRevision) return;
            var hadError = Inventory.ScanError is not null;
            var first = !Inventory.Initialized;
            var events = Inventory.ApplyNames(names, DateTimeOffset.Now, Settings.Label);
            if (first || events.Count > 0)
            {
                _inventoryRevision++;
                _detailsPending = true;
            }
            if (Inventory.HasUnconfirmedRemoval) _confirm.Start(); else _confirm.Stop();
            if (first || hadError || events.Count > 0) Changed?.Invoke();
            if (events.Count > 0) EventsDetected?.Invoke(events);
            if (!Inventory.HasUnconfirmedRemoval && (_detailsPending || DateTimeOffset.UtcNow >= _nextDetails))
                RefreshDetails();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (!_running) return;
            Inventory.ReportFailure(ex.Message);
            Changed?.Invoke();
        }
        finally
        {
            _readingNames = false;
            if (_scanAgain) { _scanAgain = false; _ = _poll.Dispatcher.BeginInvoke(new Action(() => Refresh())); }
        }
    }

    private async void RefreshDetails()
    {
        if (_readingDetails) return;
        _readingDetails = true;
        _detailsPending = false;
        _nextDetails = DateTimeOffset.UtcNow.AddSeconds(30);
        var revision = _inventoryRevision;
        try
        {
            var details = await Task.Run(WindowsPortDiscovery.ReadDetails);
            if (!_running || revision != _inventoryRevision || Inventory.HasUnconfirmedRemoval || Inventory.ScanError is not null) return;
            var recovered = DetailsError is not null;
            DetailsError = null;
            if (Inventory.ApplyDetails(details, Settings.Label) || recovered) Changed?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
            if (!_running || revision != _inventoryRevision) return;
            DetailsError = ex.Message;
            Changed?.Invoke();
        }
        finally { _readingDetails = false; }
    }

    public void Save(PortInventorySettings settings)
    {
        if (!CanSaveSettings) throw new InvalidDataException(SettingsWarning);
        PortInventorySettingsStore.Save(_settingsPath, settings);
        Settings = settings;
        SettingsWarning = null;
        Changed?.Invoke();
    }

    public void ClearHistory()
    {
        Inventory.ClearHistory();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _running = false;
        _poll.Stop(); _settle.Stop(); _confirm.Stop();
        if (_notification != 0) _ = WindowsPortDiscovery.UnregisterDeviceNotification(_notification);
        _notification = 0;
    }
}
