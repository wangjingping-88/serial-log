using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Ports;
using System.Security.Cryptography;
using System.Text.Json;
using SerialLog.Core.EcoLink;
using SerialLog.Core.Logging;
using SerialLog.Core.Serial;

namespace SerialLog.Cli;

public sealed class EcoLinkLoopRunner
{
    private const string SingleInstanceSemaphoreName =
        "SerialLog.EcoLinkLoopRunner.SingleInstance";

    private readonly EcoLinkLoopRunConfig _config;

    public EcoLinkLoopRunner(EcoLinkLoopRunConfig config)
    {
        _config = config;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        using var semaphore = new Semaphore(1, 1, SingleInstanceSemaphoreName);
        if (!semaphore.WaitOne(0))
        {
            Console.Error.WriteLine("[运行锁] 已有 ecolink-loop 正在运行。");
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

        var records = new List<EcoLinkLoopIterationRecord>();
        var maxIterations = Math.Max(1, _config.MaxIterations);
        for (var iteration = 1; iteration <= maxIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.Combine(
                _config.WorkRoot,
                $"{DateTime.Now:yyyyMMdd_HHmmss}_iter_{iteration:000}");
            Directory.CreateDirectory(directory);
            Console.WriteLine($"[迭代] {iteration} 目录={directory}");

            EcoLinkTestAnalysisResult result;
            try
            {
                result = await RunOneIterationAsync(
                    iteration,
                    directory,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = new EcoLinkTestAnalysisResult
                {
                    IsTerminal = true,
                    Stage = "exception",
                    Reason = ex.Message,
                    Evidence = [ex.ToString()]
                };
            }

            await WriteJsonAsync(
                Path.Combine(directory, "result.json"),
                result,
                cancellationToken).ConfigureAwait(false);
            records.Add(new EcoLinkLoopIterationRecord(iteration, directory, result));
            Console.WriteLine(
                $"[结果] success={result.Success} stage={result.Stage} reason={result.Reason}");

            if (result.Success)
            {
                await WriteSummaryAsync(records, true, cancellationToken).ConfigureAwait(false);
                return 0;
            }

            if (IsInfrastructureFailure(result))
            {
                Console.Error.WriteLine(
                    $"[停止] 基础设施阶段失败，不再用同一条件重复迭代：{result.Stage}");
                await WriteSummaryAsync(records, true, cancellationToken).ConfigureAwait(false);
                return 2;
            }

            if (0 < _config.SummaryInterval
                && 0 == iteration % _config.SummaryInterval)
            {
                await WriteSummaryAsync(records, false, cancellationToken).ConfigureAwait(false);
            }
        }

        await WriteSummaryAsync(records, true, cancellationToken).ConfigureAwait(false);
        return 2;
    }

    private static bool IsInfrastructureFailure(EcoLinkTestAnalysisResult result)
    {
        if (!result.IsTerminal)
        {
            return false;
        }

        return result.Stage is "build"
            or "flash_preflight"
            or "flash"
            or "version_confirm"
            or "log_open"
            or "exception";
    }

    private async Task<EcoLinkTestAnalysisResult> RunOneIterationAsync(
        int iteration,
        string iterationDirectory,
        CancellationToken cancellationToken)
    {
        var version = GenerateBuildVersion(iteration, iterationDirectory);
        var firmwareResult = await PrepareFirmwareAsync(
            version,
            iterationDirectory,
            cancellationToken).ConfigureAwait(false);
        if (!firmwareResult.Success)
        {
            return firmwareResult.Failure!;
        }

        if (_config.FlashFirmware)
        {
            var preflight = await CheckPortsAsync(
                iterationDirectory,
                cancellationToken).ConfigureAwait(false);
            if (preflight is not null)
            {
                return preflight;
            }

            var flash = await FlashAllAsync(
                firmwareResult.FirmwareByTarget,
                iterationDirectory,
                cancellationToken).ConfigureAwait(false);
            if (flash is not null)
            {
                return flash;
            }
        }

        using var collector = new EcoLinkLogCollector(
            GetEnabledDevices(),
            iterationDirectory);
        var openFailure = await collector.OpenAsync(
            TimeSpan.FromSeconds(Math.Max(1, _config.SerialOpenTimeoutSeconds)),
            cancellationToken).ConfigureAwait(false);
        if (openFailure is not null)
        {
            return openFailure;
        }

        if (0 < _config.BootWaitSeconds)
        {
            await Task.Delay(
                TimeSpan.FromSeconds(_config.BootWaitSeconds),
                cancellationToken).ConfigureAwait(false);
        }

        if (_config.ConfirmBuildVersion && !string.IsNullOrWhiteSpace(version))
        {
            var versionFailure = await ConfirmBuildVersionAsync(
                collector,
                version,
                cancellationToken).ConfigureAwait(false);
            if (versionFailure is not null)
            {
                return versionFailure;
            }
        }

        return await WaitForTestResultAsync(
            collector,
            cancellationToken).ConfigureAwait(false);
    }

    private string GenerateBuildVersion(int iteration, string iterationDirectory)
    {
        if (!_config.BuildFirmware)
        {
            return string.Empty;
        }

        var generator = new EcoLinkBuildVersionGenerator(
            _config.BuildVersionPrefix,
            _config.VersionHeaderPath);
        var version = generator.Generate(
            iteration,
            Path.Combine(iterationDirectory, "build-version.txt"));
        Console.WriteLine($"[构建版本] {version}");
        return version;
    }

    private async Task<FirmwarePreparationResult> PrepareFirmwareAsync(
        string version,
        string iterationDirectory,
        CancellationToken cancellationToken)
    {
        if (_config.BuildFirmware)
        {
            foreach (var target in _config.BuildTargets)
            {
                Console.WriteLine($"[构建开始] {target.Name}");
                var exitCode = await RunProcessAsync(
                    target.Executable,
                    target.Arguments,
                    target.WorkingDirectory,
                    Path.Combine(iterationDirectory, $"build_{target.Name}.log"),
                    cancellationToken).ConfigureAwait(false);
                if (0 != exitCode)
                {
                    return FirmwarePreparationResult.Fail(
                        new EcoLinkTestAnalysisResult
                        {
                            IsTerminal = true,
                            Stage = "build",
                            Reason = $"{target.Name} 构建失败，退出码 {exitCode}。"
                        });
                }
            }
        }

        var firmwareDirectory = Path.Combine(iterationDirectory, "firmware");
        Directory.CreateDirectory(firmwareDirectory);
        var firmwareByTarget = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var manifest = new List<FirmwareManifestItem>();
        foreach (var target in _config.BuildTargets)
        {
            if (!File.Exists(target.FirmwarePath))
            {
                return FirmwarePreparationResult.Fail(
                    new EcoLinkTestAnalysisResult
                    {
                        IsTerminal = true,
                        Stage = "build",
                        Reason = $"未找到 {target.Name} 固件：{target.FirmwarePath}"
                    });
            }

            var suffix = string.IsNullOrWhiteSpace(version) ? "current" : version;
            var snapshotPath = Path.Combine(
                firmwareDirectory,
                $"{target.Name}_{suffix}.bin");
            File.Copy(target.FirmwarePath, snapshotPath, overwrite: true);
            var hash = await ComputeSha256Async(snapshotPath, cancellationToken).ConfigureAwait(false);
            var info = new FileInfo(snapshotPath);
            firmwareByTarget[target.Name] = snapshotPath;
            manifest.Add(new FirmwareManifestItem(
                target.Name,
                target.FirmwarePath,
                snapshotPath,
                info.Length,
                hash));
            Console.WriteLine(
                $"[固件] {target.Name} size={info.Length} sha256={hash}");
        }

        await File.WriteAllTextAsync(
            Path.Combine(iterationDirectory, "firmware-manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOptions),
            cancellationToken).ConfigureAwait(false);
        return FirmwarePreparationResult.Ok(firmwareByTarget);
    }

    private async Task<EcoLinkTestAnalysisResult?> CheckPortsAsync(
        string iterationDirectory,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        var failed = new List<string>();
        foreach (var device in GetEnabledDevices())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requireAt = !device.FlashFirmware
                && _config.ConfirmBuildVersion
                && device.ConfirmBuildVersion;
            var probe = ProbePort(
                device,
                requireAt,
                device.FlashFirmware);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} "
                + $"{device.Name} {device.PortName} {probe}";
            lines.Add(line);
            Console.WriteLine($"[串口预检] {device.Name} {probe}");
            if (!probe.Success)
            {
                failed.Add(line);
            }
        }

        await File.WriteAllLinesAsync(
            Path.Combine(iterationDirectory, "flash_preflight.log"),
            lines,
            cancellationToken).ConfigureAwait(false);
        if (0 == failed.Count)
        {
            return null;
        }

        return new EcoLinkTestAnalysisResult
        {
            IsTerminal = true,
            Stage = "flash_preflight",
            Reason = "部分串口无法打开或未收到 AT 应答。",
            Evidence = failed
        };
    }

    private static PortProbeResult ProbePort(
        EcoLinkDeviceConfig device,
        bool requireAt,
        bool allowYmodemPrompt)
    {
        try
        {
            using var port = new SerialPort(device.PortName, device.AtBaudRate)
            {
                ReadTimeout = 300,
                WriteTimeout = 1000
            };
            port.Open();
            if (!requireAt)
            {
                return new PortProbeResult(true, "仅采集，跳过 AT");
            }

            port.DiscardInBuffer();
            port.DiscardOutBuffer();
            port.Write("AT\r\n");
            Thread.Sleep(200);
            var response = port.ReadExisting().Trim();
            if (!ContainsAtOk(response))
            {
                if (allowYmodemPrompt && ContainsYmodemPrompt(response))
                {
                    return new PortProbeResult(
                        true,
                        "设备已处于 YMODEM 接收态");
                }

                return new PortProbeResult(
                    false,
                    string.IsNullOrWhiteSpace(response)
                        ? "串口已打开，但未收到 AT 应答。"
                        : $"未收到 AT OK，应答={response}");
            }

            return new PortProbeResult(true, response);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            return new PortProbeResult(false, ex.Message);
        }
    }

    private static bool ContainsAtOk(string response)
    {
        return response
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(line => string.Equals(
                line.Trim(),
                "OK",
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsYmodemPrompt(string response)
    {
        var prompt = response.Where(character => !char.IsWhiteSpace(character));
        return prompt.Any() && prompt.All(character => 'C' == character);
    }

    private async Task<EcoLinkTestAnalysisResult?> FlashAllAsync(
        IReadOnlyDictionary<string, string> firmwareByTarget,
        string iterationDirectory,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_config.YmodemToolPath))
        {
            return new EcoLinkTestAnalysisResult
            {
                IsTerminal = true,
                Stage = "flash",
                Reason = $"未找到 YMODEM 工具：{_config.YmodemToolPath}"
            };
        }

        var nodes = GetEnabledDevices()
            .Where(device => device.FlashFirmware && !IsExtender(device))
            .ToArray();
        var extenders = GetEnabledDevices()
            .Where(device => device.FlashFirmware && IsExtender(device))
            .ToArray();

        var nodeFailures = new ConcurrentQueue<string>();
        if (_config.ParallelNodeFlash)
        {
            await Task.WhenAll(nodes.Select(async device =>
            {
                var failure = await FlashWithRetryAsync(
                    device,
                    firmwareByTarget,
                    iterationDirectory,
                    cancellationToken).ConfigureAwait(false);
                if (failure is not null)
                {
                    nodeFailures.Enqueue(failure);
                }
            })).ConfigureAwait(false);
        }
        else
        {
            foreach (var device in nodes)
            {
                var failure = await FlashWithRetryAsync(
                    device,
                    firmwareByTarget,
                    iterationDirectory,
                    cancellationToken).ConfigureAwait(false);
                if (failure is not null)
                {
                    nodeFailures.Enqueue(failure);
                    break;
                }
            }
        }

        if (!nodeFailures.IsEmpty)
        {
            return BuildFlashFailure(nodeFailures);
        }

        // Extender 最后烧录，避免它先启动并在日志采集器打开前跑完测试。
        foreach (var device in extenders)
        {
            var failure = await FlashWithRetryAsync(
                device,
                firmwareByTarget,
                iterationDirectory,
                cancellationToken).ConfigureAwait(false);
            if (failure is not null)
            {
                nodeFailures.Enqueue(failure);
                return BuildFlashFailure(nodeFailures);
            }
        }

        return null;
    }

    private async Task<string?> FlashWithRetryAsync(
        EcoLinkDeviceConfig device,
        IReadOnlyDictionary<string, string> firmwareByTarget,
        string iterationDirectory,
        CancellationToken cancellationToken)
    {
        if (!firmwareByTarget.TryGetValue(device.Firmware, out var firmwarePath))
        {
            return $"{device.Name} 未配置固件目标 {device.Firmware}。";
        }

        var attempts = Math.Max(1, _config.FlashRetryCount + 1);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            Console.WriteLine(
                $"[烧录开始] {device.Name} {device.PortName} attempt={attempt}");
            var exitCode = await RunProcessAsync(
                _config.YmodemToolPath,
                BuildYmodemArguments(
                    device,
                    firmwarePath,
                    1 < attempt),
                _config.EcoLinkRoot,
                Path.Combine(
                    iterationDirectory,
                    $"{device.Name}_ymodem_{attempt}.log"),
                cancellationToken).ConfigureAwait(false);
            if (0 == exitCode)
            {
                Console.WriteLine($"[烧录成功] {device.Name}");
                return null;
            }
        }

        return $"{device.Name} {device.PortName} YMODEM 烧录失败，尝试 {attempts} 次。";
    }

    private static string BuildYmodemArguments(
        EcoLinkDeviceConfig device,
        string firmwarePath,
        bool directYmodem)
    {
        var arguments = $"{Quote(firmwarePath)} {device.PortName} "
            + $"{device.AtBaudRate} {device.YmodemBaudRate}";
        return directYmodem ? $"{arguments} --direct" : arguments;
    }

    private async Task<EcoLinkTestAnalysisResult?> ConfirmBuildVersionAsync(
        EcoLinkLogCollector collector,
        string expectedVersion,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.Now
            + TimeSpan.FromSeconds(Math.Max(1, _config.VersionConfirmTimeoutSeconds));
        var nextQuery = DateTimeOffset.MinValue;
        string[] missing;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var logs = collector.GetRawLinesByNode();
            missing = GetEnabledDevices()
                .Where(device => device.ConfirmBuildVersion)
                .Where(device => !ContainsVersion(logs, device.Name, expectedVersion))
                .Select(device => device.Name)
                .ToArray();
            if (0 == missing.Length)
            {
                Console.WriteLine($"[版本确认] 全部待确认设备均为 {expectedVersion}");
                return null;
            }

            if (DateTimeOffset.Now >= nextQuery)
            {
                foreach (var name in missing)
                {
                    await collector.SendAsync(
                        name,
                        "AT+BUILD?\r\n",
                        cancellationToken).ConfigureAwait(false);
                }
                nextQuery = DateTimeOffset.Now + TimeSpan.FromSeconds(1);
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        while (DateTimeOffset.Now < deadline);

        var evidence = collector.GetLatestLines(missing, 3);
        return new EcoLinkTestAnalysisResult
        {
            IsTerminal = true,
            Stage = "version_confirm",
            Reason = $"以下设备未报告本轮版本 {expectedVersion}：{string.Join(", ", missing)}",
            Evidence = evidence
        };
    }

    private static bool ContainsVersion(
        IReadOnlyDictionary<string, IReadOnlyList<string>> logs,
        string nodeName,
        string expectedVersion)
    {
        return logs.TryGetValue(nodeName, out var lines)
            && lines.Any(line => line.Contains(
                $"+BUILD:{expectedVersion}",
                StringComparison.OrdinalIgnoreCase));
    }

    private async Task<EcoLinkTestAnalysisResult> WaitForTestResultAsync(
        EcoLinkLogCollector collector,
        CancellationToken cancellationToken)
    {
        var criteria = new EcoLinkTestCriteria
        {
            ExtenderNodeName = _config.ExtenderNodeName,
            ExpectedNodeCount = _config.ExpectedNodeCount,
            ExpectedRoundsPerPhase = _config.ExpectedRoundsPerPhase,
            AbnormalPatterns = _config.AbnormalPatterns
        };
        var deadline = DateTimeOffset.Now
            + TimeSpan.FromSeconds(Math.Max(1, _config.TestTimeoutSeconds));
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = EcoLinkTestAnalyzer.Analyze(
                collector.GetRawLinesByNode(),
                criteria);
            if (result.IsTerminal)
            {
                return result;
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
        while (DateTimeOffset.Now < deadline);

        return EcoLinkTestAnalyzer.Timeout(
            collector.GetRawLinesByNode(),
            criteria,
            _config.TestTimeoutSeconds);
    }

    private async Task WriteSummaryAsync(
        IReadOnlyList<EcoLinkLoopIterationRecord> records,
        bool isFinal,
        CancellationToken cancellationToken)
    {
        var content = EcoLinkLoopSummary.Build(
            records,
            Math.Max(1, _config.MaxIterations),
            isFinal);
        var fileName = isFinal
            ? "summary_final.md"
            : $"summary_iter_{records.Count:000}.md";
        var path = Path.Combine(_config.WorkRoot, fileName);
        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"[摘要] {path}");
    }

    private void ValidateConfig()
    {
        var enabledDevices = GetEnabledDevices();

        if (0 == _config.BuildTargets.Length)
        {
            throw new InvalidOperationException("BuildTargets 不能为空。");
        }

        if (0 == enabledDevices.Length)
        {
            throw new InvalidOperationException("至少启用一台 Devices 设备。");
        }

        if (_config.ConfirmBuildVersion && !_config.BuildFirmware)
        {
            throw new InvalidOperationException("ConfirmBuildVersion 需要 BuildFirmware=true。");
        }

        EnsureUnique(_config.BuildTargets.Select(target => target.Name), "构建目标名称");
        EnsureUnique(_config.Devices.Select(device => device.Name), "设备名称");
        EnsureUnique(_config.Devices.Select(device => device.PortName), "串口");

        var targetNames = _config.BuildTargets
            .Select(target => target.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var device in enabledDevices)
        {
            if (!targetNames.Contains(device.Firmware))
            {
                throw new InvalidOperationException(
                    $"设备 {device.Name} 引用了不存在的固件目标 {device.Firmware}。");
            }
        }

        var extender = enabledDevices.FirstOrDefault(
            device => string.Equals(
                device.Name,
                _config.ExtenderNodeName,
                StringComparison.OrdinalIgnoreCase));
        if (extender is null || !IsExtender(extender))
        {
            throw new InvalidOperationException("ExtenderNodeName 必须指向 role=extender 的设备。");
        }
    }

    private static void EnsureUnique(IEnumerable<string> values, string label)
    {
        var normalized = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToArray();
        if (normalized.Length != normalized.Distinct(StringComparer.OrdinalIgnoreCase).Count())
        {
            throw new InvalidOperationException($"{label}存在重复项。");
        }
    }

    private static bool IsExtender(EcoLinkDeviceConfig device)
    {
        return string.Equals(device.Role, "extender", StringComparison.OrdinalIgnoreCase);
    }

    private EcoLinkDeviceConfig[] GetEnabledDevices()
    {
        return _config.Devices
            .Where(device => device.Enabled)
            .ToArray();
    }

    private static EcoLinkTestAnalysisResult BuildFlashFailure(
        IEnumerable<string> failures)
    {
        return new EcoLinkTestAnalysisResult
        {
            IsTerminal = true,
            Stage = "flash",
            Reason = "YMODEM 烧录失败。",
            Evidence = failures.ToArray()
        };
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
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

        using var process = new Process { StartInfo = startInfo };
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
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            await log.WriteLineAsync(line).ConfigureAwait(false);
            await log.FlushAsync(cancellationToken).ConfigureAwait(false);
            Console.WriteLine(line);
        }
    }

    private static Task WriteJsonAsync(
        string path,
        EcoLinkTestAnalysisResult result,
        CancellationToken cancellationToken)
    {
        return File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(result, JsonOptions),
            cancellationToken);
    }

