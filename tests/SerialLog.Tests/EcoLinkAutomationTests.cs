using SerialLog.Core.EcoLink;

namespace SerialLog.Tests;

public sealed class EcoLinkAutomationTests
{
    [Fact]
    public void Analyzer_accepts_complete_three_phase_test()
    {
        var logs = BuildLogs(
            "nb_test: 5 nodes registered, start phase1-3 rounds100 attempts3",
            "nb_test: phase1 passed 100 rounds",
            "nb_test: phase2 passed 100 rounds",
            "nb_test: phase3 passed 100 rounds",
            "nb_test: all phases passed",
            "nb_test: sync tx submit=321 ok=321 fail=0");

        var result = EcoLinkTestAnalyzer.Analyze(logs, new EcoLinkTestCriteria());

        Assert.True(result.Success);
        Assert.True(result.IsTerminal);
        Assert.Equal("done", result.Stage);
        Assert.Equal(5, result.RegisteredNodeCount);
        Assert.Equal(100, result.PhaseRounds[1]);
        Assert.Equal(100, result.PhaseRounds[2]);
        Assert.Equal(100, result.PhaseRounds[3]);
        Assert.Equal(321, result.SyncSubmitCount);
        Assert.Equal(0, result.SyncFailedCount);
    }

    [Fact]
    public void Analyzer_stops_on_explicit_phase_failure()
    {
        var logs = BuildLogs(
            "nb_test: FAIL phase2 round7 bitmap0x1b missing0x04");

        var result = EcoLinkTestAnalyzer.Analyze(logs, new EcoLinkTestCriteria());

        Assert.False(result.Success);
        Assert.True(result.IsTerminal);
        Assert.Equal("phase2", result.Stage);
        Assert.Contains("round7", result.Evidence.Single());
    }

    [Fact]
    public void Analyzer_keeps_partial_test_pending_then_marks_timeout()
    {
        var logs = BuildLogs("nb_test: phase1 passed 100 rounds");
        var criteria = new EcoLinkTestCriteria();

        var pending = EcoLinkTestAnalyzer.Analyze(logs, criteria);
        var timeout = EcoLinkTestAnalyzer.Timeout(logs, criteria, 30);

        Assert.False(pending.IsTerminal);
        Assert.Equal("phase2", pending.Stage);
        Assert.True(timeout.IsTerminal);
        Assert.Equal("timeout", timeout.Stage);
        Assert.Contains("30", timeout.Reason);
    }

    [Fact]
    public void Analyzer_stops_on_node_abnormal_pattern()
    {
        var logs = BuildLogs("nb_test: phase1 passed 100 rounds");
        logs["node3"] = ["Hard fault on thread nodefrm"];

        var result = EcoLinkTestAnalyzer.Analyze(logs, new EcoLinkTestCriteria());

        Assert.True(result.IsTerminal);
        Assert.Equal("anomaly", result.Stage);
        Assert.Contains("node3", result.Evidence.Single());
    }

    [Fact]
    public void Build_version_generator_writes_shared_header_and_version_file()
    {
        var root = Path.Combine(Path.GetTempPath(), $"serial-log-{Guid.NewGuid():N}");
        var header = Path.Combine(root, "tools", "ecolink_auto_build_version.h");
        var versionFile = Path.Combine(root, "result", "build-version.txt");
        try
        {
            var generator = new EcoLinkBuildVersionGenerator(
                "eco",
                header,
                () => new DateTimeOffset(2026, 7, 14, 12, 34, 56, TimeSpan.FromHours(8)));

            var version = generator.Generate(3, versionFile);

            Assert.Equal("eco-20260714-123456-i003", version);
            Assert.Contains(
                "#define ECOLINK_AUTO_BUILD_VERSION \"eco-20260714-123456-i003\"",
                File.ReadAllText(header));
            Assert.Equal(version, File.ReadAllText(versionFile).Trim());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static Dictionary<string, IReadOnlyList<string>> BuildLogs(
        params string[] extenderLines)
    {
        return new Dictionary<string, IReadOnlyList<string>>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["ex_async"] = extenderLines,
            ["node1"] = ["wiota_app_init"],
            ["node2"] = ["wiota_app_init"],
            ["node3"] = ["wiota_app_init"],
            ["node4"] = ["wiota_app_init"],
            ["node5"] = ["wiota_app_init"]
        };
    }
}
