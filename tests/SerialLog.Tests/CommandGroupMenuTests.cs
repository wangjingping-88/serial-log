using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml.Linq;
using SerialLog.App.ViewModels;
using SerialLog.Core.Configuration;

namespace SerialLog.Tests;

public class CommandGroupMenuTests
{
    [Fact]
    public void Group_context_menu_resolves_workspace_commands_and_row_index_and_disables_empty_selection()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
                var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
                var app = XElement.Load(Path.Combine(repo, "src/SerialLog.App/App.xaml"));
                var panel = XElement.Load(Path.Combine(repo, "src/SerialLog.App/Views/CommandGroupEditorView.xaml"));
                var listXml = new XElement(panel.Descendants(ui + "ListBox").Single(e => (string?)e.Attribute(x + "Name") == "CommandGroupCommandsListBox"));
                foreach (var attribute in listXml.Attributes().Where(a => a.Name.LocalName.StartsWith("Preview") || a.Name.LocalName is "Drop" or "MouseDoubleClick").ToArray()) attribute.Remove();
                foreach (var setter in listXml.Descendants(ui + "EventSetter").ToArray()) setter.Remove();
                var root = new XElement(ui + "UserControl", new XAttribute(XNamespace.Xmlns + "x", x),
                    new XAttribute(XNamespace.Xmlns + "sys", "clr-namespace:System;assembly=mscorlib"),
                    new XAttribute(XNamespace.Xmlns + "local", "clr-namespace:SerialLog.App;assembly=SerialLog.App"),
                    new XElement(ui + "UserControl.Resources", app.Element(ui + "Application.Resources")!.Elements()), listXml);
                var view = (UserControl)XamlReader.Parse(root.ToString());
                using var vm = new MainViewModel(Path.Combine(repo, "artifacts/not-saved-group-menu.json"), false,
                    configuration: new WorkspaceConfig { CommandGroups = [new() { Commands = ["same", "middle", "same"] }] });
                view.DataContext = vm; view.Measure(new Size(480, 160)); view.Arrange(new Rect(0, 0, 480, 160)); view.UpdateLayout();
                var list = (ListBox)view.Content; list.SelectedIndex = 2;
                Assert.Same(vm, list.Tag);
                var menu = list.ContextMenu; menu.PlacementTarget = list;
                menu.ApplyTemplate(); menu.Measure(new Size(220, 160)); menu.Arrange(new Rect(0, 0, 220, 160)); menu.UpdateLayout();
                view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var items = menu.Items.OfType<MenuItem>().ToArray(); Assert.Equal(3, items.Length);
                Assert.Same(vm.EditCommandInGroupCommand, items[0].Command); Assert.Same(vm.CopyCommandFromGroupCommand, items[1].Command); Assert.Same(vm.RemoveCommandFromGroupCommand, items[2].Command);
                Assert.All(items, item => { Assert.Equal(2, item.CommandParameter); Assert.True(item.IsEnabled); });
                list.SelectedIndex = -1;
                view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.All(items, item => { Assert.Equal(-1, item.CommandParameter); Assert.False(item.IsEnabled); });
            }
            catch (Exception e) { failure = e; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "命令组右键菜单绑定检查超时");
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