    private static string Quote(string value)
    {
        return value.Contains(' ', StringComparison.Ordinal)
            ? $"\"{value}\""
            : value;
    }

    private static JsonSerializerOptions JsonOptions => new()
    {
        WriteIndented = true
    };

    private sealed class EcoLinkLogCollector : IDisposable
    {
        private readonly EcoLinkDeviceConfig[] _devices;
        private readonly string _iterationDirectory;
        private readonly object _lock = new();
        private readonly Dictionary<string, List<string>> _rawLines =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SerialPortSession> _sessions =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, StreamWriter> _writers =
            new(StringComparer.OrdinalIgnoreCase);

        public EcoLinkLogCollector(
            EcoLinkDeviceConfig[] devices,
            string iterationDirectory)
        {
            _devices = devices;
            _iterationDirectory = iterationDirectory;
        }

        public async Task<EcoLinkTestAnalysisResult?> OpenAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            foreach (var device in _devices)
            {
                var deadline = DateTimeOffset.Now + timeout;
                Exception? latest = null;
                while (DateTimeOffset.Now < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var session = new SerialPortSession(device.Name);
                    try
                    {
                        session.Open(device.PortName, device.LogBaudRate);
                        var writer = new StreamWriter(
                            Path.Combine(_iterationDirectory, $"{device.Name}.log"),
                            append: false)
                        {
                            AutoFlush = true
                        };
                        lock (_lock)
                        {
                            _rawLines[device.Name] = [];
                            _sessions[device.Name] = session;
                            _writers[device.Name] = writer;
                        }
                        session.LinesReceived += (_, lines) =>
                            OnLinesReceived(device.Name, lines);
                        latest = null;
                        break;
                    }
                    catch (Exception ex) when (ex is IOException
                        or UnauthorizedAccessException
                        or InvalidOperationException)
                    {
                        latest = ex;
                        session.Dispose();
                        await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (latest is not null)
                {
                    return new EcoLinkTestAnalysisResult
                    {
                        IsTerminal = true,
                        Stage = "log_open",
                        Reason = $"无法打开 {device.Name} 的日志串口 {device.PortName}。",
                        Evidence = [latest.Message]
                    };
                }
            }

            return null;
        }

        public Task SendAsync(
            string nodeName,
            string payload,
            CancellationToken cancellationToken)
        {
            SerialPortSession session;
            lock (_lock)
            {
                if (!_sessions.TryGetValue(nodeName, out session!))
                {
                    throw new InvalidOperationException($"设备 {nodeName} 的串口未打开。");
                }
            }

            return session.SendAsync(payload, cancellationToken);
        }

        public IReadOnlyDictionary<string, IReadOnlyList<string>> GetRawLinesByNode()
        {
            lock (_lock)
            {
                return _rawLines.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<string>)pair.Value.ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            }
        }

