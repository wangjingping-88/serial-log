using SerialLog.Core.Configuration;

namespace SerialLog.Tests;

public class WorkspaceShortcutConfigTests
{
    [Fact]
    public void Workspace_config_round_trips_shortcut_bindings()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "workspace-shortcuts-" + Guid.NewGuid().ToString("N") + ".json");
        var config = new WorkspaceConfig
        {
            ShortcutBindings =
            [
                new ShortcutBindingConfig
                {
                    ActionId = "page.add",
                    Gesture = "Ctrl+Alt+P"
                },
                new ShortcutBindingConfig
                {
                    ActionId = "help.openDocumentation",
                    Gesture = string.Empty
                }
            ]
        };

        try
        {
            WorkspaceConfigStore.Save(path, config);

            var loaded = WorkspaceConfigStore.Load(path);

            Assert.Equal(2, loaded.ShortcutBindings.Count);
            Assert.Equal("Ctrl+Alt+P", loaded.ShortcutBindings[0].Gesture);
            Assert.Equal(string.Empty, loaded.ShortcutBindings[1].Gesture);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Old_workspace_without_shortcuts_loads_empty_bindings()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "workspace-old-" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            File.WriteAllText(path, """{"LogRootDirectory":"D:\\serial-log-data\\logs"}""");

            var loaded = WorkspaceConfigStore.Load(path);

            Assert.Empty(loaded.ShortcutBindings);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
