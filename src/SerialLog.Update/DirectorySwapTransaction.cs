namespace SerialLog.Update;

public sealed class DirectorySwapTransaction
{
    private const int MoveRetryCount = 10;
    private static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(500);
    private readonly string _installDirectory;
    private readonly string _stagingDirectory;
    private readonly string _backupDirectory;
    private bool _applied;

    public DirectorySwapTransaction(
        string installDirectory,
        string stagingDirectory,
        string backupDirectory)
    {
        _installDirectory = Path.GetFullPath(installDirectory);
        _stagingDirectory = Path.GetFullPath(stagingDirectory);
        _backupDirectory = Path.GetFullPath(backupDirectory);
        ValidatePaths();
    }

    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_installDirectory))
        {
            throw new DirectoryNotFoundException($"安装目录不存在：{_installDirectory}");
        }

        if (!Directory.Exists(_stagingDirectory))
        {
            throw new DirectoryNotFoundException($"更新暂存目录不存在：{_stagingDirectory}");
        }

        if (Directory.Exists(_backupDirectory))
        {
            throw new IOException($"更新备份目录已经存在：{_backupDirectory}");
        }

        await MoveDirectoryWithRetryAsync(
            _installDirectory,
            _backupDirectory,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await MoveDirectoryWithRetryAsync(
                _stagingDirectory,
                _installDirectory,
                cancellationToken).ConfigureAwait(false);
            _applied = true;
            MovePortableDataDirectory(_backupDirectory, _installDirectory);
        }
        catch
        {
            if (_applied)
            {
                await RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await MoveDirectoryWithRetryAsync(
                    _backupDirectory,
                    _installDirectory,
                    CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (!_applied || !Directory.Exists(_backupDirectory))
        {
            return;
        }

        var failedDirectory = Directory.Exists(_stagingDirectory)
            ? $"{_stagingDirectory}-failed-{Guid.NewGuid():N}"
            : _stagingDirectory;
        if (Directory.Exists(_installDirectory))
        {
            await MoveDirectoryWithRetryAsync(
                _installDirectory,
                failedDirectory,
                cancellationToken).ConfigureAwait(false);
        }

        MovePortableDataDirectory(failedDirectory, _backupDirectory, allowExistingDestination: true);
        await MoveDirectoryWithRetryAsync(
            _backupDirectory,
            _installDirectory,
            cancellationToken).ConfigureAwait(false);
        _applied = false;
    }

    public void Commit()
    {
        if (!_applied || !Directory.Exists(_backupDirectory))
        {
            return;
        }

        UpdatePackageUtilities.TryDeleteDirectory(_backupDirectory);
        _applied = false;
    }

    private void ValidatePaths()
    {
        var installParent = Directory.GetParent(_installDirectory)?.FullName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(installParent) ||
            !string.Equals(Directory.GetParent(_stagingDirectory)?.FullName, installParent, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Directory.GetParent(_backupDirectory)?.FullName, installParent, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(_stagingDirectory).StartsWith(".serial-log-stage-", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(_backupDirectory).StartsWith(".serial-log-backup-", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("更新暂存、备份和安装目录不符合安全切换规则。");
        }
    }

    private static void MovePortableDataDirectory(
        string sourceRoot,
        string destinationRoot,
        bool allowExistingDestination = false)
    {
        var source = Path.Combine(sourceRoot, UpdatePaths.PortableDataDirectoryName);
        if (!Directory.Exists(source))
        {
            return;
        }

        var destination = Path.Combine(destinationRoot, UpdatePaths.PortableDataDirectoryName);
        if (Directory.Exists(destination))
        {
            if (allowExistingDestination)
            {
                return;
            }

            throw new IOException($"新版安装包不应包含用户数据目录：{destination}");
        }

        Directory.Move(source, destination);
    }

    private static async Task MoveDirectoryWithRetryAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 0; attempt < MoveRetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (IOException exception)
            {
                lastException = exception;
            }
            catch (UnauthorizedAccessException exception)
            {
                lastException = exception;
            }

            await Task.Delay(MoveRetryDelay, cancellationToken).ConfigureAwait(false);
        }

        throw new IOException($"无法将目录从 {source} 移动到 {destination}。", lastException);
    }
}
