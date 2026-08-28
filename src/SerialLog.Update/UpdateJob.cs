using System.Text.Json;

namespace SerialLog.Update;

public sealed class UpdateJob
{
    public int CurrentProcessId { get; set; }

    public string InstallDirectory { get; set; } = string.Empty;

    public string StagingDirectory { get; set; } = string.Empty;

    public string BackupDirectory { get; set; } = string.Empty;

    public string ApplicationFileName { get; set; } = UpdatePaths.ApplicationFileName;

    public string TargetVersion { get; set; } = string.Empty;

    public string ConfirmationFile { get; set; } = string.Empty;

    public string LogFilePath { get; set; } = string.Empty;

    public string UpdateStateFilePath { get; set; } = string.Empty;
}

public static class UpdateJobStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static UpdateJob Load(string path)
    {
        var job = JsonSerializer.Deserialize<UpdateJob>(File.ReadAllText(path), JsonOptions);
        return job ?? throw new InvalidDataException("更新任务文件内容无效。");
    }

    public static void Save(string path, UpdateJob job)
    {
        var directory = Path.GetDirectoryName(path) ??
            throw new InvalidOperationException("更新任务路径没有父目录。");
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(job, JsonOptions));
    }
}

public static class UpdateStartupConfirmation
{
    private const string ConfirmationArgument = "--update-confirmation-file";

    public static bool TryConfirmFromCommandLine(
        IReadOnlyList<string> arguments,
        string updateRoot,
        out string? error)
    {
        return TryConfirmFromCommandLine(
            arguments,
            [updateRoot],
            out _,
            out error);
    }

    internal static bool TryConfirmFromCommandLine(
        IReadOnlyList<string> arguments,
        IReadOnlyList<string> allowedUpdateRoots,
        out string? confirmedUpdateRoot,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(allowedUpdateRoots);

        confirmedUpdateRoot = null;
        error = null;
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (!string.Equals(arguments[index], ConfirmationArgument, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var confirmationFile = Path.GetFullPath(arguments[index + 1]);
                var matchingRoot = allowedUpdateRoots
                    .Select(Path.GetFullPath)
                    .FirstOrDefault(root => UpdatePaths.IsPathWithin(confirmationFile, root));
                if (matchingRoot is null)
                {
                    error = "更新确认文件不在更新数据目录中。";
                    return false;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(confirmationFile)!);
                File.WriteAllText(confirmationFile, DateTimeOffset.UtcNow.ToString("O"));
                confirmedUpdateRoot = matchingRoot;
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        return false;
    }

    public static string ArgumentName => ConfirmationArgument;
}
