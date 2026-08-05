using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace SerialLog.Update;

public sealed class UpdateService : IUpdateService, IDisposable
{
    public static readonly Uri DefaultReleasesPageUri =
        new("https://github.com/wangjingping-88/serial-log/releases");

    private static readonly TimeSpan SuccessfulCheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan FailedCheckInterval = TimeSpan.FromHours(2);
    private readonly HttpClient _httpClient;
    private readonly GitHubReleaseClient _releaseClient;
    private readonly UpdateStateStore _stateStore;
    private readonly string _updateRoot;
    private readonly string _applicationDirectory;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private readonly bool _ownsHttpClient;

    public UpdateService(
        string updateRoot,
        string applicationDirectory,
        HttpClient? httpClient = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _updateRoot = Path.GetFullPath(updateRoot);
        _applicationDirectory = Path.GetFullPath(applicationDirectory);
        _httpClient = httpClient ?? CreateHttpClient();
        _ownsHttpClient = httpClient is null;
        _releaseClient = new GitHubReleaseClient(_httpClient);
        _stateStore = new UpdateStateStore(Path.Combine(_updateRoot, "update-state.json"));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public Uri ReleasesPageUri => DefaultReleasesPageUri;

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(
        string currentVersion,
        bool force,
        CancellationToken cancellationToken = default)
    {
        await _checkLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = _stateStore.Load();
            var now = _utcNow();
            if (!force && ShouldSkipCheck(state, now))
            {
                return UpdateCheckResult.Skipped();
            }

            if (!ReleaseVersion.TryParse(currentVersion, out var installedVersion))
            {
                return RecordFailure(state, now, $"当前应用版本格式无效：{currentVersion}");
            }

            try
            {
                var latest = await _releaseClient.GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
                if (latest.IsDraft || latest.IsPrerelease ||
                    !ReleaseVersion.TryParseTag(latest.TagName, out var latestVersion))
                {
                    return RecordFailure(state, now, "GitHub 最新 Release 不是有效的正式版本。");
                }

                if (latestVersion.CompareTo(installedVersion) <= 0)
                {
                    RecordSuccessfulCheck(state, now);
                    return UpdateCheckResult.NoUpdate();
                }

                var normalizedVersion = latestVersion.ToString();
                var packageName = $"SerialLog-v{normalizedVersion}-win-x64-portable.zip";
                var checksumName = $"{packageName}.sha256.txt";
                var package = latest.Assets.FirstOrDefault(asset =>
                    string.Equals(asset.Name, packageName, StringComparison.Ordinal));
                var checksum = latest.Assets.FirstOrDefault(asset =>
                    string.Equals(asset.Name, checksumName, StringComparison.Ordinal));

                if (package is null || checksum is null ||
                    !Uri.TryCreate(package.BrowserDownloadUrl, UriKind.Absolute, out var packageUri) ||
                    !Uri.TryCreate(checksum.BrowserDownloadUrl, UriKind.Absolute, out var checksumUri) ||
                    !Uri.TryCreate(latest.HtmlUrl, UriKind.Absolute, out var releasePageUri) ||
                    !HasValidSha256Digest(package.Digest) ||
                    !HasValidSha256Digest(checksum.Digest))
                {
                    return RecordFailure(state, now, "新版本发布资源不完整，请打开 GitHub Release 手动下载。");
                }

                RecordSuccessfulCheck(state, now);
                return UpdateCheckResult.Available(new UpdateReleaseInfo(
                    latestVersion,
                    latest.TagName,
                    latest.Body ?? "本次发布未提供更新说明。",
                    latest.PublishedAt,
                    releasePageUri,
                    new UpdateAsset(package.Name, packageUri, package.Size, package.Digest),
                    new UpdateAsset(checksum.Name, checksumUri, checksum.Size, checksum.Digest)));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return RecordFailure(state, now, exception.Message);
            }
        }
        finally
        {
            _checkLock.Release();
        }
    }

