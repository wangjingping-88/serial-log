using System.Windows.Input;
using SerialLog.Core.Configuration;

namespace SerialLog.App.Shortcuts;

public static class ShortcutActionIds
{
    public const string OpenDocumentation = "help.openDocumentation";
    public const string AddPage = "page.add";
    public const string RemoveCurrentPage = "page.removeCurrent";
    public const string PreviousPage = "page.previous";
    public const string NextPage = "page.next";
    public const string AddSerialWindow = "serial.addWindow";
    public const string ToggleAllConnections = "serial.toggleAllConnections";
    public const string ToggleActiveWindowConnection = "serial.toggleActiveWindowConnection";
    public const string ToggleActiveWindowLogFollow = "log.toggleActiveWindowFollow";
    public const string ToggleAllWindowLogFollow = "log.toggleAllWindowFollow";
    public const string ClearActiveWindowLog = "log.clearActiveWindow";
    public const string ClearAllWindowLogs = "log.clearAllWindows";
    public const string ToggleCommandPanel = "view.toggleCommandPanel";
    public const string NewLogSession = "log.newSession";
    public const string BrowseLogDirectory = "log.browseDirectory";
    public const string ToggleCollaboration = "collaboration.toggle";
}

public sealed record ShortcutDefinition(
    string ActionId,
    string DisplayName,
    string DefaultGesture);

public readonly record struct ShortcutGesture(Key Key, ModifierKeys Modifiers)
{
    public string ToCanonicalString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        parts.Add(Key.ToString());
        return string.Join("+", parts);
    }

    public static bool TryParse(string? text, out ShortcutGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var modifiers = ModifierKeys.None;
        for (var index = 0; index < parts.Length - 1; index++)
        {
            if (parts[index].Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                parts[index].Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModifierKeys.Control;
            }
            else if (parts[index].Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModifierKeys.Shift;
            }
            else if (parts[index].Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModifierKeys.Alt;
            }
            else
            {
                return false;
            }
        }

        if (!Enum.TryParse(parts[^1], ignoreCase: true, out Key key))
        {
            return false;
        }

        gesture = new ShortcutGesture(key, modifiers);
        return true;
    }
}

public sealed class ShortcutValidationResult
{
    public ShortcutValidationResult(IReadOnlyDictionary<string, string> errors)
    {
        Errors = errors;
    }

    public IReadOnlyDictionary<string, string> Errors { get; }

    public bool IsValid => Errors.Count == 0;
}

public sealed class ShortcutManager
{
    private static readonly IReadOnlyList<ShortcutDefinition> DefinitionsInternal =
    [
        new(ShortcutActionIds.OpenDocumentation, "打开操作说明", "F1"),
        new(ShortcutActionIds.AddPage, "新增页面", "Ctrl+N"),
        new(ShortcutActionIds.RemoveCurrentPage, "删除当前页", "Alt+Delete"),
        new(ShortcutActionIds.PreviousPage, "上一页", "Left"),
        new(ShortcutActionIds.NextPage, "下一页", "Right"),
        new(ShortcutActionIds.AddSerialWindow, "新增串口窗口", "Alt+P"),
        new(ShortcutActionIds.ToggleAllConnections, "连接/断开全部", "Alt+L"),
        new(ShortcutActionIds.ToggleActiveWindowConnection, "\u8FDE\u63A5/\u65AD\u5F00\u5F53\u524D\u7A97\u53E3", "Ctrl+L"),
        new(ShortcutActionIds.ToggleCommandPanel, "显示/隐藏命令区", "Ctrl+M"),
        new(ShortcutActionIds.NewLogSession, "新建日志会话", "Alt+N"),
        new(ShortcutActionIds.BrowseLogDirectory, "浏览日志目录", "Alt+O"),
        new(ShortcutActionIds.ToggleCollaboration, "启动/停止多机协作", "Alt+I"),
        new(ShortcutActionIds.ClearActiveWindowLog, "\u6E05\u7A7A\u5F53\u524D\u7A97\u53E3\u65E5\u5FD7", "Ctrl+K"),
        new(ShortcutActionIds.ClearAllWindowLogs, "\u6E05\u7A7A\u5168\u90E8\u7A97\u53E3\u65E5\u5FD7", "Alt+K"),
        new(ShortcutActionIds.ToggleActiveWindowLogFollow, "\u6682\u505C/\u6062\u590D\u5F53\u524D\u7A97\u53E3\u65E5\u5FD7\u8DDF\u968F", "Ctrl+S"),
        new(ShortcutActionIds.ToggleAllWindowLogFollow, "\u6682\u505C/\u6062\u590D\u5168\u90E8\u7A97\u53E3\u65E5\u5FD7\u8DDF\u968F", "Alt+S"),
    ];

