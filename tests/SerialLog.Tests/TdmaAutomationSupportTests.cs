using SerialLog.Cli;
using System.Reflection;

namespace SerialLog.Tests;

public class TdmaAutomationSupportTests
{
    [Fact]
    public void Build_version_generator_writes_header_and_version_text()
    {
        var root = Path.Combine(Path.GetTempPath(), "serial-log-version-" + Guid.NewGuid().ToString("N"));
        var headerPath = Path.Combine(root, "tdma_auto_build_version.h");
        var versionPath = Path.Combine(root, "version.txt");
        var generator = new TdmaBuildVersionGenerator(
            "tdma",
            headerPath,
            versionPath,
            () => new DateTimeOffset(2026, 6, 30, 11, 40, 1, TimeSpan.FromHours(8)));

        try
        {
            var version = generator.Generate(3);

            Assert.Equal("tdma-20260630-114001-i003", version);
            Assert.Contains("#define TDMA_AUTO_BUILD_VERSION \"tdma-20260630-114001-i003\"", File.ReadAllText(headerPath));
            Assert.Equal("tdma-20260630-114001-i003", File.ReadAllText(versionPath).Trim());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Version_confirmation_requires_every_node_to_report_expected_version()
    {
        var responses = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["center"] = ["+BUILD:tdma-20260630-114001-i001", "OK"],
            ["R1"] = ["boot", "+BUILD:tdma-20260630-114001-i001"],
            ["R2"] = ["+BUILD:old"],
            ["R3"] = ["+BUILD:tdma-20260630-114001-i001"],
            ["R4"] = ["+BUILD:tdma-20260630-114001-i001"]
        };

        var result = TdmaVersionConfirmAnalyzer.Analyze(
            ["center", "R1", "R2", "R3", "R4"],
            responses,
            "+BUILD:",
            "tdma-20260630-114001-i001");

        Assert.False(result.Success);
        Assert.Equal("version_confirm", result.Stage);
        Assert.Contains("R2", result.Evidence[0]);
    }

    [Fact]
    public void Abnormal_pattern_matcher_finds_configured_field()
    {
        var matcher = new TdmaAbnormalPatternMatcher(["LOST", "WATCHDOG"]);

        Assert.True(matcher.TryMatch("R4", "[11:00] TDMA_SYNC_LOST,0x03d5", out var evidence));
        Assert.Contains("R4", evidence);
        Assert.Contains("TDMA_SYNC_LOST", evidence);
    }

    [Fact]
    public void Loop_summary_reports_iteration_window_and_failures()
    {
        var records = new[]
        {
            new TdmaLoopIterationRecord(1, @"D:\run\iter001", new()
            {
                Success = false,
                Stage = "anomaly",
                Reason = "Abnormal log pattern matched.",
                HasSyncLost = true,
                Evidence = ["R3: TDMA_SYNC_LOST"]
            }),
            new TdmaLoopIterationRecord(2, @"D:\run\iter002", new()
            {
                Success = false,
                Stage = "ack_rx",
                Reason = "ACK was transmitted but center did not receive ACK."
            }),
            new TdmaLoopIterationRecord(3, @"D:\run\iter003", new()
            {
                Success = false,
                Stage = "data_local",
                Reason = "DATA TX was found, but target DATA_LOCAL was not found."
            })
        };

        var summary = TdmaLoopSummary.Build(records, 50, isFinal: false);

        Assert.Contains("已迭代: 3/50", summary);
        Assert.Contains("成功: 0", summary);
        Assert.Contains("anomaly: 1", summary);
        Assert.Contains("ack_rx: 1", summary);
        Assert.Contains("最近失败: data_local", summary);
        Assert.Contains("TDMA_SYNC_LOST", summary);
    }

    [Fact]
    public void Flash_preflight_uses_configured_baud_before_fallback_baud()
    {
        var method = typeof(TdmaLoopRunner).GetMethod(
            "BuildAtProbeBaudRates",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var defaultOrder = (int[])method.Invoke(null, [115200])!;
        var fallbackFirstOrder = (int[])method.Invoke(null, [460800])!;

        Assert.Equal([115200, 460800], defaultOrder);
        Assert.Equal([460800, 115200], fallbackFirstOrder);
    }

    [Fact]
    public async Task Tdma_loop_refuses_to_start_when_another_loop_is_running()
    {
        using var lockReady = new ManualResetEventSlim(false);
        using var releaseLock = new ManualResetEventSlim(false);
        Exception? threadError = null;
        var thread = new Thread(() =>
        {
            using var semaphore = new Semaphore(1, 1, "SerialLog.TdmaLoopRunner.SingleInstance");
            if (!semaphore.WaitOne(0))
            {
                threadError = new InvalidOperationException("Test could not acquire tdma-loop semaphore.");
                lockReady.Set();
                return;
            }

            lockReady.Set();
            releaseLock.Wait();
            semaphore.Release();
        });

        thread.Start();
        Assert.True(lockReady.Wait(TimeSpan.FromSeconds(5)));
        if (threadError is not null)
        {
            throw threadError;
        }

        try
        {
            var runner = new TdmaLoopRunner(new TdmaLoopRunConfig());

            var code = await runner.RunAsync(CancellationToken.None);

            Assert.Equal(2, code);
        }
        finally
        {
            releaseLock.Set();
            thread.Join();
        }
    }
}