    public bool CanInstallInPlace(string installDirectory, out string reason)
    {
        try
        {
            var target = Path.GetFullPath(installDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var targetRoot = Path.GetPathRoot(target);
            if (string.IsNullOrWhiteSpace(targetRoot) ||
                string.Equals(target, targetRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                reason = "应用位于磁盘根目录，不能安全执行目录切换。";
                return false;
            }

            if (UpdatePaths.IsPathWithin(target, UpdatePaths.DefaultDataRoot))
            {
                reason = "应用位于 Serial Log 数据目录中，为避免影响日志数据，请从 GitHub Release 手动更新。";
                return false;
            }

            if (!File.Exists(Path.Combine(target, UpdatePaths.ApplicationFileName)))
            {
                reason = "当前目录不是有效的 Serial Log 便携版目录。";
                return false;
            }

            if (!File.Exists(Path.Combine(_applicationDirectory, UpdatePaths.UpdaterFileName)))
            {
                reason = "当前版本未包含独立更新助手，请从 GitHub Release 手动更新一次。";
                return false;
            }

            var parent = Directory.GetParent(target)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) ||
                !string.Equals(Path.GetPathRoot(parent), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase))
            {
                reason = "无法为当前安装目录创建同磁盘更新暂存区。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            reason = exception.Message;
            return false;
        }
    }

    public async Task<PreparedUpdate> DownloadAndPrepareAsync(
        UpdateReleaseInfo release,
        string installDirectory,
        int currentProcessId,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!CanInstallInPlace(installDirectory, out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        var target = Path.GetFullPath(installDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var targetParent = Directory.GetParent(target)?.FullName ??
            throw new InvalidOperationException("安装目录没有有效的父目录。");
        VerifyDirectoryWritable(targetParent);

        var versionName = UpdatePaths.EnsureSafeFileName(release.Version.ToString());
        var downloadDirectory = Path.Combine(_updateRoot, "downloads", versionName);
        Directory.CreateDirectory(downloadDirectory);
        var packagePath = Path.Combine(downloadDirectory, release.PackageAsset.Name);
        var checksumPath = Path.Combine(downloadDirectory, release.ChecksumAsset.Name);

        await DownloadFileAsync(
            release.ChecksumAsset,
            checksumPath,
            "正在下载校验文件",
            progress,
            cancellationToken).ConfigureAwait(false);
        await ValidateDownloadedAssetAsync(
            release.ChecksumAsset,
            checksumPath,
            cancellationToken).ConfigureAwait(false);
        var checksumText = await File.ReadAllTextAsync(checksumPath, cancellationToken).ConfigureAwait(false);
        var expectedHash = UpdatePackageUtilities.ParseSha256(checksumText);

        await DownloadFileAsync(
            release.PackageAsset,
            packagePath,
            "正在下载更新包",
            progress,
            cancellationToken).ConfigureAwait(false);

        progress?.Report(new UpdateDownloadProgress("正在校验更新包", release.PackageAsset.Size, release.PackageAsset.Size, 0));
        var actualHash = await ValidateDownloadedAssetAsync(
            release.PackageAsset,
            packagePath,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(packagePath);
            throw new InvalidDataException("更新包 SHA-256 校验失败，下载文件已删除。");
        }

        var actualLength = new FileInfo(packagePath).Length;
        var token = Guid.NewGuid().ToString("N");
        var stagingDirectory = Path.Combine(targetParent, $".serial-log-stage-{token}");
        var backupDirectory = Path.Combine(targetParent, $".serial-log-backup-{token}");
        progress?.Report(new UpdateDownloadProgress("正在准备更新文件", actualLength, actualLength, 0));
        await Task.Run(
            () => UpdatePackageUtilities.ExtractVerifiedZip(packagePath, stagingDirectory),
            cancellationToken).ConfigureAwait(false);

        var jobDirectory = Path.Combine(_updateRoot, "jobs", token);
        var updaterRuntimeDirectory = Path.Combine(_updateRoot, "runtime", token);
        try
        {
            var confirmationFile = Path.Combine(jobDirectory, "startup-confirmed.txt");
            var jobFile = Path.Combine(jobDirectory, "update-job.json");
            Directory.CreateDirectory(updaterRuntimeDirectory);
            var updaterExecutable = Path.Combine(updaterRuntimeDirectory, UpdatePaths.UpdaterFileName);
            File.Copy(
                Path.Combine(_applicationDirectory, UpdatePaths.UpdaterFileName),
                updaterExecutable,
                overwrite: true);

            UpdateJobStore.Save(jobFile, new UpdateJob
            {
                CurrentProcessId = currentProcessId,
                InstallDirectory = target,
                StagingDirectory = stagingDirectory,
                BackupDirectory = backupDirectory,
                ApplicationFileName = UpdatePaths.ApplicationFileName,
                TargetVersion = release.Version.ToString(),
                ConfirmationFile = confirmationFile,
                LogFilePath = Path.Combine(_updateRoot, "updater.log"),
                UpdateStateFilePath = Path.Combine(_updateRoot, "update-state.json")
            });

            var state = _stateStore.Load();
            state.PendingUpdate = new PendingUpdateState
            {
                TargetVersion = release.Version.ToString(),
                JobFilePath = jobFile,
                PreparedAtUtc = _utcNow()
            };
            _stateStore.Save(state);

            return new PreparedUpdate(jobFile, updaterExecutable);
        }
        catch
        {
            UpdatePackageUtilities.TryDeleteDirectory(stagingDirectory);
            UpdatePackageUtilities.TryDeleteDirectory(jobDirectory);
            UpdatePackageUtilities.TryDeleteDirectory(updaterRuntimeDirectory);
            throw;
        }
    }

    public bool TryConfirmStartedUpdate(
        IReadOnlyList<string> arguments,
        out string? error)
    {
        if (!UpdateStartupConfirmation.TryConfirmFromCommandLine(arguments, _updateRoot, out error))
        {
            return false;
        }

        try
        {
            _stateStore.ClearPendingUpdate();
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }

        return true;
    }

    public void Dispose()
    {
        _checkLock.Dispose();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static bool ShouldSkipCheck(UpdateState state, DateTimeOffset now)
    {
        return state.LastSuccessfulCheckUtc is { } successful && now - successful < SuccessfulCheckInterval ||
            state.LastFailedCheckUtc is { } failed && now - failed < FailedCheckInterval;
    }

    private UpdateCheckResult RecordFailure(UpdateState state, DateTimeOffset now, string message)
    {
        state.LastFailedCheckUtc = now;
        TrySaveState(state);
        TryLogCheckFailure(now, message);
        return UpdateCheckResult.Failed(message);
    }

    private void RecordSuccessfulCheck(UpdateState state, DateTimeOffset now)
    {
        state.LastSuccessfulCheckUtc = now;
        state.LastFailedCheckUtc = null;
        TrySaveState(state);
    }

    private void TrySaveState(UpdateState state)
    {
        try
        {
            _stateStore.Save(state);
        }
        catch
        {
            // A read-only state directory must not prevent a manual update check.
        }
    }

    private async Task DownloadFileAsync(
        UpdateAsset asset,
        string destinationPath,
        string stage,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var temporaryPath = $"{destinationPath}.download";
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }

        try
        {
            using var request = CreateDownloadRequest(asset.DownloadUri);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ??
                (asset.Size > 0 ? asset.Size : null);
            {
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var destination = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                var buffer = new byte[128 * 1024];
                var stopwatch = Stopwatch.StartNew();
                long bytesReceived = 0;
                while (true)
                {
                    var count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (count == 0)
                    {
                        break;
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    bytesReceived += count;
                    var speed = stopwatch.Elapsed.TotalSeconds > 0
                        ? bytesReceived / stopwatch.Elapsed.TotalSeconds
                        : 0;
                    progress?.Report(new UpdateDownloadProgress(stage, bytesReceived, totalBytes, speed));
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }

    private static HttpRequestMessage CreateDownloadRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("SerialLog-Updater/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        return request;
    }

    private static bool HasValidSha256Digest(string? digest)
    {
        const string prefix = "sha256:";
        return digest is { Length: 71 } &&
            digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            digest[prefix.Length..].All(Uri.IsHexDigit);
    }

    private static async Task<string> ValidateDownloadedAssetAsync(
        UpdateAsset asset,
        string path,
        CancellationToken cancellationToken)
    {
        var actualLength = new FileInfo(path).Length;
        if (asset.Size <= 0 || actualLength != asset.Size)
        {
            File.Delete(path);
            throw new InvalidDataException(
                $"资源 {asset.Name} 大小不匹配，预期 {asset.Size} 字节，实际 {actualLength} 字节。");
        }

        var actualHash = await UpdatePackageUtilities.ComputeSha256Async(path, cancellationToken)
            .ConfigureAwait(false);
        if (!HasValidSha256Digest(asset.Digest) ||
            !string.Equals(asset.Digest!["sha256:".Length..], actualHash, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidDataException($"资源 {asset.Name} 的 GitHub SHA-256 摘要校验失败。");
        }

        return actualHash;
    }

    private void TryLogCheckFailure(DateTimeOffset timestamp, string message)
    {
        try
        {
            Directory.CreateDirectory(_updateRoot);
            File.AppendAllText(
                Path.Combine(_updateRoot, "update-check.log"),
                $"[{timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private static void VerifyDirectoryWritable(string directory)
    {
        var probe = Path.Combine(directory, $".serial-log-write-{Guid.NewGuid():N}.tmp");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }
        }
        catch (Exception exception)
        {
            throw new UnauthorizedAccessException("安装目录不可写，无法执行自动更新。", exception);
        }
        finally
        {
            if (File.Exists(probe))
            {
                File.Delete(probe);
            }
        }
    }
}