    private static readonly IReadOnlyDictionary<ShortcutGesture, string> ReservedGestures =
        new Dictionary<ShortcutGesture, string>
        {
            [new ShortcutGesture(Key.A, ModifierKeys.Control)] = "日志全选 Ctrl+A",
            [new ShortcutGesture(Key.C, ModifierKeys.Control)] = "日志复制 Ctrl+C",
            [new ShortcutGesture(Key.Enter, ModifierKeys.None)] = "日志恢复跟随 Enter",
            [new ShortcutGesture(Key.Delete, ModifierKeys.None)] = "命令列表删除 Delete"
        };

    private readonly Dictionary<string, string> _bindings =
        new(StringComparer.OrdinalIgnoreCase);

    public ShortcutManager(IEnumerable<ShortcutBindingConfig>? bindings = null)
    {
        Load(bindings);
    }

    public static IReadOnlyList<ShortcutDefinition> Definitions => DefinitionsInternal;

    public IReadOnlyList<ShortcutBindingConfig> ExportBindings()
    {
        return DefinitionsInternal
            .Select(definition => new ShortcutBindingConfig
            {
                ActionId = definition.ActionId,
                Gesture = _bindings.GetValueOrDefault(definition.ActionId, definition.DefaultGesture)
            })
            .ToList();
    }

    public IReadOnlyList<ShortcutBindingConfig> GetDefaultBindings()
    {
        return DefinitionsInternal
            .Select(definition => new ShortcutBindingConfig
            {
                ActionId = definition.ActionId,
                Gesture = definition.DefaultGesture
            })
            .ToList();
    }

    public string GetGesture(string actionId)
    {
        return _bindings.GetValueOrDefault(actionId, string.Empty);
    }

    public bool TryGetAction(Key key, ModifierKeys modifiers, out string actionId)
    {
        var gesture = new ShortcutGesture(key, modifiers);
        foreach (var definition in DefinitionsInternal)
        {
            var configured = GetGesture(definition.ActionId);
            if (ShortcutGesture.TryParse(configured, out var candidate) && candidate == gesture)
            {
                actionId = definition.ActionId;
                return true;
            }
        }

        actionId = string.Empty;
        return false;
    }

    public void ApplyBindings(IEnumerable<ShortcutBindingConfig> bindings)
    {
        var materialized = MaterializeBindings(bindings);
        var validation = ValidateBindings(materialized);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException("快捷键配置存在冲突或无效按键。");
        }

