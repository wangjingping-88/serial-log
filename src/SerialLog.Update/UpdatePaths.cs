namespace SerialLog.Update;

public static class UpdatePaths
{
    public const string UpdaterFileName = "SerialLog.Updater.exe";
    public const string ApplicationFileName = "SerialLog.App.exe";
    public const string PortableDataDirectoryName = "data";

    // 更新暂存目录必须位于安装目录之外，否则目录切换时会连同更新助手一起移动。
    public static string DefaultDataRoot => Path.Combine(Path.GetTempPath(), "SerialLog");

    public static string DefaultUpdateRoot => Path.Combine(DefaultDataRoot, "updates");

    public static bool IsPathWithin(string path, string root)
    {
        var fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        return relative == "." ||
            (!relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
             !string.Equals(relative, "..", StringComparison.Ordinal) &&
             !Path.IsPathRooted(relative));
    }

    public static string EnsureSafeFileName(string value)
    {
        foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalidCharacter, '_');
        }

        return value;
    }
}
