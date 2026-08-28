using SerialLog.App.Shortcuts;
using SerialLog.Core.Configuration;

namespace SerialLog.Tests;

public sealed class ShortcutMenuEntryBuilderTests
{
    [Fact]
    public void Entries_follow_definitions_and_current_bindings()
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

        var entries = ShortcutMenuEntryBuilder.Build(manager);

        Assert.Equal(ShortcutManager.Definitions.Count, entries.Count);
        Assert.Equal(
            ShortcutManager.Definitions.Select(definition => definition.ActionId),
            entries.Select(entry => entry.ActionId));
        Assert.Equal(
            ShortcutManager.Definitions.Select(definition => definition.DisplayName),
            entries.Select(entry => entry.DisplayName));
        Assert.Equal(
            "Ctrl+Alt+P",
            entries.Single(entry => entry.ActionId == ShortcutActionIds.AddPage).GestureText);
        Assert.Equal(
            "未设置",
            entries.Single(entry => entry.ActionId == ShortcutActionIds.OpenDocumentation).GestureText);
    }
}
