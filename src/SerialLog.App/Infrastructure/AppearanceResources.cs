using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SerialLog.App.Infrastructure;

/// <summary>窗口级外观资源；不修改工作区配置，也不把活动工作区颜色写入全局资源。</summary>
internal static class AppearanceResources
{
    private sealed class Scope(Color accent) { internal Color Accent = accent; }
    private static readonly ConditionalWeakTable<FrameworkElement, Scope> Scopes = new();
    private static readonly List<WeakReference<FrameworkElement>> Roots = [];
    private static readonly ConditionalWeakTable<Popup, object> NativePopups = new();
    private static bool _installed;
    internal static readonly Color DefaultAccent = Color.FromRgb(11, 117, 183);

    internal static void Install()
    {
        if (_installed) return;
        _installed = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(WindowLoaded));
        EventManager.RegisterClassHandler(typeof(Control), FrameworkElement.LoadedEvent, new RoutedEventHandler(ControlLoaded));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(PopupOpened));
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler(PopupOpened));
        SystemParameters.StaticPropertyChanged += SystemChanged;
    }

    internal static void Apply(FrameworkElement root, string accent)
    {
        var color = DefaultAccent;
        try { if (ColorConverter.ConvertFromString(accent) is Color parsed) color = parsed; }
        catch (FormatException) { /* 无效外观值不应使窗口无法打开，配置保持原样。 */ }
        Apply(root, color);
    }

    private static void Apply(FrameworkElement root, Color accent)
    {
        if (!Scopes.TryGetValue(root, out var scope)) { Scopes.Add(root, new Scope(accent)); Roots.Add(new(root)); }
        else scope.Accent = accent;
        foreach (var (key, brush) in CreatePalette(accent, SystemParameters.HighContrast)) root.Resources[key] = brush;
    }

    internal static void CaptureOwner(FrameworkElement root, FrameworkElement? owner)
    {
        var accent = (owner?.TryFindResource("AccentBrush") as SolidColorBrush)?.Color ?? DefaultAccent;
        Apply(root, accent);
    }

    private static void WindowLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Window window && !Scopes.TryGetValue(window, out _)) CaptureOwner(window, window.Owner);
    }

    private static void PopupOpened(object sender, RoutedEventArgs args)
    {
        if (sender is ContextMenu menu) { CaptureOwner(menu, menu.PlacementTarget as FrameworkElement); MainWindow.EnableNativePopupWindowChrome(menu); }
        if (sender is ToolTip tip) { CaptureOwner(tip, tip.PlacementTarget as FrameworkElement); MainWindow.EnableNativePopupWindowChrome(tip); }
    }

    private static void ControlLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is not Control control) return;
        ControlFeedback.Attach(control);
        if (control.Template?.FindName("PART_Popup", control) is Popup popup && !NativePopups.TryGetValue(popup, out _))
        {
            NativePopups.Add(popup, new object());
            popup.Opened += NativePopupOpened;
        }
    }

    private static void NativePopupOpened(object? sender, EventArgs args)
    {
        if (sender is Popup popup) MainWindow.EnableNativePopupWindowChrome(popup);
    }

    private static void SystemChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(SystemParameters.HighContrast) or nameof(SystemParameters.ClientAreaAnimation))) return;
        foreach (var reference in Roots.ToArray())
        {
            if (!reference.TryGetTarget(out var root)) { Roots.Remove(reference); continue; }
            void Refresh() { if (Scopes.TryGetValue(root, out var scope)) Apply(root, scope.Accent); }
            if (root.Dispatcher.CheckAccess()) Refresh(); else root.Dispatcher.BeginInvoke((Action)Refresh);
        }
        ControlFeedback.RefreshSystemSettings();
    }

    internal static IReadOnlyDictionary<string, Brush> CreatePalette(Color accent, bool highContrast)
    {
        static SolidColorBrush Brush(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
        static Color Hex(string value) => (Color)ColorConverter.ConvertFromString(value);
        var textAccent = accent;
        for (var index = 0; Contrast(textAccent, Colors.White) < 4.5 && index < 255; index++) textAccent = Mix(textAccent, Colors.Black, .04);
        // 实心控件统一白字；浅主题色仅为控件派生较深底色，保留原主题配置及设备颜色。
        var solidAccent = textAccent;
        var onAccent = Colors.White;
        // 大面积按钮使用浅色面，降低与白色工具界面的反差；勾选标记仍使用实心白字。
        var primaryBackground = Mix(Colors.White, accent, .18);
        var primaryHover = Mix(Colors.White, accent, .26);
        var primaryPressed = Mix(Colors.White, accent, .34);
        var primaryText = textAccent;
        for (var index = 0; Contrast(primaryText, primaryPressed) < 4.5 && index < 255; index++)
            primaryText = Mix(primaryText, Colors.Black, .04);
        var colors = new Dictionary<string, Color>
        {
            ["BackgroundBrush"] = Hex("#F3F5F8"), ["PanelBrush"] = Colors.White, ["PanelAltBrush"] = Hex("#F7F8FA"),
            ["BorderBrushSoft"] = Hex("#D6DCE5"), ["InputBorderBrush"] = Hex("#8995A5"),
            ["TextBrush"] = Hex("#1F2937"), ["MutedBrush"] = Hex("#5F6B7A"),
            ["LogBackgroundBrush"] = Colors.White, ["LogTextBrush"] = Hex("#111827"),
            ["AccentBrush"] = accent, ["AccentTextBrush"] = textAccent, ["OnAccentBrush"] = onAccent,
            ["SolidAccentBrush"] = solidAccent,
            ["PrimaryBackgroundBrush"] = primaryBackground, ["PrimaryTextBrush"] = primaryText,
            ["PrimaryBorderBrush"] = Mix(Colors.White, textAccent, .45),
            ["PrimaryButtonHoverBrush"] = primaryHover, ["PrimaryButtonPressedBrush"] = primaryPressed,
            ["PrimaryHoverBrush"] = Mix(solidAccent, Colors.Black, .18),
            ["PrimaryPressedBrush"] = Mix(solidAccent, Colors.Black, .30),
            ["AccentSoftBrush"] = Mix(Colors.White, accent, .1), ["ControlHoverBrush"] = Mix(Colors.White, textAccent, .14),
            ["ControlPressedBrush"] = Mix(Colors.White, textAccent, .24), ["SelectionBrush"] = Mix(Colors.White, accent, .13),
            ["InactiveSelectionBrush"] = Hex("#E9EDF2"), ["FocusBrush"] = textAccent,
            ["DangerBrush"] = Hex("#B42318"), ["DangerSoftBrush"] = Hex("#FFF1F0"), ["DangerBorderBrush"] = Hex("#D9A19B"),
            ["DisabledTextBrush"] = Hex("#7B8492"), ["DisabledBrush"] = Hex("#F0F2F5")
        };
        if (highContrast)
        {
            foreach (var key in colors.Keys.ToArray()) colors[key] = key switch
            {
                "BackgroundBrush" or "PanelBrush" or "PanelAltBrush" or "DisabledBrush" or "DangerSoftBrush" or "AccentSoftBrush" or "ControlHoverBrush" or "ControlPressedBrush" or "SelectionBrush" or "InactiveSelectionBrush" => SystemColors.WindowColor,
                "LogBackgroundBrush" => SystemColors.WindowColor,
                "TextBrush" or "MutedBrush" or "DangerBrush" or "LogTextBrush" => SystemColors.WindowTextColor,
                "DisabledTextBrush" => SystemColors.GrayTextColor,
                "PrimaryHoverBrush" or "PrimaryPressedBrush" or "PrimaryBackgroundBrush" or "PrimaryButtonHoverBrush" or "PrimaryButtonPressedBrush" => SystemColors.HighlightColor,
                "OnAccentBrush" or "PrimaryTextBrush" => SystemColors.HighlightTextColor,
                "AccentBrush" or "SolidAccentBrush" => SystemColors.HighlightColor,
                _ => SystemColors.WindowTextColor
            };
        }
        return colors.ToDictionary(pair => pair.Key, pair => (Brush)Brush(pair.Value));
    }

    private static Color Mix(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount), (byte)Math.Round(from.G + (to.G - from.G) * amount), (byte)Math.Round(from.B + (to.B - from.B) * amount));

    internal static double Contrast(Color first, Color second)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte value) { var part = value / 255d; return part <= .04045 ? part / 12.92 : Math.Pow((part + .055) / 1.055, 2.4); }
            return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        }
        var a = Luminance(first); var b = Luminance(second); return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}

