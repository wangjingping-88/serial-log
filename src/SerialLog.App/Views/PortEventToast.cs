using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using SerialLog.App.Infrastructure;

namespace SerialLog.App.Views;

internal sealed class PortEventToast : Window
{
    private readonly Window _anchor;
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private HwndSource? _source;
    private bool _placing;

    internal PortEventToast(Window anchor)
    {
        _anchor = anchor;
        Title = "Serial Log 串口插拔";
        Width = 365;
        MaxHeight = 360;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        // 独立窗口：主窗口最小化时仍可显示，关闭应用时由控制器释放。
        var bounds = anchor.RestoreBounds;
        if (!bounds.IsEmpty) { Left = bounds.Left; Top = bounds.Top; }
        AppearanceResources.CaptureOwner(this, anchor);
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        var panel = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var close = new Button { Content = "关闭" };
        close.SetResourceReference(StyleProperty, "CompactButton");
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(new TextBlock { Text = "串口插拔", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(header);
        AutomationProperties.SetLiveSetting(_message, AutomationLiveSetting.Polite);
        panel.Children.Add(_message);
        var border = new Border { Padding = new Thickness(12), BorderThickness = new Thickness(1), Child = panel };
        border.SetResourceReference(Border.BorderBrushProperty, "PrimaryBorderBrush");
        Content = border;
        SourceInitialized += (_, _) =>
        {
            _source = PresentationSource.FromVisual(this) as HwndSource;
            _source?.AddHook(Hook);
        };
        Loaded += (_, _) => PlaceInWorkArea();
        SizeChanged += (_, _) => PlaceInWorkArea();
    }

    internal void Display(string text)
    {
        _message.Text = text;
        if (!IsVisible) Show();
        _ = Dispatcher.InvokeAsync(PlaceInWorkArea, DispatcherPriority.Loaded);
    }

    protected override void OnClosed(EventArgs e)
    {
        _source?.RemoveHook(Hook);
        _source = null;
        base.OnClosed(e);
    }

    private nint Hook(nint window, int message, nint parameter, nint data, ref bool handled)
    {
        if (message != 0x21) return 0;
        handled = true;
        return 3; // WM_MOUSEACTIVATE / MA_NOACTIVATE：点击关闭也不改变当前应用焦点。
    }

    private void PlaceInWorkArea()
    {
        if (_source is null || _placing) return;
        var handle = _source.Handle;
        var anchorHandle = new WindowInteropHelper(_anchor).Handle;
        var monitor = MonitorFromWindow(anchorHandle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfoW(monitor, ref info) || !GetWindowRect(handle, out var rect)) return;
        var scale = _source.CompositionTarget?.TransformToDevice.M11 ?? 1;
        var margin = (int)Math.Round(16 * scale);
        _placing = true;
        try
        {
            _ = SetWindowPos(handle, 0, Math.Max(info.Work.Left, info.Work.Right - (rect.Right - rect.Left) - margin),
                Math.Max(info.Work.Top, info.Work.Bottom - (rect.Bottom - rect.Top) - margin), 0, 0, 0x15); // NOSIZE | NOZORDER | NOACTIVATE
        }
        finally { _placing = false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectPixels { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public RectPixels Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out RectPixels bounds);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
