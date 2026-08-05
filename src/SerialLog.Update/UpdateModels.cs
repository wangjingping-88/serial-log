namespace SerialLog.Update;

public enum UpdateCheckStatus
{
    Skipped,
    NoUpdate,
    UpdateAvailable,
    Failed
}

public sealed record UpdateAsset(
    string Name,
    Uri DownloadUri,
    long Size,
    string? Digest);

public sealed record UpdateReleaseInfo(
    ReleaseVersion Version,
    string TagName,
    string ReleaseNotes,
    DateTimeOffset? PublishedAt,
    Uri ReleasePageUri,
    UpdateAsset PackageAsset,
    UpdateAsset ChecksumAsset);

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    UpdateReleaseInfo? Release = null,
    string? ErrorMessage = null)
{
    public static UpdateCheckResult Skipped() => new(UpdateCheckStatus.Skipped);

    public static UpdateCheckResult NoUpdate() => new(UpdateCheckStatus.NoUpdate);

    public static UpdateCheckResult Available(UpdateReleaseInfo release) =>
        new(UpdateCheckStatus.UpdateAvailable, release);

    public static UpdateCheckResult Failed(string message) =>
        new(UpdateCheckStatus.Failed, ErrorMessage: message);
}

public sealed record UpdateDownloadProgress(
    string Stage,
    long BytesReceived,
    long? TotalBytes,
    double BytesPerSecond);

public sealed record PreparedUpdate(
    string JobFilePath,
    string UpdaterExecutablePath);

public sealed class UpdateState
{
    public DateTimeOffset? LastSuccessfulCheckUtc { get; set; }

    public DateTimeOffset? LastFailedCheckUtc { get; set; }

    public PendingUpdateState? PendingUpdate { get; set; }
}

public sealed class PendingUpdateState
{
    public string TargetVersion { get; set; } = string.Empty;

    public string JobFilePath { get; set; } = string.Empty;

    public DateTimeOffset PreparedAtUtc { get; set; }
}

public readonly record struct ReleaseVersion(int Major, int Minor, int Patch) : IComparable<ReleaseVersion>
{
    public static bool TryParse(string? value, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        var parts = normalized.Split('.');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out var major) ||
            !int.TryParse(parts[1], out var minor) ||
            !int.TryParse(parts[2], out var patch) ||
            major < 0 || minor < 0 || patch < 0)
        {
            return false;
        }

        version = new ReleaseVersion(major, minor, patch);
        return true;
    }

    public static bool TryParseTag(string? value, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith('v'))
        {
            return false;
        }

        return TryParse(value, out version) &&
            string.Equals(value, $"v{version}", StringComparison.Ordinal);
    }

    public int CompareTo(ReleaseVersion other)
    {
        var majorComparison = Major.CompareTo(other.Major);
        if (majorComparison != 0)
        {
            return majorComparison;
        }

        var minorComparison = Minor.CompareTo(other.Minor);
        return minorComparison != 0 ? minorComparison : Patch.CompareTo(other.Patch);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

public interface IUpdateService
{
    Uri ReleasesPageUri { get; }

    Task<UpdateCheckResult> CheckForUpdatesAsync(
        string currentVersion,
        bool force,
        CancellationToken cancellationToken = default);

    bool CanInstallInPlace(string installDirectory, out string reason);

    Task<PreparedUpdate> DownloadAndPrepareAsync(
        UpdateReleaseInfo release,
        string installDirectory,
        int currentProcessId,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