/// <summary>仅动画模板内独立状态层的透明度；卸载解绑，不修改共享画刷或布局属性。</summary>
internal static class ControlFeedback
{
    private sealed class Subscription(Control control)
    {
        internal readonly Control Control = control;
        internal readonly Dictionary<string, Border> Layers = new();
        internal DependencyPropertyDescriptor? PressedDescriptor;
        internal DependencyPropertyDescriptor? ValidationDescriptor;
        internal void Changed(object? sender, EventArgs args) => Update(this, true);
        internal void KeyboardChanged(object sender, KeyboardFocusChangedEventArgs args) => Update(this, true);
        internal void EnabledChanged(object sender, DependencyPropertyChangedEventArgs args) => Update(this, false);
        internal void Unloaded(object sender, RoutedEventArgs args) => Detach(this);
    }
    private static readonly ConditionalWeakTable<Control, Subscription> Active = new();
    private static readonly List<WeakReference<Subscription>> Subscriptions = [];

    internal static void Attach(Control control)
    {
        if (Active.TryGetValue(control, out _)) return;
        var subscription = new Subscription(control);
        foreach (var name in new[] { "HoverLayer", "PressedLayer", "FocusLayer" })
            if (control.Template?.FindName(name, control) is Border layer) subscription.Layers[name] = layer;
        if (subscription.Layers.Count == 0) return;
        Active.Add(control, subscription); Subscriptions.Add(new(subscription));
        control.MouseEnter += subscription.Changed; control.MouseLeave += subscription.Changed;
        control.GotKeyboardFocus += subscription.KeyboardChanged; control.LostKeyboardFocus += subscription.KeyboardChanged;
        control.IsEnabledChanged += subscription.EnabledChanged; control.Unloaded += subscription.Unloaded;
        subscription.ValidationDescriptor = DependencyPropertyDescriptor.FromProperty(Validation.HasErrorProperty, control.GetType());
        subscription.ValidationDescriptor?.AddValueChanged(control, subscription.Changed);
        if (control is ToggleButton toggle)
        {
            toggle.Checked += subscription.Changed; toggle.Unchecked += subscription.Changed; toggle.Indeterminate += subscription.Changed;
        }
        if (control is ButtonBase)
        {
            subscription.PressedDescriptor = DependencyPropertyDescriptor.FromProperty(ButtonBase.IsPressedProperty, control.GetType());
            subscription.PressedDescriptor.AddValueChanged(control, subscription.Changed);
        }
        Update(subscription, false);
    }

