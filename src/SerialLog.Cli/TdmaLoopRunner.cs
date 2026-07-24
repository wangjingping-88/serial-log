using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Ports;
using System.Text.Json;
using SerialLog.Core.Logging;
using SerialLog.Core.Serial;
using SerialLog.Core.Tdma;

namespace SerialLog.Cli;

public sealed class TdmaLoopRunner
{
    private const string SingleInstanceSemaphoreName = "SerialLog.TdmaLoopRunner.SingleInstance";
    private const int AtProbeTimeoutMilliseconds = 1200;

    private readonly TdmaLoopRunConfig _config;

    public TdmaLoopRunner(TdmaLoopRunConfig config)
    {
        _config = config;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        using var semaphore = new Semaphore(1, 1, SingleInstanceSemaphoreName);
        if (!semaphore.WaitOne(0))
        {
            Console.Error.WriteLine(
                "[RUN_LOCK] Another tdma-loop process is already running.");
            return 2;
        }

        try
        {
            return await RunCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<int> RunCoreAsync(CancellationToken cancellationToken)
    {
        ValidateConfig();
        Directory.CreateDirectory(_config.WorkRoot);

        var lastExitCode = 2;
        var records = new List<TdmaLoopIterationRecord>();
        for (var iteration = 1; iteration <= Math.Max(1, _config.MaxIterations); iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var iterationDirectory = Path.Combine(
                _config.WorkRoot,
                $"{DateTime.Now:yyyyMMdd_HHmmss}_iter_{iteration:000}");
            Directory.CreateDirectory(iterationDirectory);

            Console.WriteLine($"[ITER] {iteration} directory={iterationDirectory}");
            var result = await RunOneIterationAsync(iteration, iterationDirectory, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(Path.Combine(iterationDirectory, "result.json"), result, cancellationToken).ConfigureAwait(false);
            records.Add(new TdmaLoopIterationRecord(iteration, iterationDirectory, result));

            Console.WriteLine($"[RESULT] success={result.Success} stage={result.Stage} reason={result.Reason}");
            if (result.Success)
            {
                await WriteSummaryAsync(records, iteration, isFinal: true, cancellationToken).ConfigureAwait(false);
                return 0;
            }

            if (ShouldWriteSummary(iteration))
            {
                await WriteSummaryAsync(records, iteration, isFinal: false, cancellationToken).ConfigureAwait(false);
            }

            lastExitCode = 2;
        }

        await WriteSummaryAsync(records, records.Count, isFinal: true, cancellationToken).ConfigureAwait(false);
        return lastExitCode;
    }

    private bool ShouldWriteSummary(int iteration)
    {
        return _config.SummaryInterval > 0 && iteration % _config.SummaryInterval == 0;
    }

    private async Task WriteSummaryAsync(
        IReadOnlyList<TdmaLoopIterationRecord> records,
        int iteration,
        bool isFinal,
        CancellationToken cancellationToken)
    {
        var summary = TdmaLoopSummary.Build(records, Math.Max(1, _config.MaxIterations), isFinal);
        var fileName = isFinal ? "summary_final.md" : $"summary_iter_{iteration:000}.md";
        var path = Path.Combine(_config.WorkRoot, fileName);
        await File.WriteAllTextAsync(path, summary, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"[SUMMARY] {path}");
        Console.WriteLine(summary);
    }

    private async Task<TdmaLoopAnalysisResult> RunOneIterationAsync(
        int iteration,
        string iterationDirectory,
        CancellationToken cancellationToken)
    {
        if (_config.FlashFirmware)
        {
            var preflightResult = await CheckFlashPortsReadyAsync(
                GetFlashNodes(),
                iterationDirectory,
                cancellationToken).ConfigureAwait(false);
            if (!preflightResult.Success)
            {
                return new TdmaLoopAnalysisResult
                {
                    Success = false,
                    Stage = "flash",
                    Reason = preflightResult.Reason,
                    Evidence = preflightResult.Evidence
                };
            }
        }

        var expectedVersion = GenerateBuildVersion(iteration, iterationDirectory);
        if (_config.BuildFirmware)
        {
            var buildCode = await RunProcessAsync(
                "scons",
                "",
                _config.WiotaMeshRoot,
                Path.Combine(iterationDirectory, "scons.log"),
                cancellationToken).ConfigureAwait(false);
            if (buildCode != 0)
            {
                return new TdmaLoopAnalysisResult
                {
                    Success = false,
                    Stage = "build",
                    Reason = $"scons failed with exit code {buildCode}."
                };
            }
        }

        if (_config.FlashFirmware)
        {
            var flashResult = await FlashAllAsync(iterationDirectory, cancellationToken).ConfigureAwait(false);
            if (!flashResult.Success)
            {
                return new TdmaLoopAnalysisResult
                {
                    Success = false,
                    Stage = "flash",
                    Reason = flashResult.Reason,
                    Evidence = flashResult.Evidence
                };
            }
        }

        var events = new ConcurrentQueue<TdmaLogEvent>();
        using var collector = new LoopLogCollector(_config.Nodes, iterationDirectory, events);
        collector.Open();

        if (_config.BootWaitSeconds > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(_config.BootWaitSeconds), cancellationToken).ConfigureAwait(false);
        }

        if (_config.ConfirmBuildVersion && !string.IsNullOrWhiteSpace(expectedVersion))
        {
            var versionResult = await ConfirmBuildVersionAsync(
                collector,
                expectedVersion,
                cancellationToken).ConfigureAwait(false);
            if (!versionResult.Success)
            {
                return versionResult;
            }
        }

        await SendPlanCommandsAsync(collector, cancellationToken).ConfigureAwait(false);
        var syncResult = await DelayWithSyncCheckAsync(
            collector,
            events,
            TimeSpan.FromSeconds(_config.PureSyncSeconds),
            cancellationToken).ConfigureAwait(false);
        if (syncResult is not null)
        {
            return syncResult;
        }

        var syncLost = events.FirstOrDefault(item => item.Type == TdmaEventType.SyncLost);
        if (syncLost is not null)
        {
            return TdmaLoopAnalyzer.Analyze(events, BuildCriteria());
        }

        await SendCenterCommandAsync(collector, _config.SendCommand, cancellationToken).ConfigureAwait(false);

        var deadline = DateTimeOffset.Now + TimeSpan.FromSeconds(_config.LoopTimeoutSeconds);
        TdmaLoopAnalysisResult result;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            var abnormalResult = BuildAbnormalResult(collector);
            if (abnormalResult is not null)
            {
                return abnormalResult;
            }

            result = TdmaLoopAnalyzer.Analyze(events, BuildCriteria());
            if (result.Success || result.HasSyncLost)
            {
                return result;
            }
        }
        while (DateTimeOffset.Now < deadline);

        result = TdmaLoopAnalyzer.Analyze(events, BuildCriteria());
        return result with
        {
            Reason = result.Success ? result.Reason : $"Timeout after {_config.LoopTimeoutSeconds}s. {result.Reason}"
        };
    }

    private string GenerateBuildVersion(int iteration, string iterationDirectory)
    {
        if (!_config.BuildFirmware)
        {
            return string.Empty;
        }

        var versionPath = Path.Combine(iterationDirectory, "build-version.txt");
        var generator = new TdmaBuildVersionGenerator(
            _config.BuildVersionPrefix,
            _config.VersionHeaderPath,
            versionPath);
        var version = generator.Generate(iteration);
        Console.WriteLine($"[BUILD_VERSION] {version}");
        return version;
    }

    private async Task<TdmaLoopAnalysisResult> ConfirmBuildVersionAsync(
        LoopLogCollector collector,
        string expectedVersion,
        CancellationToken cancellationToken)
    {
        foreach (var node in _config.Nodes)
        {
            await SendAtCommandAsync(collector, node, _config.VersionQueryCommand, cancellationToken).ConfigureAwait(false);
            if (0 < _config.CommandDelayMilliseconds)
            {
                await Task.Delay(_config.CommandDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }

        var deadline = DateTimeOffset.Now + TimeSpan.FromSeconds(_config.VersionConfirmTimeoutSeconds);
        TdmaLoopAnalysisResult result;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = TdmaVersionConfirmAnalyzer.Analyze(
                _config.Nodes.Select(node => node.Name),
                collector.GetRawLinesByNode(),
                _config.VersionResponsePrefix,
                expectedVersion);
            if (result.Success)
            {
                return result;
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
        while (DateTimeOffset.Now < deadline);

        result = TdmaVersionConfirmAnalyzer.Analyze(
            _config.Nodes.Select(node => node.Name),
            collector.GetRawLinesByNode(),
            _config.VersionResponsePrefix,
            expectedVersion);
        return result with
        {
            Reason = $"Timeout waiting build version {expectedVersion}. {result.Reason}"
        };
    }

    private async Task<TdmaLoopAnalysisResult?> DelayWithSyncCheckAsync(
        LoopLogCollector collector,
        ConcurrentQueue<TdmaLogEvent> events,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.Now + delay;
        while (DateTimeOffset.Now < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var abnormalResult = BuildAbnormalResult(collector);
            if (abnormalResult is not null)
            {
                return abnormalResult;
            }

            if (events.Any(item => item.Type == TdmaEventType.SyncLost))
            {
                return TdmaLoopAnalyzer.Analyze(events, BuildCriteria());
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private TdmaLoopAnalysisResult? BuildAbnormalResult(LoopLogCollector collector)
    {
        var matcher = new TdmaAbnormalPatternMatcher(_config.AbnormalPatterns);
        if (!collector.TryGetAbnormal(matcher, out var evidence))
        {
            return null;
        }

        return new TdmaLoopAnalysisResult
        {
            Success = false,
            Stage = "anomaly",
            Reason = "Abnormal log pattern matched.",
            HasSyncLost = evidence.Contains("LOST", StringComparison.OrdinalIgnoreCase),
            Evidence = [evidence]
        };
    }

    private async Task<FlashResult> FlashAllAsync(string iterationDirectory, CancellationToken cancellationToken)
    {
        if (!File.Exists(_config.YmodemToolPath))
        {
            throw new FileNotFoundException("YMODEM tool not found.", _config.YmodemToolPath);
        }

        if (!File.Exists(_config.FirmwarePath))
        {
            throw new FileNotFoundException("Firmware not found.", _config.FirmwarePath);
        }

        var flashNodes = GetFlashNodes();

        Console.WriteLine(
            $"[FLASH_MODE] parallel={_config.ParallelFlash} nodes={flashNodes.Length}");
        if (_config.ParallelFlash)
        {
            var tasks = flashNodes
                .Select(node => FlashOneAsync(node, iterationDirectory, cancellationToken))
                .ToArray();
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            var failedNodes = flashNodes
                .Zip(results)
                .Where(item => item.Second != 0)
                .Select(item => item.First)
                .ToArray();

            if (failedNodes.Length == 0)
            {
                return FlashResult.Ok();
            }

            Console.WriteLine(
                $"[FLASH_RETRY] Parallel flash failed on {failedNodes.Length} node(s), retry sequentially.");
            foreach (var node in failedNodes)
            {
                var code = await FlashOneAsync(
                    node,
                    iterationDirectory,
                    cancellationToken,
                    "retry").ConfigureAwait(false);
                if (code != 0)
                {
                    return FlashResult.Fail(
                        "YMODEM firmware update failed.",
                        $"{node.Name} {node.AtPort} retry ymodem exit code {code}");
                }
            }

            return FlashResult.Ok();
        }

        foreach (var node in flashNodes)
        {
            var code = await FlashOneAsync(node, iterationDirectory, cancellationToken).ConfigureAwait(false);
            if (code != 0)
            {
                return FlashResult.Fail(
                    "YMODEM firmware update failed.",
                    $"{node.Name} {node.AtPort} ymodem exit code {code}");
            }
        }

        return FlashResult.Ok();
    }

    private TdmaLoopNodeConfig[] GetFlashNodes()
    {
        return _config.Nodes
            .Where(node => !string.IsNullOrWhiteSpace(node.AtPort))
            .ToArray();
    }

    private async Task<FlashResult> CheckFlashPortsReadyAsync(
        IReadOnlyList<TdmaLoopNodeConfig> nodes,
        string iterationDirectory,
        CancellationToken cancellationToken)
    {
        var logPath = Path.Combine(iterationDirectory, "flash_preflight.log");
        var lines = new List<string>();
        var failed = new List<string>();

        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probe = ProbeAtPort(node);
            var line =
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {node.Name} {node.AtPort} {probe}";
            lines.Add(line);
            Console.WriteLine($"[FLASH_PREFLIGHT] {node.Name} {node.AtPort} {probe}");
            if (!probe.Success)
            {
                failed.Add($"{node.Name} {node.AtPort}: {probe}");
            }
        }

        await File.WriteAllLinesAsync(logPath, lines, cancellationToken).ConfigureAwait(false);
        if (failed.Count == 0)
        {
            return FlashResult.Ok();
        }

        return new FlashResult(
            false,
            "AT preflight failed before YMODEM firmware update.",
            failed);
    }

    private static AtProbeResult ProbeAtPort(TdmaLoopNodeConfig node)
    {
        var lastResponse = string.Empty;
        foreach (var baudRate in BuildAtProbeBaudRates(node.AtBaudRate))
        {
            var response = ProbeAtPort(node.AtPort!, baudRate);
            lastResponse = response;
            if (response.Contains("OK", StringComparison.OrdinalIgnoreCase))
            {
                return new AtProbeResult(true, baudRate, response);
            }
        }

        return new AtProbeResult(false, 0, lastResponse);
    }

    internal static int[] BuildAtProbeBaudRates(int preferredBaudRate)
    {
        return preferredBaudRate == 460800 ?
            [460800, 115200] :
            [preferredBaudRate, 460800];
    }

    private static string ProbeAtPort(string portName, int baudRate)
    {
        try
        {
            using var port = new SerialPort(portName, baudRate)
            {
                ReadTimeout = 500,
                WriteTimeout = 1000
            };
            port.Open();
            port.DiscardInBuffer();
            port.DiscardOutBuffer();
            port.Write("AT\r\n");
            Thread.Sleep(AtProbeTimeoutMilliseconds);
            return port.ReadExisting().Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ex.Message;
        }
    }

    private Task<int> FlashOneAsync(
        TdmaLoopNodeConfig node,
        string iterationDirectory,
        CancellationToken cancellationToken,
        string? attemptName = null)
    {
        var suffix = string.IsNullOrWhiteSpace(attemptName) ? string.Empty : $"_{attemptName}";
        var logPath = Path.Combine(iterationDirectory, $"{node.Name}_ymodem{suffix}.log");
        Console.WriteLine($"[FLASH_START] {node.Name} {node.AtPort}{suffix}");
        return RunProcessAsync(
            _config.YmodemToolPath,
            Quote(_config.FirmwarePath) + " " + node.AtPort + " " + node.AtBaudRate + " " + _config.YmodemBaudRate,
            _config.WiotaMeshRoot,
            logPath,
            cancellationToken);
    }

    private async Task SendPlanCommandsAsync(
        LoopLogCollector collector,
        CancellationToken cancellationToken)
    {
        foreach (var item in _config.PlanCommands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = FindNode(item.Node);
            await SendAtCommandAsync(collector, node, item.Command, cancellationToken).ConfigureAwait(false);
            var delay = item.DelayMilliseconds ?? _config.CommandDelayMilliseconds;
            if (delay > 0)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private Task SendCenterCommandAsync(
        LoopLogCollector collector,
        string command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException("SendCommand is empty.");
        }

        return SendAtCommandAsync(collector, FindNode(_config.CenterNode), command, cancellationToken);
    }

    private static Task SendAtCommandAsync(
        LoopLogCollector collector,
        TdmaLoopNodeConfig node,
        string command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(node.AtPort))
        {
            throw new InvalidOperationException($"Node '{node.Name}' has no AtPort.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (collector.TrySend(node, command.TrimEnd('\r', '\n') + "\r\n", cancellationToken))
        {
            return Task.CompletedTask;
        }

        using var port = new SerialPort(node.AtPort, node.AtBaudRate)
        {
            ReadTimeout = 500,
            WriteTimeout = 1000
        };
        port.Open();
        port.DiscardInBuffer();
        port.DiscardOutBuffer();
        port.Write(command.TrimEnd('\r', '\n') + "\r\n");
        return Task.CompletedTask;
    }

    private TdmaLoopCriteria BuildCriteria()
    {
        var center = FindNode(_config.CenterNode);
        var target = FindNode(_config.TargetNode);
        return new TdmaLoopCriteria
        {
            CenterAddress = center.Address,
            TargetAddress = target.Address,
            CenterNodeName = center.Name,
            TargetNodeName = target.Name,
            RequireOneFrameLoop = true
        };
    }

    private TdmaLoopNodeConfig FindNode(string name)
    {
        return _config.Nodes.FirstOrDefault(
            node => string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Node not found: {name}");
    }

    private void ValidateConfig()
    {
        if (_config.Nodes.Length == 0)
        {
            throw new InvalidOperationException("Config must contain Nodes.");
        }

        if (_config.ConfirmBuildVersion && !_config.BuildFirmware)
        {
            throw new InvalidOperationException("ConfirmBuildVersion requires BuildFirmware.");
        }

        _ = FindNode(_config.CenterNode);
        _ = FindNode(_config.TargetNode);
    }

    private static async Task<int> RunProcessAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        string logPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        await using var log = new StreamWriter(logPath, append: false);
        process.Start();
        var stdout = PumpAsync(process.StandardOutput, log, cancellationToken);
        var stderr = PumpAsync(process.StandardError, log, cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return process.ExitCode;
    }

    private static async Task PumpAsync(
        StreamReader reader,
        StreamWriter log,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            await log.WriteLineAsync(line).ConfigureAwait(false);
            await log.FlushAsync(cancellationToken).ConfigureAwait(false);
            Console.WriteLine(line);
        }
    }

    private static Task WriteJsonAsync(string path, TdmaLoopAnalysisResult result, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        return File.WriteAllTextAsync(path, json, cancellationToken);
    }

    private static string Quote(string value)
    {
        return value.Contains(' ', StringComparison.Ordinal) ? "\"" + value + "\"" : value;
    }

    private sealed class LoopLogCollector : IDisposable
    {
        private readonly ConcurrentQueue<TdmaLogEvent> _events;
        private readonly List<SerialPortSession> _sessions = [];
        private readonly Dictionary<string, SerialPortSession> _sessionByNode = [];
        private readonly Dictionary<string, StreamWriter> _writers = [];
        private readonly Dictionary<string, List<string>> _rawLinesByNode = [];
        private readonly object _lock = new();
        private readonly TdmaLoopNodeConfig[] _nodes;
        private readonly string _iterationDirectory;

        public LoopLogCollector(
            TdmaLoopNodeConfig[] nodes,
            string iterationDirectory,
            ConcurrentQueue<TdmaLogEvent> events)
        {
            _nodes = nodes;
            _iterationDirectory = iterationDirectory;
            _events = events;
        }

        public void Open()
        {
            foreach (var node in _nodes.Where(item => !string.IsNullOrWhiteSpace(item.LogPort)))
            {
                var session = new SerialPortSession(node.Name);
                var writer = new StreamWriter(Path.Combine(_iterationDirectory, $"{node.Name}.log"), append: false);
                _writers[node.Name] = writer;
                _rawLinesByNode[node.Name] = [];
                session.LinesReceived += (_, lines) => OnLinesReceived(node.Name, lines);
                session.Open(node.LogPort!, node.LogBaudRate);
                _sessions.Add(session);
                _sessionByNode[node.Name] = session;
            }
        }

        public bool TrySend(TdmaLoopNodeConfig node, string payload, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(node.AtPort)
                || string.IsNullOrWhiteSpace(node.LogPort)
                || !string.Equals(node.AtPort, node.LogPort, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!_sessionByNode.TryGetValue(node.Name, out var session) || !session.IsConnected)
            {
                return false;
            }

            session.SendAsync(payload, cancellationToken).GetAwaiter().GetResult();
            return true;
        }

        public IReadOnlyDictionary<string, IReadOnlyList<string>> GetRawLinesByNode()
        {
            lock (_lock)
            {
                return _rawLinesByNode.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<string>)pair.Value.ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            }
        }

        public bool TryGetAbnormal(TdmaAbnormalPatternMatcher matcher, out string evidence)
        {
            lock (_lock)
            {
                foreach (var pair in _rawLinesByNode)
                {
                    foreach (var line in pair.Value)
                    {
                        if (matcher.TryMatch(pair.Key, line, out evidence))
                        {
                            return true;
                        }
                    }
                }
            }

            evidence = string.Empty;
            return false;
        }

        private void OnLinesReceived(string nodeName, IReadOnlyList<ReceivedLogLine> lines)
        {
            lock (_lock)
            {
                foreach (var line in lines)
                {
                    _writers[nodeName].WriteLine(line.FormattedText);
                    _writers[nodeName].Flush();
                    _rawLinesByNode[nodeName].Add(line.Text);
                    if (TdmaLogParser.TryParse(nodeName, line.Text, out var logEvent) && logEvent is not null)
                    {
                        _events.Enqueue(logEvent);
                    }
                }
            }
        }

        public void Dispose()
        {
            foreach (var session in _sessions)
            {
                session.Dispose();
            }

            foreach (var writer in _writers.Values)
            {
                writer.Dispose();
            }
        }
    }

    private sealed record FlashResult(bool Success, string Reason, IReadOnlyList<string> Evidence)
    {
        public static FlashResult Ok()
        {
            return new FlashResult(true, string.Empty, []);
        }

        public static FlashResult Fail(string reason, string evidence)
        {
            return new FlashResult(false, reason, [evidence]);
        }
    }

    internal sealed record AtProbeResult(bool Success, int BaudRate, string Response)
    {
        public override string ToString()
        {
            if (Success)
            {
                return $"OK baud={BaudRate}";
            }

            if (string.IsNullOrWhiteSpace(Response))
            {
                return "FAIL no AT response";
            }

            return $"FAIL {Response.Replace('\r', ' ').Replace('\n', ' ').Trim()}";
        }
    }
}
