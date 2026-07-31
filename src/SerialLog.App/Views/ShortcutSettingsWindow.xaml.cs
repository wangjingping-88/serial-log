using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SerialLog.App.Infrastructure;
using SerialLog.App.Shortcuts;
using SerialLog.Core.Configuration;

namespace SerialLog.App.Views;

public partial class ShortcutSettingsWindow : Window
{
    private ShortcutEditorRow? _capturingRow;

    public ShortcutSettingsWindow(IEnumerable<ShortcutBindingConfig> bindings)
    {
        InitializeComponent();

        var manager = new ShortcutManager(bindings);
        var configured = manager.ExportBindings()
            .ToDictionary(binding => binding.ActionId, StringComparer.OrdinalIgnoreCase);
        Rows = new ObservableCollection<ShortcutEditorRow>(
            ShortcutManager.Definitions.Select(definition =>
                new ShortcutEditorRow(
                    definition.ActionId,
                    definition.DisplayName,
                    configured[definition.ActionId].Gesture)));
        DataContext = this;
        ValidateRows();
    }

    public ObservableCollection<ShortcutEditorRow> Rows { get; }

    public IReadOnlyList<ShortcutBindingConfig> ResultBindings { get; private set; } = [];

    private void ModifyShortcutButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ShortcutEditorRow row })
        {
            return;
        }

        CancelCapture();
        _capturingRow = row;
        row.IsCapturing = true;
        row.ErrorText = string.Empty;
        Focus();
        Keyboard.Focus(this);
    }

    private void ClearShortcutButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ShortcutEditorRow row })
        {
            return;
        }

        CancelCapture();
        row.Gesture = string.Empty;
        ValidateRows();
    }

    private void RestoreDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        CancelCapture();
        var defaults = ShortcutManager.Definitions.ToDictionary(
            definition => definition.ActionId,
            definition => definition.DefaultGesture,
            StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows)
        {
            row.Gesture = defaults[row.ActionId];
        }

        ValidateRows();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        CancelCapture();
        if (!ValidateRows())
        {
            return;
        }

        ResultBindings = BuildBindings();
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturingRow is null)
        {
            return;
        }

        e.Handled = true;
        var row = _capturingRow;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            CancelCapture();
            ValidateRows();
            return;
        }

        if (key == Key.Back)
        {
            row.Gesture = string.Empty;
            CancelCapture();
            ValidateRows();
            return;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or
            Key.LeftShift or Key.RightShift or
            Key.LeftAlt or Key.RightAlt or
            Key.LWin or Key.RWin)
        {
            row.ErrorText = "请继续按下非修饰键";
            return;
        }

        var gesture = new ShortcutGesture(key, Keyboard.Modifiers);
        var error = ShortcutManager.ValidateGesture(gesture);
        if (error is not null)
        {
            row.ErrorText = error;
            return;
        }

        row.Gesture = gesture.ToCanonicalString();
        CancelCapture();
        ValidateRows();
    }

    private void CancelCapture()
    {
        if (_capturingRow is null)
        {
            return;
        }

        _capturingRow.IsCapturing = false;
        _capturingRow = null;
    }

    private bool ValidateRows()
    {
        var validation = ShortcutManager.ValidateBindings(BuildBindings());
        foreach (var row in Rows)
        {
            row.ErrorText = validation.Errors.GetValueOrDefault(row.ActionId, string.Empty);
        }

        SaveButton.IsEnabled = validation.IsValid;
        return validation.IsValid;
    }

    private IReadOnlyList<ShortcutBindingConfig> BuildBindings()
    {
        return Rows.Select(row => new ShortcutBindingConfig
        {
            ActionId = row.ActionId,
            Gesture = row.Gesture
        }).ToList();
    }
}

public sealed class ShortcutEditorRow : ObservableObject
{
    private string _gesture;
    private string _errorText = string.Empty;
    private bool _isCapturing;

    public ShortcutEditorRow(string actionId, string displayName, string gesture)
    {
        ActionId = actionId;
        DisplayName = displayName;
        _gesture = gesture;
    }

    public string ActionId { get; }

    public string DisplayName { get; }

    public string Gesture
    {
        get => _gesture;
        set
        {
            if (SetProperty(ref _gesture, value))
            {
                OnPropertyChanged(nameof(DisplayGesture));
            }
        }
    }

    public string DisplayGesture => IsCapturing
        ? "请按下新快捷键…"
        : string.IsNullOrWhiteSpace(Gesture)
            ? "未设置"
            : Gesture;

    public string ErrorText
    {
        get => _errorText;
        set
        {
            if (SetProperty(ref _errorText, value))
            {
                OnPropertyChanged(nameof(IsInvalid));
            }
        }
    }

    public bool IsInvalid => !string.IsNullOrWhiteSpace(ErrorText);

    public bool IsCapturing
    {
        get => _isCapturing;
        set
        {
            if (SetProperty(ref _isCapturing, value))
            {
                OnPropertyChanged(nameof(DisplayGesture));
            }
        }
    }
}