    private static void Detach(Subscription subscription)
    {
        var control = subscription.Control;
        control.MouseEnter -= subscription.Changed; control.MouseLeave -= subscription.Changed;
        control.GotKeyboardFocus -= subscription.KeyboardChanged; control.LostKeyboardFocus -= subscription.KeyboardChanged;
        control.IsEnabledChanged -= subscription.EnabledChanged; control.Unloaded -= subscription.Unloaded;
        subscription.PressedDescriptor?.RemoveValueChanged(control, subscription.Changed);
        subscription.ValidationDescriptor?.RemoveValueChanged(control, subscription.Changed);
        if (control is ToggleButton toggle)
        {
            toggle.Checked -= subscription.Changed; toggle.Unchecked -= subscription.Changed; toggle.Indeterminate -= subscription.Changed;
        }
        foreach (var layer in subscription.Layers.Values) layer.BeginAnimation(UIElement.OpacityProperty, null);
        Active.Remove(control);
        Subscriptions.RemoveAll(reference => !reference.TryGetTarget(out var value) || ReferenceEquals(value, subscription));
    }

    private static void Update(Subscription subscription, bool animate)
    {
        var control = subscription.Control;
        var enabled = control.IsEnabled && !Validation.GetHasError(control);
        var filled = control is CheckBox { IsChecked: true } or CheckBox { IsChecked: null } ||
            control is Button && control.Background is SolidColorBrush background && control.TryFindResource("SolidAccentBrush") is SolidColorBrush accent && background.Color == accent.Color;
        var softPrimary = control is Button && control.Background is SolidColorBrush buttonBackground &&
            control.TryFindResource("PrimaryBackgroundBrush") is SolidColorBrush primary && buttonBackground.Color == primary.Color;
        foreach (var (name, layer) in subscription.Layers)
        {
            if (name is "HoverLayer" or "PressedLayer")
                layer.SetResourceReference(Border.BackgroundProperty, softPrimary
                    ? name == "HoverLayer" ? "PrimaryButtonHoverBrush" : "PrimaryButtonPressedBrush"
                    : filled ? name == "HoverLayer" ? "PrimaryHoverBrush" : "PrimaryPressedBrush"
                    : name == "HoverLayer" ? "ControlHoverBrush" : "ControlPressedBrush");
            var target = enabled && (name switch
            {
                "HoverLayer" => control.IsMouseOver,
                "PressedLayer" => control is ButtonBase { IsPressed: true },
                _ => control.IsKeyboardFocusWithin
            }) ? 1d : 0d;
            var current = layer.Opacity;
            layer.BeginAnimation(UIElement.OpacityProperty, null);
            layer.SetCurrentValue(UIElement.OpacityProperty, target);
            if (animate && enabled && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast && control.IsVisible && Math.Abs(current - target) > .001)
                layer.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(name == "PressedLayer" && target == 1 ? 80 : 180))
                {
                    FillBehavior = FillBehavior.Stop,
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });
        }
    }

    internal static void RefreshSystemSettings()
    {
        foreach (var reference in Subscriptions.ToArray())
        {
            if (!reference.TryGetTarget(out var value)) { Subscriptions.Remove(reference); continue; }
            if (value.Control.Dispatcher.CheckAccess()) Update(value, false);
            else value.Control.Dispatcher.BeginInvoke((Action)(() => Update(value, false)));
        }
    }
}