        _bindings.Clear();
        foreach (var binding in materialized)
        {
            _bindings[binding.ActionId] = NormalizeGesture(binding.Gesture);
        }
    }

    public static ShortcutValidationResult ValidateBindings(IEnumerable<ShortcutBindingConfig> bindings)
    {
        var configured = MaterializeBindings(bindings);
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var gestures = new Dictionary<ShortcutGesture, List<string>>();

        foreach (var binding in configured)
        {
            if (string.IsNullOrWhiteSpace(binding.Gesture))
            {
                continue;
            }

            if (!ShortcutGesture.TryParse(binding.Gesture, out var gesture))
            {
                errors[binding.ActionId] = "无法识别该快捷键";
                continue;
            }

            var basicError = ValidateGesture(gesture);
            if (basicError is not null)
            {
                errors[binding.ActionId] = basicError;
                continue;
            }

            if (ReservedGestures.TryGetValue(gesture, out var reservedBy))
            {
                errors[binding.ActionId] = $"与固定快捷键“{reservedBy}”冲突";
                continue;
            }

            if (!gestures.TryGetValue(gesture, out var actionIds))
            {
                actionIds = [];
                gestures[gesture] = actionIds;
            }

            actionIds.Add(binding.ActionId);
        }

        foreach (var duplicate in gestures.Where(pair => pair.Value.Count > 1))
        {
            var names = duplicate.Value
                .Select(GetDefinition)
                .Where(definition => definition is not null)
                .Select(definition => definition!.DisplayName)
                .ToList();
            var message = $"与“{string.Join("、", names)}”使用相同快捷键";
            foreach (var actionId in duplicate.Value)
            {
                errors[actionId] = message;
            }
        }

        return new ShortcutValidationResult(errors);
    }

    public static string? ValidateGesture(ShortcutGesture gesture)
    {
        if (gesture.Modifiers.HasFlag(ModifierKeys.Windows) ||
            gesture.Key is Key.LWin or Key.RWin)
        {
            return "不能使用 Windows 系统键";
        }

        if (gesture.Key is Key.None or Key.System or Key.Escape ||
            IsModifierKey(gesture.Key))
        {
            return gesture.Key == Key.Escape
                ? "Esc 用于关闭菜单和取消编辑"
                : "请按下完整的快捷键组合";
        }

        if (gesture.Key == Key.F4 && gesture.Modifiers == ModifierKeys.Alt)
        {
            return "Alt+F4 是 Windows 关闭窗口快捷键";
        }

        if (gesture.Modifiers == ModifierKeys.None &&
            (gesture.Key is >= Key.A and <= Key.Z ||
             gesture.Key is >= Key.D0 and <= Key.D9 ||
             gesture.Key is >= Key.NumPad0 and <= Key.NumPad9))
        {
            return "字母和数字必须搭配 Ctrl 或 Alt";
        }

        return null;
    }

    private void Load(IEnumerable<ShortcutBindingConfig>? bindings)
    {
        _bindings.Clear();
        foreach (var definition in DefinitionsInternal)
        {
            _bindings[definition.ActionId] = definition.DefaultGesture;
        }

        if (bindings is null)
        {
            return;
        }

        var supplied = bindings
            .Where(binding => GetDefinition(binding.ActionId) is not null)
            .GroupBy(binding => binding.ActionId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        foreach (var pair in supplied)
        {
            var gestureText = MigrateLegacyDefault(pair.Key, pair.Value.Gesture);
            if (string.IsNullOrWhiteSpace(gestureText))
            {
                _bindings[pair.Key] = string.Empty;
                continue;
            }

            if (ShortcutGesture.TryParse(gestureText, out var gesture) &&
                ValidateGesture(gesture) is null &&
                !ReservedGestures.ContainsKey(gesture))
            {
                _bindings[pair.Key] = gesture.ToCanonicalString();
            }
        }

        var current = ExportBindings();
        var validation = ValidateBindings(current);
        if (validation.IsValid)
        {
            return;
        }

        foreach (var actionId in validation.Errors.Keys)
        {
            if (supplied.ContainsKey(actionId) && GetDefinition(actionId) is { } definition)
            {
                _bindings[actionId] = definition.DefaultGesture;
            }
        }
    }

    private static IReadOnlyList<ShortcutBindingConfig> MaterializeBindings(
        IEnumerable<ShortcutBindingConfig> bindings)
    {
        var supplied = bindings
            .Where(binding => GetDefinition(binding.ActionId) is not null)
            .GroupBy(binding => binding.ActionId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        return DefinitionsInternal
            .Select(definition => new ShortcutBindingConfig
            {
                ActionId = definition.ActionId,
                Gesture = supplied.TryGetValue(definition.ActionId, out var binding)
                    ? binding.Gesture ?? string.Empty
                    : definition.DefaultGesture
            })
            .ToList();
    }

    private static ShortcutDefinition? GetDefinition(string? actionId)
    {
        return DefinitionsInternal.FirstOrDefault(definition =>
            string.Equals(definition.ActionId, actionId, StringComparison.OrdinalIgnoreCase));
    }

    private static string? MigrateLegacyDefault(string actionId, string? gestureText)
    {
        var legacyDefaults = actionId switch
        {
            ShortcutActionIds.RemoveCurrentPage => new[] { "Ctrl+Shift+Delete" },
            ShortcutActionIds.AddSerialWindow => new[] { "Ctrl+Shift+A", "Ctrl+Shift+P" },
            ShortcutActionIds.ToggleAllConnections => new[] { "Ctrl+Shift+L" },
            ShortcutActionIds.NewLogSession => new[] { "Ctrl+Shift+N" },
            ShortcutActionIds.BrowseLogDirectory => new[] { "Ctrl+Shift+O" },
            ShortcutActionIds.ToggleCollaboration => new[] { "Ctrl+Shift+S", "Ctrl+Shift+I" },
            ShortcutActionIds.ClearAllWindowLogs => new[] { "Ctrl+Shift+K" },
            ShortcutActionIds.ToggleAllWindowLogFollow => new[] { "Ctrl+Shift+S" },
            _ => []
        };

        if (legacyDefaults.Any(legacy =>
                string.Equals(gestureText, legacy, StringComparison.OrdinalIgnoreCase)))
        {
            return GetDefinition(actionId)?.DefaultGesture ?? gestureText;
        }

        return gestureText;
    }
    private static string NormalizeGesture(string? gestureText)
    {
        if (string.IsNullOrWhiteSpace(gestureText))
        {
            return string.Empty;
        }

        return ShortcutGesture.TryParse(gestureText, out var gesture)
            ? gesture.ToCanonicalString()
            : string.Empty;
    }

    private static bool IsModifierKey(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl or
            Key.LeftShift or Key.RightShift or
            Key.LeftAlt or Key.RightAlt or
            Key.LWin or Key.RWin;
    }
}
