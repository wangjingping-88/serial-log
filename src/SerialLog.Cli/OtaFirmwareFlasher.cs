using System.Diagnostics;

namespace SerialLog.Cli;

public sealed class OtaFirmwareFlasher
{
    private readonly EcoLinkOtaRunConfig _config;

    public OtaFirmwareFlasher(EcoLinkOtaRunConfig config)
    {
        _config = config;
    }

    public async Task FlashBaselinesAsync(
        string runDirectory,
        CancellationToken cancellationToken)
    {
        if (!_config.FlashBaseline)
        {
            return;
        }

        foreach (var device in _config.Devices.Where(
            device => device.Enabled && device.FlashBaseline))
        {
            ValidateDevice(device);
            var logPath = Path.Combine(
                runDirectory,
                $"flash-{SanitizeFileName(device.Name)}.log");
            var result = await FlashOneAsync(
                device,
                logPath,
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0
                || (string.Equals(
                        device.FlashMethod,
                        "uc_programmer",
                        StringComparison.OrdinalIgnoreCase)
                    && (!result.Output.Contains("OK", StringComparison.Ordinal)
                        || result.Output.Contains("ERROR", StringComparison.OrdinalIgnoreCase))))
            {
                throw new InvalidOperationException(
                    $"{device.Name}基线固件烧录失败，详见{logPath}。");
            }
        }
    }

    private Task<OtaProcessResult> FlashOneAsync(
        EcoLinkOtaDeviceConfig device,
        string logPath,
        CancellationToken cancellationToken)
    {
        if (string.Equals(
                device.FlashMethod,
                "uc_programmer",
                StringComparison.OrdinalIgnoreCase))
        {
            return RunProcessAsync(
                _config.Tools.UcProgrammerPath,
                [
                    "-f",
                    device.BaselineFirmwarePath,
                    "-a",
                    _config.Tools.UcProgrammerAddress,
                    "-i",
                    _config.Tools.UcProgrammerDeviceIndex.ToString()
                ],
                logPath,
                cancellationToken);
        }

        if (string.Equals(
                device.FlashMethod,
                "eco_ymodem",
                StringComparison.OrdinalIgnoreCase))
        {
            return RunProcessAsync(
                _config.Tools.EcoYmodemPath,
                [device.BaselineFirmwarePath, device.PortName],
                logPath,
                cancellationToken);
        }

        if (string.Equals(
                device.FlashMethod,
                "gateway_ymodem",
                StringComparison.OrdinalIgnoreCase))
        {
            return RunProcessAsync(
                _config.Tools.GatewayYmodemPath,
                ["--gateway", device.BaselineFirmwarePath, device.PortName],
                logPath,
                cancellationToken);
        }

        throw new InvalidOperationException(
            $"设备{device.Name}使用了未知烧录方式：{device.FlashMethod}。");
    }

    private void ValidateDevice(EcoLinkOtaDeviceConfig device)
    {
        if (string.IsNullOrWhiteSpace(device.Name)
            || string.IsNullOrWhiteSpace(device.BaselineFirmwarePath)
            || !File.Exists(device.BaselineFirmwarePath))
        {
            throw new InvalidOperationException(
                $"设备{device.Name}的基线烧录配置无效或固件不存在。");
        }

        var executable = device.FlashMethod.ToLowerInvariant() switch
        {
            "uc_programmer" => _config.Tools.UcProgrammerPath,
            "eco_ymodem" => _config.Tools.EcoYmodemPath,
            "gateway_ymodem" => _config.Tools.GatewayYmodemPath,
            _ => string.Empty
        };
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            throw new FileNotFoundException(
                $"设备{device.Name}的烧录工具不存在。",
                executable);
        }

        if (!string.Equals(
                device.FlashMethod,
                "uc_programmer",
                StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(device.PortName))
        {
            throw new InvalidOperationException(
                $"设备{device.Name}通过串口烧录但未配置PortName。");
        }
    }

    private async Task<OtaProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string logPath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = _config.EcoLinkRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await stdoutTask.ConfigureAwait(false)
            + await stderrTask.ConfigureAwait(false);
        await File.WriteAllTextAsync(logPath, output, cancellationToken)
            .ConfigureAwait(false);
        return new OtaProcessResult(process.ExitCode, output);
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return string.Concat(value.Select(character =>
            invalid.Contains(character) ? '_' : character));
    }

    private sealed record OtaProcessResult(int ExitCode, string Output);
}
