using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SerialLog.Update;

public static partial class UpdatePackageUtilities
{
    public static string ParseSha256(string checksumText)
    {
        var match = Sha256Regex().Match(checksumText);
        if (!match.Success)
        {
            throw new InvalidDataException("SHA-256 校验文件格式无效。");
        }

        return match.Value.ToUpperInvariant();
    }

    public static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    public static void ExtractVerifiedZip(string packagePath, string stagingDirectory)
    {
        if (Directory.Exists(stagingDirectory))
        {
            throw new IOException($"更新暂存目录已经存在：{stagingDirectory}");
        }

        Directory.CreateDirectory(stagingDirectory);
        var stagingRoot = Path.GetFullPath(stagingDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            foreach (var entry in archive.Entries)
            {
                var destinationPath = Path.GetFullPath(Path.Combine(stagingDirectory, entry.FullName));
                if (!destinationPath.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"压缩包包含不安全路径：{entry.FullName}");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                entry.ExtractToFile(destinationPath, overwrite: false);
            }

            if (!File.Exists(Path.Combine(stagingDirectory, UpdatePaths.ApplicationFileName)) ||
                !File.Exists(Path.Combine(stagingDirectory, UpdatePaths.UpdaterFileName)))
            {
                throw new InvalidDataException("便携版压缩包缺少主程序或更新助手。");
            }
        }
        catch
        {
            TryDeleteDirectory(stagingDirectory);
            throw;
        }
    }

    internal static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Cleanup failure is non-fatal; the next attempt uses a new unique path.
        }
    }

    [GeneratedRegex("(?i)(?<![0-9a-f])[0-9a-f]{64}(?![0-9a-f])", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();
}
