using System.Text;

namespace SerialLog.Tests;

public class LayoutXamlTests
{
    [Fact]
    public void Imported_command_set_editor_keeps_delete_button_fully_visible()
    {
        var xaml = ReadAppXaml(Path.Combine("Views", "CommandPanelView.xaml"));

        Assert.Contains("<ColumnDefinition Width=\"*\" />", xaml);
        Assert.Contains("<ColumnDefinition Width=\"68\" />", xaml);
        Assert.Contains("Content=\"删除\"", xaml);
        Assert.Contains("MinWidth=\"56\"", xaml);
    }

    [Fact]
    public void Imported_command_set_picker_binds_item_text_to_live_name()
    {
        var xaml = ReadAppXaml(Path.Combine("Views", "CommandPanelView.xaml"));

        Assert.DoesNotContain("DisplayMemberPath=\"Name\"", xaml);
        Assert.Contains("Text=\"{Binding Name}\"", xaml);
    }

    [Fact]
    public void Target_window_picker_uses_fixed_cells_for_column_alignment()
    {
        var xaml = ReadAppXaml(Path.Combine("Views", "CommandPanelView.xaml"));

        Assert.Equal(2, CountOccurrences(xaml, "<WrapPanel ItemWidth=\"92\" ItemHeight=\"28\" />"));
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", xaml);
        Assert.Contains("ToolTip=\"{Binding Title}\"", xaml);
    }

    private static string ReadAppXaml(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "SerialLog.App",
            relativePath));

        return File.ReadAllText(path, Encoding.UTF8);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var startIndex = 0;

        while (true)
        {
            var index = text.IndexOf(value, startIndex, StringComparison.Ordinal);
            if (index < 0)
            {
                return count;
            }

            count++;
            startIndex = index + value.Length;
        }
    }
}
