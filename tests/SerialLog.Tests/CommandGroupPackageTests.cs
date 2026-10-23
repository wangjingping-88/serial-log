using System.Text.Json.Nodes;
using SerialLog.Core.Commands;
using SerialLog.Core.Configuration;

namespace SerialLog.Tests;

public class CommandGroupPackageTests
{
    private static string Sample() => CommandGroupPackageCodec.Export(
        [new() { Id = "group", Name = "中文发送", ListIds = ["a", "b"], TargetIds = ["private-port"],
            Commands = ["AT+SEND=10\\r\\n中文数据", "第二条"], DelayMilliseconds = 17, LoopIntervalMilliseconds = 35,
            LoopCount = 3, IsInfiniteLoop = false, InterpretEscapes = true, LineEnding = LineEnding.Lf }],
        [new() { Id = "a", Name = "AP" }, new() { Id = "b", Name = "共用" }]);

    [Fact]
    public void Roundtrip_keeps_commands_order_parameters_and_many_list_membership_but_no_targets()
    {
        var json = Sample(); Assert.DoesNotContain("private-port", json);
        var package = CommandGroupPackageCodec.Parse(json); var group = Assert.Single(package.Groups);
        Assert.Equal(new[] { "AP", "共用" }, package.Lists.Select(l => l.Name));
        Assert.Equal(new[] { "a", "b" }, group.ListIds);
        Assert.Equal(new[] { "AT+SEND=10\\r\\n中文数据", "第二条" }, group.Commands);
        Assert.Empty(group.TargetIds); Assert.Equal(17, group.DelayMilliseconds); Assert.Equal(35, group.LoopIntervalMilliseconds);
        Assert.Equal(3, group.LoopCount); Assert.True(group.InterpretEscapes); Assert.Equal(LineEnding.Lf, group.LineEnding);
    }

    [Fact]
    public void Import_is_append_only_with_fresh_ids_and_collision_names_and_never_reuses_remote_targets()
    {
        var first = CommandGroupPackageCodec.PrepareImport(Sample(), ["中文发送"], ["AP", "共用"]);
        var second = CommandGroupPackageCodec.PrepareImport(Sample(), [], []);
        Assert.Equal("中文发送 (2)", first.Groups[0].Name); Assert.Equal("AP (2)", first.Lists[0].Name);
        Assert.NotEqual(first.Groups[0].Id, second.Groups[0].Id);
        Assert.Equal(first.Lists.Select(l => l.Id), first.Groups[0].ListIds);
        Assert.All(first.Lists, l => Assert.True(Guid.TryParseExact(l.Id, "N", out _)));
        Assert.Empty(first.Groups[0].TargetIds);
        var hostileTarget = JsonNode.Parse(Sample())!; hostileTarget["groups"]![0]!["targetIds"] = new JsonArray("local-id");
        Assert.Empty(CommandGroupPackageCodec.Parse(hostileTarget.ToJsonString()).Groups[0].TargetIds);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("reference")]
    [InlineData("delay")]
    [InlineData("null-command")]
    [InlineData("duplicate")]
    public void Malformed_packages_are_rejected_before_import(string fault)
    {
        var root = JsonNode.Parse(Sample())!;
        switch (fault)
        {
            case "version": root["version"] = 9; break;
            case "reference": root["groups"]![0]!["listIds"] = new JsonArray("missing"); break;
            case "delay": root["groups"]![0]!["delayMilliseconds"] = -1; break;
            case "null-command": root["groups"]![0]!["commands"] = new JsonArray((JsonNode?)null); break;
            case "duplicate": root["lists"]![1]!["id"] = "a"; break;
        }
        Assert.Throws<InvalidDataException>(() => CommandGroupPackageCodec.Parse(root.ToJsonString()));
    }

    [Fact]
    public void Wrong_document_and_oversize_are_rejected_and_export_does_not_modify_original()
    {
        Assert.Throws<InvalidDataException>(() => CommandGroupPackageCodec.Parse("{}"));
        Assert.Throws<InvalidDataException>(() => CommandGroupPackageCodec.Parse(new string('x', CommandGroupPackageCodec.MaxBytes + 1)));
        var group = new CommandGroupConfig { TargetIds = ["local"] };
        CommandGroupPackageCodec.Export([group], []);
        Assert.Equal(new[] { "local" }, group.TargetIds);
    }

    [Fact]
    public void Workspace_import_preserves_existing_groups_and_has_no_selected_targets_or_running_tasks()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var vm = new SerialLog.App.ViewModels.MainViewModel(Path.Combine(Path.GetTempPath(), "not-saved-command-package.json"), false,
                    configuration: new WorkspaceConfig
                    {
                        SerialWindows = [new() { Id = "local", Title = "本机串口", PortName = "COM1" }],
                        CommandGroups = [new() { Name = "原配置", Commands = ["KEEP"], TargetIds = ["local"] }]
                    });
                var panel = vm.CommandPanel; var existing = panel.CommandGroups[0];
                Assert.Throws<InvalidDataException>(() => panel.ImportCommandPackage("{}"));
                Assert.Single(panel.CommandGroups); Assert.Empty(panel.CommandGroupLists);
                panel.ImportCommandPackage(Sample());
                Assert.Same(existing, panel.CommandGroups[0]); Assert.Equal("KEEP", existing.Commands[0]);
                var imported = panel.CommandGroups[1]; Assert.All(imported.Targets, target => Assert.False(target.IsSelected));
                Assert.False(imported.IsRunning); Assert.False(imported.IsSelectedForRun);
                Assert.Equal(2, panel.CommandGroupLists.Count);
                Assert.All(imported.ListIds, id => Assert.Contains(panel.CommandGroupLists, list => list.Id == id));
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