        public IReadOnlyList<string> GetLatestLines(
            IEnumerable<string> nodeNames,
            int count)
        {
            var result = new List<string>();
            lock (_lock)
            {
                foreach (var name in nodeNames)
                {
                    if (!_rawLines.TryGetValue(name, out var lines))
                    {
                        result.Add($"{name}: <无日志>");
                        continue;
                    }

                    result.AddRange(lines.TakeLast(count).Select(line => $"{name}: {line}"));
                }
            }

            return result;
        }

        private void OnLinesReceived(
            string nodeName,
            IReadOnlyList<ReceivedLogLine> lines)
        {
            lock (_lock)
            {
                foreach (var line in lines)
                {
                    _writers[nodeName].WriteLine(line.FormattedText);
                    _rawLines[nodeName].Add(line.Text);
                }
            }
        }

        public void Dispose()
        {
            foreach (var session in _sessions.Values)
            {
                session.Dispose();
            }

            foreach (var writer in _writers.Values)
            {
                writer.Dispose();
            }
        }
    }

    private sealed record FirmwareManifestItem(
        string Target,
        string SourcePath,
        string SnapshotPath,
        long Size,
        string Sha256);

    private sealed record FirmwarePreparationResult(
        bool Success,
        IReadOnlyDictionary<string, string> FirmwareByTarget,
        EcoLinkTestAnalysisResult? Failure)
    {
        public static FirmwarePreparationResult Ok(
            IReadOnlyDictionary<string, string> firmwareByTarget)
        {
            return new FirmwarePreparationResult(true, firmwareByTarget, null);
        }

        public static FirmwarePreparationResult Fail(EcoLinkTestAnalysisResult failure)
        {
            return new FirmwarePreparationResult(
                false,
                new Dictionary<string, string>(),
                failure);
        }
    }

    private sealed record PortProbeResult(bool Success, string Response)
    {
        public override string ToString()
        {
            if (!Success)
            {
                return $"FAIL {Response}";
            }

            return string.IsNullOrWhiteSpace(Response)
                ? "OPEN（未收到 AT 应答）"
                : $"OK {Response.Replace('\r', ' ').Replace('\n', ' ').Trim()}";
        }
    }
}
