using System.IO.Ports;
using System.Text.Json;

namespace SerialLog.Cli;

public sealed class EcoLinkOtaRunner
{
    private const string SingleInstanceSemaphoreName =
        "SerialLog.EcoLinkOtaRunner.SingleInstance";
    private readonly EcoLinkOtaRunConfig _config;
    private string? _runDirectory;

    public EcoLinkOtaRunner(EcoLinkOtaRunConfig config)
    {
        _config = config;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        using var semaphore = new Semaphore(1, 1, SingleInstanceSemaphoreName);
        if (!semaphore.WaitOne(0))
        {
            Console.Error.WriteLine("已有ecolink-ota任务正在运行。");
            return 2;
        }

        try
        {
            return await RunCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("OTA自动测试已取消。");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"OTA自动测试失败：{ex.Message}");
            if (_runDirectory is not null)
            {
                await WriteResultAsync(
                    _runDirectory,
                    new EcoLinkOtaRunResult(
                        false,
                        "infrastructure",
                        ex.Message,
                        []),
                    CancellationToken.None).ConfigureAwait(false);
            }
            return 2;
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<int> RunCoreAsync(CancellationToken cancellationToken)
    {
        ValidateConfig();
        var runDirectory = CreateRunDirectory();
        _runDirectory = runDirectory;
        Console.WriteLine($"[OTA] 运行目录：{runDirectory}");

        await using var httpServer = new OtaHttpFileServer();
        httpServer.Start(_config.Http);
        Console.WriteLine(
            $"[HTTP] 监听{_config.Http.BindAddress}:{_config.Http.Port}，"
            + $"对外地址http://{_config.Http.AdvertiseAddress}:{_config.Http.Port}/"
            + $"{_config.Http.BasePath.Trim('/')}/");

        await using var mqtt = new Mqtt311Client();
        await mqtt.ConnectAsync(_config.Mqtt, cancellationToken).ConfigureAwait(false);
        Console.WriteLine(
            $"[MQTT] 已连接{_config.Mqtt.Host}:{_config.Mqtt.Port}，"
            + $"clientId={mqtt.ClientId}，订阅={_config.Mqtt.UpTopic}");

        var configuredExtenderDeviceId = _config.ExtenderDeviceId;
        _config.ExtenderDeviceId = await ResolveExtenderDeviceIdAsync(
            mqtt,
            runDirectory,
            cancellationToken).ConfigureAwait(false);
        OtaTargetTopologyResolver.Apply(_config, configuredExtenderDeviceId);

        var packageBuilder = new OtaPackageBuilder(_config, httpServer);
        var packages = await packageBuilder.BuildAsync(
            runDirectory,
            cancellationToken).ConfigureAwait(false);
        await VerifyHttpServerAsync(
            httpServer,
            packages.Values.FirstOrDefault(),
            cancellationToken).ConfigureAwait(false);

        var flasher = new OtaFirmwareFlasher(_config);
        await flasher.FlashBaselinesAsync(runDirectory, cancellationToken)
            .ConfigureAwait(false);

        using var serialCollector = new OtaSerialLogCollector(
            _config.Devices,
            runDirectory);
        if (_config.Devices.Any(device => device.Enabled && device.MonitorLogs))
        {
            await serialCollector.OpenAsync(
                TimeSpan.FromSeconds(_config.SerialOpenTimeoutSeconds),
                cancellationToken).ConfigureAwait(false);
            Console.WriteLine("[串口] 所有启用的日志串口均已打开。");
        }

        if (_config.PreflightOnly || !_config.PublishUpgradeCommands)
        {
            Console.WriteLine(
                _config.PreflightOnly
                    ? "[预检] MQTT、HTTP、工具和串口检查完成，未下发升级命令。"
                    : "[准备] 差分包已生成，PublishUpgradeCommands=false，未下发升级命令。");
            await WriteResultAsync(
                runDirectory,
                new EcoLinkOtaRunResult(true, "preflight", "预检完成", []),
                cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var targetResults = new List<EcoLinkOtaTargetResult>();
        foreach (var target in _config.Targets.Where(target => target.Enabled))
        {
            var result = await RunTargetAsync(
                target,
                packages[target.Name],
                mqtt,
                serialCollector,
                runDirectory,
                cancellationToken).ConfigureAwait(false);
            targetResults.Add(result);
            if (!result.Success)
            {
                await WriteResultAsync(
                    runDirectory,
                    new EcoLinkOtaRunResult(
                        false,
                        target.Name,
                        result.Reason,
                        targetResults),
                    cancellationToken).ConfigureAwait(false);
                return 2;
            }
        }

        await WriteResultAsync(
            runDirectory,
            new EcoLinkOtaRunResult(true, "done", "全部OTA目标验证成功", targetResults),
            cancellationToken).ConfigureAwait(false);
        Console.WriteLine("[OTA] Sync、Async和Node阶段全部通过。");
        return 0;
    }

    private async Task<EcoLinkOtaTargetResult> RunTargetAsync(
        EcoLinkOtaTargetConfig target,
        EcoLinkOtaPackage package,
        Mqtt311Client mqtt,
        OtaSerialLogCollector serialCollector,
        string runDirectory,
        CancellationToken cancellationToken)
    {
        DrainMessages(mqtt);
        var command = OtaUpgradeCommandBuilder.Build(target, package);
        var commandPath = Path.Combine(
            runDirectory,
            $"ota-command-{SanitizeFileName(target.Name)}.json");
        await File.WriteAllTextAsync(commandPath, command, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine(
            $"[OTA:{target.Name}] 下发{_config.Mqtt.DownTopic}，"
            + $"文件={package.FileName}，size={package.Size}，md5={package.Md5}");
        await mqtt.PublishAsync(
            _config.Mqtt.DownTopic,
            command,
            _config.Mqtt.Qos,
            cancellationToken).ConfigureAwait(false);

        var mqttLogPath = Path.Combine(
            runDirectory,
            $"mqtt-{SanitizeFileName(target.Name)}.log");
        await using var mqttLog = new StreamWriter(mqttLogPath, append: false)
        {
            AutoFlush = true
        };

        var deadline = DateTimeOffset.Now + TimeSpan.FromSeconds(target.TimeoutSeconds);
        var nextVersionQuery = DateTimeOffset.MinValue;
        var mqttSucceeded = target.SuccessMqttPrompts.Length == 0;
        var gatewayVersions = new HashSet<uint>();
        var evidence = new List<string>();
        while (DateTimeOffset.Now < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (mqtt.Messages.TryRead(out var message))
            {
                var line = $"[{DateTimeOffset.Now:O}] {message.Topic} {message.Text}";
                await mqttLog.WriteLineAsync(line).ConfigureAwait(false);
                evidence.Add(line);

                var failure = FindPattern(message.Text, target.FailurePatterns);
                if (failure is not null)
                {
                    return new EcoLinkOtaTargetResult(
                        target.Name,
                        false,
                        $"MQTT上报失败：{failure}",
                        evidence.TakeLast(20).ToArray());
                }

                if (FindPattern(message.Text, target.SuccessMqttPrompts) is not null)
                {
                    mqttSucceeded = true;
                }

                if (TryReadGatewayVersion(
                        message.Text,
                        target.NewVersion,
                        out var deviceId))
                {
                    gatewayVersions.Add(deviceId);
                }
            }

            var serialLogs = serialCollector.Snapshot();
            var serialFailure = FindSerialFailure(serialLogs, target.FailurePatterns);
            if (serialFailure is not null)
            {
                return new EcoLinkOtaTargetResult(
                    target.Name,
                    false,
                    $"串口日志检测到失败：{serialFailure}",
                    serialCollector.LatestLines(5));
            }

            var serialVersionsConfirmed = AreVersionsConfirmed(
                serialLogs,
                target.VerifyDevices,
                target.NewVersion);
            var gatewayVersionsConfirmed = !target.VerifyGatewayVersionReports
                || target.DeviceIds.All(gatewayVersions.Contains);
            var versionsConfirmed = serialVersionsConfirmed
                && gatewayVersionsConfirmed;
            if (mqttSucceeded && versionsConfirmed)
            {
                return new EcoLinkOtaTargetResult(
                    target.Name,
                    true,
                    "升级结果和版本确认均成功",
                    evidence.TakeLast(20).ToArray());
            }

            if (!versionsConfirmed && DateTimeOffset.Now >= nextVersionQuery)
            {
                await QueryVersionsAsync(
                    target.VerifyDevices,
                    serialCollector,
                    cancellationToken).ConfigureAwait(false);
                if (mqttSucceeded
                    && target.VerifyGatewayVersionReports
                    && !gatewayVersionsConfirmed)
                {
                    var query = OtaUpgradeCommandBuilder.BuildVersionQuery(
                        target.DeviceIds.Except(gatewayVersions));
                    await mqtt.PublishAsync(
                        _config.Mqtt.DownTopic,
                        query,
                        _config.Mqtt.Qos,
                        cancellationToken).ConfigureAwait(false);
                }
                nextVersionQuery = DateTimeOffset.Now + TimeSpan.FromSeconds(2);
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        evidence.AddRange(serialCollector.LatestLines(5));
        return new EcoLinkOtaTargetResult(
            target.Name,
            false,
            $"等待升级完成超时（{target.TimeoutSeconds}秒）",
            evidence.TakeLast(40).ToArray());
    }

    private async Task<uint> ResolveExtenderDeviceIdAsync(
        Mqtt311Client mqtt,
        string runDirectory,
        CancellationToken cancellationToken)
    {
        var configuredDeviceId = _config.ExtenderDeviceId;
        if (!_config.DiscoverExtenderDeviceIdFromCmd201)
        {
            if (configuredDeviceId == 0)
            {
                throw new InvalidOperationException(
                    "未启用cmd=201自动发现时，ExtenderDeviceId必须为非零值。");
            }

            Console.WriteLine(
                $"[MQTT] 使用配置的Extender dev_id={configuredDeviceId}。");
            return configuredDeviceId;
        }

        var timeout = TimeSpan.FromSeconds(_config.DeviceDiscoveryTimeoutSeconds);
        var deadline = DateTimeOffset.UtcNow + timeout;
        var fallbackDeadline = DateTimeOffset.UtcNow
            + TimeSpan.FromSeconds(Math.Min(2, timeout.TotalSeconds));
        var states = new Dictionary<uint, bool>();
        var logPath = Path.Combine(runDirectory, "mqtt-device-discovery.log");
        await using var log = new StreamWriter(logPath, append: false)
        {
            AutoFlush = true
        };

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (mqtt.Messages.TryRead(out var message))
            {
                await log.WriteLineAsync(
                    $"[{DateTimeOffset.Now:O}] {message.Topic} {message.Text}")
                    .ConfigureAwait(false);
                if (OtaDeviceStatusParser.TryParse(
                        message.Text,
                        out var deviceId,
                        out var statusOnline))
                {
                    states[deviceId] = statusOnline;
                    Console.WriteLine(
                        $"[MQTT] cmd=201 dev_id={deviceId} "
                        + $"status={(statusOnline ? "online" : "offline")}。");
                }
            }

            if (configuredDeviceId != 0
                && states.TryGetValue(configuredDeviceId, out var configuredOnline)
                && configuredOnline)
            {
                Console.WriteLine(
                    $"[MQTT] 已确认配置的Extender dev_id={configuredDeviceId}在线。");
                return configuredDeviceId;
            }

            var onlineDeviceIds = states
                .Where(pair => pair.Value)
                .Select(pair => pair.Key)
                .OrderBy(deviceId => deviceId)
                .ToArray();
            if (onlineDeviceIds.Length == 1)
            {
                Console.WriteLine(
                    $"[MQTT] 从cmd=201自动发现Extender dev_id={onlineDeviceIds[0]}。");
                return onlineDeviceIds[0];
            }

            if (configuredDeviceId != 0
                && !states.ContainsKey(configuredDeviceId)
                && onlineDeviceIds.Length == 0
                && DateTimeOffset.UtcNow >= fallbackDeadline)
            {
                Console.WriteLine(
                    $"[MQTT] 观察窗口内未收到cmd=201，使用配置回退dev_id="
                    + $"{configuredDeviceId}。");
                return configuredDeviceId;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        var finalOnlineDeviceIds = states
            .Where(pair => pair.Value)
            .Select(pair => pair.Key)
            .OrderBy(deviceId => deviceId)
            .ToArray();
        if (finalOnlineDeviceIds.Length > 1)
        {
            throw new InvalidOperationException(
                "cmd=201同时报告多个在线Extender，无法唯一确定OTA目标dev_id："
                + string.Join(", ", finalOnlineDeviceIds));
        }

        if (configuredDeviceId != 0
            && states.TryGetValue(configuredDeviceId, out var finalOnline)
            && !finalOnline)
        {
            throw new InvalidOperationException(
                $"cmd=201报告配置的Extender dev_id={configuredDeviceId}离线。");
        }

        throw new InvalidOperationException(
            $"{_config.DeviceDiscoveryTimeoutSeconds}秒内未发现在线Extender dev_id。");
    }

    private async Task VerifyHttpServerAsync(
        OtaHttpFileServer httpServer,
        EcoLinkOtaPackage? package,
        CancellationToken cancellationToken)
    {
        var deleteProbe = false;
        string path;
        Uri uri;
        if (package is null)
        {
            path = Path.Combine(_config.Http.PackageRoot, ".ota-http-probe");
            await File.WriteAllTextAsync(path, "ecolink-ota-probe", cancellationToken)
                .ConfigureAwait(false);
            uri = httpServer.BuildPackageUri(Path.GetFileName(path));
            deleteProbe = true;
        }
        else
        {
            path = package.FullPath;
            uri = package.DownloadUri;
        }

        try
        {
            using var handler = new SocketsHttpHandler { UseProxy = false };
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(5)
            };
            var localUri = new UriBuilder(uri) { Host = "127.0.0.1" }.Uri;
            await VerifyOneHttpUriAsync(
                client,
                path,
                localUri,
                cancellationToken).ConfigureAwait(false);
            await VerifyOneHttpUriAsync(
                client,
                path,
                uri,
                cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"[HTTP] 本机下载校验通过：{uri}");
        }
        finally
        {
            if (deleteProbe && File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static async Task VerifyOneHttpUriAsync(
        HttpClient client,
        string sourcePath,
        Uri uri,
        CancellationToken cancellationToken)
    {
        await using var source = File.OpenRead(sourcePath);
        await using var response = await client.GetStreamAsync(
            uri,
            cancellationToken).ConfigureAwait(false);
        if (!await StreamsEqualAsync(source, response, cancellationToken)
            .ConfigureAwait(false))
        {
            throw new IOException($"HTTP服务返回内容与源文件不一致：{uri}");
        }
    }

    private static async Task<bool> StreamsEqualAsync(
        Stream expected,
        Stream actual,
        CancellationToken cancellationToken)
    {
        var expectedBuffer = new byte[64 * 1024];
        var actualBuffer = new byte[64 * 1024];
        while (true)
        {
            var expectedCount = await expected.ReadAsync(
                expectedBuffer,
                cancellationToken).ConfigureAwait(false);
            var actualCount = await ReadAtMostAsync(
                actual,
                actualBuffer,
                expectedCount,
                cancellationToken).ConfigureAwait(false);
            if (expectedCount != actualCount
                || !expectedBuffer.AsSpan(0, expectedCount)
                    .SequenceEqual(actualBuffer.AsSpan(0, actualCount)))
            {
                return false;
            }

            if (expectedCount == 0)
            {
                return true;
            }
        }
    }

    private static async Task<int> ReadAtMostAsync(
        Stream stream,
        byte[] buffer,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        if (expectedCount == 0)
        {
            return await stream.ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
        }

        var offset = 0;
        while (offset < expectedCount)
        {
            var count = await stream.ReadAsync(
                buffer.AsMemory(offset, expectedCount - offset),
                cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            offset += count;
        }

        return offset;
    }

    private async Task QueryVersionsAsync(
        IEnumerable<string> deviceNames,
        OtaSerialLogCollector serialCollector,
        CancellationToken cancellationToken)
    {
        var deviceMap = _config.Devices.ToDictionary(
            device => device.Name,
            StringComparer.OrdinalIgnoreCase);
        foreach (var deviceName in deviceNames)
        {
            if (deviceMap.TryGetValue(deviceName, out var device)
                && (string.Equals(device.Role, "node", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(device.Role, "async", StringComparison.OrdinalIgnoreCase)))
            {
                await serialCollector.SendTextAsync(
                    deviceName,
                    "AT+BUILD?\r\n",
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool AreVersionsConfirmed(
        IReadOnlyDictionary<string, IReadOnlyList<string>> logs,
        IEnumerable<string> deviceNames,
        string expectedVersion)
    {
        var names = deviceNames.ToArray();
        if (names.Length == 0)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(expectedVersion)
            && names.All(name => logs.TryGetValue(name, out var lines)
                && lines.Any(line => line.Contains(
                    expectedVersion,
                    StringComparison.OrdinalIgnoreCase)));
    }

    private static bool TryReadGatewayVersion(
        string payload,
        string expectedVersion,
        out uint deviceId)
    {
        deviceId = 0;
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!root.TryGetProperty("cmd", out var command)
                || command.GetInt32() is not (20 or 151)
                || !root.TryGetProperty("iote_ver", out var version)
                || !string.Equals(
                    version.GetString(),
                    expectedVersion,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var idField = command.GetInt32() == 151 ? "dev_id" : "src";
            return root.TryGetProperty(idField, out var id)
                && id.TryGetUInt32(out deviceId)
                && deviceId != 0;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string? FindSerialFailure(
        IReadOnlyDictionary<string, IReadOnlyList<string>> logs,
        IEnumerable<string> patterns)
    {
        foreach (var pair in logs)
        {
            foreach (var line in pair.Value)
            {
                var pattern = FindPattern(line, patterns);
                if (pattern is not null)
                {
                    return $"{pair.Key}: {line}";
                }
            }
        }

        return null;
    }

    private static string? FindPattern(string text, IEnumerable<string> patterns)
    {
        return patterns.FirstOrDefault(pattern =>
            !string.IsNullOrWhiteSpace(pattern)
            && text.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }

    private static void DrainMessages(Mqtt311Client mqtt)
    {
        while (mqtt.Messages.TryRead(out _))
        {
        }
    }

    private void ValidateConfig()
    {
        if (string.IsNullOrWhiteSpace(_config.WorkRoot)
            || _config.SerialOpenTimeoutSeconds <= 0
            || _config.DeviceDiscoveryTimeoutSeconds <= 0
            || _config.Mqtt.Qos != 0)
        {
            throw new InvalidOperationException(
                "OTA运行配置无效；当前MQTT发布仅允许QoS 0。");
        }

        EnsureUnique(
            _config.Devices.Where(device => device.Enabled).Select(device => device.Name),
            "设备名称");
        EnsureUnique(
            _config.Devices
                .Where(device => device.Enabled && device.MonitorLogs)
                .Select(device => device.PortName),
            "日志串口");
        EnsureUnique(
            _config.Targets.Where(target => target.Enabled).Select(target => target.Name),
            "OTA目标名称");

        var existingPorts = SerialPort.GetPortNames()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingPorts = _config.Devices
            .Where(device => device.Enabled && device.MonitorLogs)
            .Where(device => !existingPorts.Contains(device.PortName))
            .Select(device => $"{device.Name}={device.PortName}")
            .ToArray();
        if (missingPorts.Length > 0)
        {
            throw new InvalidOperationException(
                $"以下串口不存在：{string.Join(", ", missingPorts)}。");
        }

        if (!_config.PreflightOnly
            && _config.PublishUpgradeCommands
            && !_config.Targets.Any(target => target.Enabled))
        {
            throw new InvalidOperationException("没有启用任何OTA目标。");
        }
    }

    private static void EnsureUnique(IEnumerable<string> values, string label)
    {
        var normalized = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToArray();
        if (normalized.Length != normalized
            .Distinct(StringComparer.OrdinalIgnoreCase).Count())
        {
            throw new InvalidOperationException($"{label}存在重复项。");
        }
    }

    private string CreateRunDirectory()
    {
        Directory.CreateDirectory(_config.WorkRoot);
        var path = Path.Combine(
            _config.WorkRoot,
            DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static Task WriteResultAsync(
        string runDirectory,
        EcoLinkOtaRunResult result,
        CancellationToken cancellationToken)
    {
        return File.WriteAllTextAsync(
            Path.Combine(runDirectory, "ota-result.json"),
            JsonSerializer.Serialize(result, JsonOptions),
            cancellationToken);
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return string.Concat(value.Select(character =>
            invalid.Contains(character) ? '_' : character));
    }

    private static JsonSerializerOptions JsonOptions => new()
    {
        WriteIndented = true
    };
}

public sealed record EcoLinkOtaTargetResult(
    string Target,
    bool Success,
    string Reason,
    IReadOnlyList<string> Evidence);

public sealed record EcoLinkOtaRunResult(
    bool Success,
    string Stage,
    string Reason,
    IReadOnlyList<EcoLinkOtaTargetResult> Targets);
