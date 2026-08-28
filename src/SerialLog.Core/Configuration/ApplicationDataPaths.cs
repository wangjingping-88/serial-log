namespace SerialLog.Core.Configuration;

public static class ApplicationDataPaths
{
    public const string DataDirectoryName = "data";
    public const string LegacyDataDirectory = @"D:\serial-log-data";

    public static string DataDirectory => GetDataDirectory(AppContext.BaseDirectory);

    public static string WorkspaceFile => Path.Combine(DataDirectory, "workspace.json");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static string CrashLogDirectory => Path.Combine(DataDirectory, "crash-logs");

    public static string LegacyWorkspaceFile => Path.Combine(LegacyDataDirectory, "workspace.json");

    public static string LegacyLogDirectory => Path.Combine(LegacyDataDirectory, "logs");

    public static string GetDataDirectory(string applicationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        return Path.Combine(Path.GetFullPath(applicationDirectory), DataDirectoryName);
    }

    public static bool IsLegacyDefaultLogDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return string.Equals(
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(LegacyLogDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    public static bool MigrateLegacyWorkspaceIfNeeded(
        string? destinationWorkspaceFile = null,
        string? legacyWorkspaceFile = null)
    {
        var destination = destinationWorkspaceFile ?? WorkspaceFile;
        var legacy = legacyWorkspaceFile ?? LegacyWorkspaceFile;
        if (File.Exists(destination) || !File.Exists(legacy))
        {
            return false;
        }

        var destinationDirectory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        File.Copy(legacy, destination, overwrite: false);
        return true;
    }
}
