using System.Windows.Input;
using SerialLog.App.Shortcuts;
using SerialLog.Core.Configuration;

namespace SerialLog.Tests;

public class ShortcutManagerTests
{
    [Fact]
    public void Default_shortcuts_are_valid_and_unique()
    {
        var manager = new ShortcutManager();

        var validation = ShortcutManager.ValidateBindings(manager.ExportBindings());

        Assert.True(validation.IsValid);
        Assert.Equal(
            ShortcutManager.Definitions.Count,
            manager.ExportBindings().Select(binding => binding.Gesture).Distinct().Count());
    }

    [Fact]
    public void Log_clear_shortcuts_have_distinct_defaults()
    {
        var manager = new ShortcutManager();

        Assert.Equal("Ctrl+K", manager.GetGesture(ShortcutActionIds.ClearActiveWindowLog));
        Assert.Equal("Alt+K", manager.GetGesture(ShortcutActionIds.ClearAllWindowLogs));
    }


    [Fact]
    public void Window_and_follow_shortcuts_have_expected_defaults()
    {
        var manager = new ShortcutManager();

        Assert.Equal("Alt+P", manager.GetGesture(ShortcutActionIds.AddSerialWindow));
        Assert.Equal("Ctrl+L", manager.GetGesture(ShortcutActionIds.ToggleActiveWindowConnection));
        Assert.Equal("Ctrl+S", manager.GetGesture(ShortcutActionIds.ToggleActiveWindowLogFollow));
        Assert.Equal("Alt+S", manager.GetGesture(ShortcutActionIds.ToggleAllWindowLogFollow));
        Assert.Equal("Alt+I", manager.GetGesture(ShortcutActionIds.ToggleCollaboration));
    }

    [Theory]
    [InlineData(ShortcutActionIds.RemoveCurrentPage, "Ctrl+Shift+Delete", "Alt+Delete")]
    [InlineData(ShortcutActionIds.AddSerialWindow, "Ctrl+Shift+A", "Alt+P")]
    [InlineData(ShortcutActionIds.AddSerialWindow, "Ctrl+Shift+P", "Alt+P")]
    [InlineData(ShortcutActionIds.ToggleAllConnections, "Ctrl+Shift+L", "Alt+L")]
    [InlineData(ShortcutActionIds.NewLogSession, "Ctrl+Shift+N", "Alt+N")]
    [InlineData(ShortcutActionIds.BrowseLogDirectory, "Ctrl+Shift+O", "Alt+O")]
    [InlineData(ShortcutActionIds.ToggleCollaboration, "Ctrl+Shift+S", "Alt+I")]
    [InlineData(ShortcutActionIds.ToggleCollaboration, "Ctrl+Shift+I", "Alt+I")]
    [InlineData(ShortcutActionIds.ClearAllWindowLogs, "Ctrl+Shift+K", "Alt+K")]
    [InlineData(ShortcutActionIds.ToggleAllWindowLogFollow, "Ctrl+Shift+S", "Alt+S")]
    public void Legacy_defaults_are_migrated_to_current_defaults(
        string actionId,
        string legacyGesture,
        string expectedGesture)
    {
        var manager = new ShortcutManager(
        [
            new ShortcutBindingConfig
            {
                ActionId = actionId,
                Gesture = legacyGesture
            }
        ]);

        Assert.Equal(expectedGesture, manager.GetGesture(actionId));
    }

    [Fact]
    public void Custom_and_disabled_shortcuts_are_applied()
    {
        var manager = new ShortcutManager();
        var bindings = manager.ExportBindings()
            .Select(binding => new ShortcutBindingConfig
            {
                ActionId = binding.ActionId,
                Gesture = binding.ActionId switch
                {
                    ShortcutActionIds.AddPage => "Ctrl+Alt+P",
                    ShortcutActionIds.OpenDocumentation => string.Empty,
                    _ => binding.Gesture
                }
            })
            .ToList();

        manager.ApplyBindings(bindings);

        Assert.Equal("Ctrl+Alt+P", manager.GetGesture(ShortcutActionIds.AddPage));
        Assert.Equal(string.Empty, manager.GetGesture(ShortcutActionIds.OpenDocumentation));
        Assert.True(manager.TryGetAction(
            Key.P,
            ModifierKeys.Control | ModifierKeys.Alt,
            out var actionId));
        Assert.Equal(ShortcutActionIds.AddPage, actionId);
    }

    [Fact]
    public void Duplicate_shortcuts_are_reported_for_both_actions()
    {
        var manager = new ShortcutManager();
        var bindings = manager.ExportBindings()
            .Select(binding => new ShortcutBindingConfig
            {
                ActionId = binding.ActionId,
                Gesture = binding.ActionId is ShortcutActionIds.AddPage or ShortcutActionIds.NextPage
                    ? "Ctrl+Alt+P"
                    : binding.Gesture
            })
            .ToList();

        var validation = ShortcutManager.ValidateBindings(bindings);

        Assert.False(validation.IsValid);
        Assert.Contains(ShortcutActionIds.AddPage, validation.Errors.Keys);
        Assert.Contains(ShortcutActionIds.NextPage, validation.Errors.Keys);
    }

    [Theory]
    [InlineData("Ctrl+C")]
    [InlineData("Enter")]
    [InlineData("Delete")]
    [InlineData("Escape")]
    [InlineData("Alt+F4")]
    [InlineData("A")]
    public void Reserved_or_invalid_shortcuts_are_rejected(string gesture)
    {
        var manager = new ShortcutManager();
        var bindings = manager.ExportBindings()
            .Select(binding => new ShortcutBindingConfig
            {
                ActionId = binding.ActionId,
                Gesture = binding.ActionId == ShortcutActionIds.AddPage
                    ? gesture
                    : binding.Gesture
            })
            .ToList();

        var validation = ShortcutManager.ValidateBindings(bindings);

        Assert.False(validation.IsValid);
        Assert.Contains(ShortcutActionIds.AddPage, validation.Errors.Keys);
    }

    [Fact]
    public void Invalid_saved_shortcut_falls_back_to_default()
    {
        var manager = new ShortcutManager(
        [
            new ShortcutBindingConfig
            {
                ActionId = ShortcutActionIds.AddPage,
                Gesture = "Ctrl+C"
            },
            new ShortcutBindingConfig
            {
                ActionId = "unknown.action",
                Gesture = "Ctrl+Alt+U"
            }
        ]);

        Assert.Equal("Ctrl+N", manager.GetGesture(ShortcutActionIds.AddPage));
        Assert.DoesNotContain(
            manager.ExportBindings(),
            binding => binding.ActionId == "unknown.action");
    }
}
