using SerialLog.Core.Tdma;

namespace SerialLog.Cli;

public sealed class TdmaBuildVersionGenerator
{
    private readonly string _prefix;
    private readonly string _headerPath;
    private readonly string _versionPath;
    private readonly Func<DateTimeOffset> _nowProvider;

    public TdmaBuildVersionGenerator(
        string prefix,
        string headerPath,
        string versionPath,
        Func<DateTimeOffset>? nowProvider = null)
    {
        _prefix = string.IsNullOrWhiteSpace(prefix) ? "tdma" : prefix.Trim();
        _headerPath = headerPath;
        _versionPath = versionPath;
        _nowProvider = nowProvider ?? (() => DateTimeOffset.Now);
    }

    public string Generate(int iteration)
    {
        var version = $"{_prefix}-{_nowProvider():yyyyMMdd-HHmmss}-i{iteration:000}";
        var headerDirectory = Path.GetDirectoryName(Path.GetFullPath(_headerPath));
        var versionDirectory = Path.GetDirectoryName(Path.GetFullPath(_versionPath));

        if (!string.IsNullOrWhiteSpace(headerDirectory))
        {
            Directory.CreateDirectory(headerDirectory);
        }

        if (!string.IsNullOrWhiteSpace(versionDirectory))
        {
            Directory.CreateDirectory(versionDirectory);
        }

        File.WriteAllText(_headerPath, BuildHeader(version));
        File.WriteAllText(_versionPath, version + Environment.NewLine);
        return version;
    }

    private static string BuildHeader(string version)
    {
        return string.Join(Environment.NewLine,
        [
            "#ifndef __TDMA_AUTO_BUILD_VERSION_H__",
            "#define __TDMA_AUTO_BUILD_VERSION_H__",
            "",
            $"#define TDMA_AUTO_BUILD_VERSION \"{version}\"",
            "",
            "#endif /* __TDMA_AUTO_BUILD_VERSION_H__ */",
            ""
        ]);
    }
}

public static class TdmaVersionConfirmAnalyzer
{
    public static TdmaLoopAnalysisResult Analyze(
        IEnumerable<string> nodeNames,
        IReadOnlyDictionary<string, IReadOnlyList<string>> responses,
        string responsePrefix,
        string expectedVersion)
    {
        var missing = new List<string>();
        var evidence = new List<string>();

        foreach (var nodeName in nodeNames)
        {
            if (!responses.TryGetValue(nodeName, out var lines)
                || !lines.Any(line => ContainsVersion(line, responsePrefix, expectedVersion)))
            {
                missing.Add(nodeName);
                var latest = lines is null ? "<no response>" : string.Join(" | ", lines.TakeLast(3));
                evidence.Add($"{nodeName}: {latest}");
            }
        }

        if (0 == missing.Count)
        {
            return new TdmaLoopAnalysisResult
            {
                Success = true,
                Stage = "version_confirm",
                Reason = $"All nodes report build version {expectedVersion}."
            };
        }

        return new TdmaLoopAnalysisResult
        {
            Success = false,
            Stage = "version_confirm",
            Reason = "Some nodes did not report expected build version.",
            Evidence = evidence
        };
    }

    private static bool ContainsVersion(string line, string responsePrefix, string expectedVersion)
    {
        return line.Contains(responsePrefix, StringComparison.OrdinalIgnoreCase)
            && line.Contains(expectedVersion, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class TdmaAbnormalPatternMatcher
{
    private readonly string[] _patterns;

    public TdmaAbnormalPatternMatcher(IEnumerable<string> patterns)
    {
        _patterns = patterns
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Select(pattern => pattern.Trim())
            .ToArray();
    }

    public bool TryMatch(string nodeName, string line, out string evidence)
    {
        foreach (var pattern in _patterns)
        {
            if (line.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                evidence = $"{nodeName}: {line}";
                return true;
            }
        }

        evidence = string.Empty;
        return false;
    }
}
