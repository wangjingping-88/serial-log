using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SerialLog.Cli;

public sealed record EcoLinkOtaPackage(
    string TargetName,
    string FileName,
    string FullPath,
    long Size,
    string Md5,
    Uri DownloadUri);

public sealed class OtaPackageBuilder
{
    private readonly EcoLinkOtaRunConfig _config;
    private readonly OtaHttpFileServer _httpServer;

    public OtaPackageBuilder(
        EcoLinkOtaRunConfig config,
        OtaHttpFileServer httpServer)
    {
        _config = config;
        _httpServer = httpServer;
    }

    public async Task<IReadOnlyDictionary<string, EcoLinkOtaPackage>> BuildAsync(
        string runDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_config.Http.PackageRoot);
        var packages = new Dictionary<string, EcoLinkOtaPackage>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var target in _config.Targets.Where(target => target.Enabled))
        {
            ValidateTarget(target);
            if (packages.ContainsKey(target.Name))
            {
                throw new InvalidOperationException(
                    $"OTA目标名称重复：{target.Name}。");
            }

            var packagePath = Path.Combine(
                _config.Http.PackageRoot,
                target.PackageFileName);
            if (_config.PreparePackages)
            {
                await PrepareOneAsync(
                    target,
                    packagePath,
                    runDirectory,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (!File.Exists(packagePath))
            {
                throw new FileNotFoundException(
                    $"未生成差分包且HTTP目录中不存在文件：{packagePath}",
                    packagePath);
            }

            var md5 = await ComputeMd5Async(packagePath, cancellationToken)
                .ConfigureAwait(false);
            var fileInfo = new FileInfo(packagePath);
            if (fileInfo.Length == 0)
            {
                throw new InvalidOperationException(
                    $"目标{target.Name}的升级包为空文件。");
            }

            if (target.UpgradeType == 1 && fileInfo.Length > target.MaxPackageBytes)
            {
                throw new InvalidOperationException(
                    $"目标{target.Name}差分包大小{fileInfo.Length}超过限制"
                    + $"{target.MaxPackageBytes}字节。");
            }
            packages.Add(
                target.Name,
                new EcoLinkOtaPackage(
                    target.Name,
                    target.PackageFileName,
                    packagePath,
                    fileInfo.Length,
                    md5,
                    _httpServer.BuildPackageUri(target.PackageFileName)));
        }

        var manifestPath = Path.Combine(runDirectory, "ota-package-manifest.json");
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(packages.Values, JsonOptions),
            cancellationToken).ConfigureAwait(false);
        return packages;
    }

    private async Task PrepareOneAsync(
        EcoLinkOtaTargetConfig target,
        string packagePath,
        string runDirectory,
        CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(
            runDirectory,
            $"{target.Name}-{Guid.NewGuid():N}.tmp");
        try
        {
            if (target.UpgradeType == 0)
            {
                File.Copy(target.NewFirmwarePath, tempPath, overwrite: true);
            }
            else
            {
                var logPath = Path.Combine(
                    runDirectory,
                    $"bsdiff-{SanitizeFileName(target.Name)}.log");
                var exitCode = await RunBsdiffAsync(
                    target.OldFirmwarePath,
                    target.NewFirmwarePath,
                    tempPath,
                    target.IncludeUbootPartition,
                    logPath,
                    cancellationToken).ConfigureAwait(false);
                if (exitCode != 0
                    || !File.Exists(tempPath)
                    || new FileInfo(tempPath).Length == 0)
                {
                    throw new InvalidOperationException(
                        $"目标{target.Name}差分包生成失败，退出码={exitCode}，详见{logPath}。");
                }
            }

            File.Copy(tempPath, packagePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private async Task<int> RunBsdiffAsync(
        string oldFirmware,
        string newFirmware,
        string patchPath,
        bool includeUbootPartition,
        string logPath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _config.Tools.BsdiffPath,
            WorkingDirectory = _config.EcoLinkRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(oldFirmware);
        startInfo.ArgumentList.Add(newFirmware);
        startInfo.ArgumentList.Add(patchPath);
        startInfo.ArgumentList.Add(includeUbootPartition ? "1" : "0");

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        await File.WriteAllTextAsync(
            logPath,
            stdout + stderr,
            cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }

    private void ValidateTarget(EcoLinkOtaTargetConfig target)
    {
        if (string.IsNullOrWhiteSpace(target.Name)
            || string.IsNullOrWhiteSpace(target.DevType)
            || target.UpgradeType is < 0 or > 1
            || target.Range is < 0 or > 1
            || string.IsNullOrWhiteSpace(target.OldVersion)
            || string.IsNullOrWhiteSpace(target.NewVersion)
            || target.MaxPackageBytes <= 0
            || target.TimeoutSeconds <= 0)
        {
            throw new InvalidOperationException(
                $"OTA目标{target.Name}的配置无效或新固件不存在。");
        }

        if (_config.PreparePackages
            && (string.IsNullOrWhiteSpace(target.NewFirmwarePath)
                || !File.Exists(target.NewFirmwarePath)))
        {
            throw new InvalidOperationException(
                $"OTA目标{target.Name}的新固件不存在。");
        }

        if (_config.PreparePackages
            && target.UpgradeType == 1
            && (string.IsNullOrWhiteSpace(target.OldFirmwarePath)
                || !File.Exists(target.OldFirmwarePath)))
        {
            throw new InvalidOperationException(
                $"OTA目标{target.Name}使用差分升级，但旧固件不存在。");
        }

        if (string.IsNullOrWhiteSpace(target.PackageFileName)
            || Path.GetFileName(target.PackageFileName) != target.PackageFileName
            || Encoding.UTF8.GetByteCount(target.PackageFileName) > 36)
        {
            throw new InvalidOperationException(
                $"OTA目标{target.Name}的差分包文件名无效或超过36字节。");
        }

        if (target.Range == 1 && target.DeviceIds.Length == 0)
        {
            throw new InvalidOperationException(
                $"OTA目标{target.Name}为指定设备升级，但DeviceIds为空。");
        }

        if (target.DeviceIds.Any(id => id == 0)
            || target.DeviceIds.Distinct().Count() != target.DeviceIds.Length)
        {
            throw new InvalidOperationException(
                $"OTA目标{target.Name}的DeviceIds包含零值或重复值。");
        }

        if (string.Equals(target.DevType, "node", StringComparison.OrdinalIgnoreCase)
            && (target.SessionId == 0 || target.NodeIds.Length == 0))
        {
            throw new InvalidOperationException(
                $"Node OTA目标{target.Name}必须指定非零SessionId和NodeIds。");
        }


        if (target.NodeIds.Length > 256
            || target.NodeIds.Any(id => id == 0)
            || target.NodeIds.Distinct().Count() != target.NodeIds.Length)
        {
            throw new InvalidOperationException(
                $"OTA目标{target.Name}的NodeIds必须为1至256个唯一非零ID。");
        }

        if (_config.PreparePackages
            && !File.Exists(_config.Tools.BsdiffPath)
            && target.UpgradeType == 1)
        {
            throw new FileNotFoundException(
                "找不到bsdiff命令行工具。",
                _config.Tools.BsdiffPath);
        }

        if (_config.PreparePackages
            && target.UpgradeType == 1
            && !_config.Tools.BsdiffCompatibilityConfirmed)
        {
            throw new InvalidOperationException(
                "尚未确认bsdiff_cmd与桌面OTA_TOOL差分包格式兼容，禁止自动生成差分包。");
        }
    }

    private static async Task<string> ComputeMd5Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await MD5.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
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
