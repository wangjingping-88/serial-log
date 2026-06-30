using SerialLog.Core.Commands;

namespace SerialLog.Tests;

public class CommandTests
{
    [Theory]
    [InlineData(LineEnding.None, "AT")]
    [InlineData(LineEnding.Cr, "AT\r")]
    [InlineData(LineEnding.Lf, "AT\n")]
    [InlineData(LineEnding.CrLf, "AT\r\n")]
    public void Command_formatter_appends_selected_line_ending(LineEnding ending, string expected)
    {
        Assert.Equal(expected, CommandFormatter.ApplyLineEnding("AT", ending));
    }

    [Fact]
    public void At_importer_reads_one_command_per_line_and_ignores_comments()
    {
        var file = Path.Combine(Path.GetTempPath(), "at-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(file, "\r\n# comment\r\nAT\r\n  AT+GMR  \r\n");

        try
        {
            var commands = AtCommandImporter.Import(file);

            Assert.Equal(["AT", "AT+GMR"], commands);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void At_importer_reads_literal_crlf_separated_at_list()
    {
        var file = Path.Combine(Path.GetTempPath(), "at-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(file, @"AT\r\nAT+LOG=<>\r\n# comment\r\nAT+GMR\r\n");

        try
        {
            var commands = AtCommandImporter.Import(file);

            Assert.Equal(["AT", "AT+LOG=<>", "AT+GMR"], commands);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void At_importer_reads_at_cmd_export_from_c_file_and_ignores_commented_exports()
    {
        var file = Path.Combine(Path.GetTempPath(), "at-" + Guid.NewGuid().ToString("N") + ".c");
        File.WriteAllText(file, """
        AT_CMD_EXPORT("AT+ROLE", "=<role[0-2]>", mesh_at_role_test, mesh_at_role_query, mesh_at_role_setup, NULL);
        // AT_CMD_EXPORT("AT+DISABLED", "=<value>", NULL, NULL, NULL, NULL);
        AT_CMD_EXPORT("AT+TDMABIZCFG",
                      "<a><b>[<c>]",
                      NULL,
                      mesh_at_tdma_biz_cfg_query,
                      mesh_at_tdma_biz_cfg_setup,
                      NULL);
        AT_CMD_EXPORT("AT+NB", NULL, NULL, mesh_at_neighbor_query, NULL, NULL);
        """);

        try
        {
            var commands = AtCommandImporter.Import(file);

            Assert.Equal(["AT+ROLE=<role[0-2]>", "AT+TDMABIZCFG<a><b>[<c>]", "AT+NB"], commands);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void At_importer_extracts_commands_from_at_list_output_text()
    {
        const string text = """
        [2026-06-30 15:02:01.001] AT&L
        [2026-06-30 15:02:01.010] AT+ROLE=<role[0-2]>
        [2026-06-30 15:02:01.011] AT+SEND=<addr>,<len>,<data>
        [2026-06-30 15:02:01.012] AT+NB
        [2026-06-30 15:02:01.013] OK
        [2026-06-30 15:02:01.014] AT+NB
        """;

        var commands = AtCommandImporter.ImportFromText(text);

        Assert.Equal(["AT&L", "AT+ROLE=<role[0-2]>", "AT+SEND=<addr>,<len>,<data>", "AT+NB"], commands);
    }

    [Fact]
    public void At_importer_appends_new_commands_without_replacing_existing_commands()
    {
        var merged = AtCommandImporter.AppendDistinct(
            ["AT+SN", "AT+FREQ=<freq>"],
            ["AT+FREQ=<freq>", "AT+ROLE=<role>", "at+sn"]);

        Assert.Equal(["AT+SN", "AT+FREQ=<freq>", "AT+ROLE=<role>"], merged);
    }

    [Fact]
    public void At_importer_asks_resolver_when_same_command_has_different_arguments()
    {
        var conflicts = new List<AtCommandConflict>();

        var merged = AtCommandImporter.AppendDistinct(
            ["AT+ROLE=<role>", "AT+TDMABIZCFG<a><b>"],
            ["AT+ROLE=<role[0-2]>", "AT+TDMABIZCFG<a><b>[<c>]"],
            conflict =>
            {
                conflicts.Add(conflict);
                return conflict.CommandName == "AT+ROLE"
                    ? AtCommandConflictChoice.UseNew
                    : AtCommandConflictChoice.KeepExisting;
            });

        Assert.Equal(["AT+ROLE=<role[0-2]>", "AT+TDMABIZCFG<a><b>"], merged);
        Assert.Collection(conflicts,
            conflict =>
            {
                Assert.Equal("AT+ROLE", conflict.CommandName);
                Assert.Equal("AT+ROLE=<role>", conflict.ExistingCommand);
                Assert.Equal("AT+ROLE=<role[0-2]>", conflict.NewCommand);
            },
            conflict => Assert.Equal("AT+TDMABIZCFG", conflict.CommandName));
    }

    [Fact]
    public async Task Command_group_sends_commands_to_connected_targets_in_order_and_skips_disconnected_targets()
    {
        var connected = new FakeTarget("port-1", true);
        var disconnected = new FakeTarget("port-2", false);
        var group = new CommandGroup(
            "启动检查",
            ["port-1", "port-2"],
            ["AT", "AT+GMR"],
            TimeSpan.Zero,
            LineEnding.CrLf);

        var result = await CommandGroupExecutor.ExecuteAsync(group, [connected, disconnected], CancellationToken.None);

        Assert.Equal(["AT\r\n", "AT+GMR\r\n"], connected.Payloads);
        Assert.Empty(disconnected.Payloads);
        Assert.Equal(2, result.SentCount);
        Assert.Equal(2, result.SkippedCount);
        Assert.Contains(result.Steps, step => step.TargetId == "port-2" && step.Status == CommandSendStatus.SkippedDisconnected);
    }

    private sealed class FakeTarget(string id, bool isConnected) : ICommandTarget
    {
        public string Id { get; } = id;

        public bool IsConnected { get; } = isConnected;

        public List<string> Payloads { get; } = [];

        public Task SendAsync(string payload, CancellationToken cancellationToken)
        {
            Payloads.Add(payload);
            return Task.CompletedTask;
        }
    }
}
