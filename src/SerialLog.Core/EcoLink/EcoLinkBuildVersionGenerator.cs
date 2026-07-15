namespace SerialLog.Core.EcoLink;

public sealed class EcoLinkBuildVersionGenerator
{
    private readonly string _headerPath;
    private readonly Func<DateTimeOffset> _nowProvider;
    private readonly string _prefix;

    public EcoLinkBuildVersionGenerator(
        string prefix,
        string headerPath,
        Func<DateTimeOffset>? nowProvider = null)
    {
        _prefix = string.IsNullOrWhiteSpace(prefix) ? "ecolink" : prefix.Trim();
        _headerPath = headerPath;
        _nowProvider = nowProvider ?? (() => DateTimeOffset.Now);
    }

    public string Generate(int iteration, string versionPath)
    {
        var version = $"{_prefix}-{_nowProvider():yyyyMMdd-HHmmss}-i{iteration:000}";
        EnsureParentDirectory(_headerPath);
        EnsureParentDirectory(versionPath);
        File.WriteAllText(_headerPath, BuildHeader(version));
        File.WriteAllText(versionPath, version + Environment.NewLine);
        return version;
    }

    private static void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static string BuildHeader(string version)
    {
        return string.Join(Environment.NewLine,
        [
            "/**",
            " * @file ecolink_auto_build_version.h",
            " * @brief EcoLink 自动迭代固件版本标识",
            " */",
            "",
            "#ifndef __ECOLINK_AUTO_BUILD_VERSION_H__",
            "#define __ECOLINK_AUTO_BUILD_VERSION_H__",
            "",
            $"#define ECOLINK_AUTO_BUILD_VERSION \"{version}\"",
            "",
            "#endif /* __ECOLINK_AUTO_BUILD_VERSION_H__ */",
            ""
        ]);
    }
}
