using System.Text.RegularExpressions;

namespace SerialLog.Core.EcoLink;

public static partial class EcoLinkTestAnalyzer
{
    public static EcoLinkTestAnalysisResult Analyze(
        IReadOnlyDictionary<string, IReadOnlyList<string>> logsByNode,
        EcoLinkTestCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(logsByNode);
        ArgumentNullException.ThrowIfNull(criteria);

        var evidence = new List<string>();
        var allLines = Flatten(logsByNode);
        var failure = FindPhaseFailure(allLines);
        if (failure is not null)
        {
            return new EcoLinkTestAnalysisResult
            {
                IsTerminal = true,
                Stage = $"phase{failure.Phase}",
                Reason = $"第 {failure.Phase} 阶段第 {failure.Round} 轮失败。",
                Evidence = [failure.Line]
            };
        }

        var abnormal = FindAbnormal(allLines, criteria.AbnormalPatterns);
        if (abnormal is not null)
        {
            return new EcoLinkTestAnalysisResult
            {
                IsTerminal = true,
                Stage = "anomaly",
                Reason = "检测到配置的异常日志。",
                Evidence = [abnormal]
            };
        }

        var extenderLines = FindNodeLines(logsByNode, criteria.ExtenderNodeName);
        var registeredNodeCount = CountRegisteredNodes(extenderLines, criteria.ExpectedNodeCount);
        var phaseRounds = ParsePhaseRounds(extenderLines);
        var syncStat = ParseSyncStat(extenderLines);
        var allPhasesPassed = extenderLines.Any(
            line => line.Contains("nb_test: all phases passed", StringComparison.OrdinalIgnoreCase));

        foreach (var phase in criteria.RequiredPhases)
        {
            if (phaseRounds.TryGetValue(phase, out var rounds))
            {
                evidence.Add($"phase{phase} passed {rounds} rounds");
            }
        }

        if (syncStat is not null)
        {
            evidence.Add(
                $"sync tx submit={syncStat.Submit} ok={syncStat.Success} fail={syncStat.Failed}");
        }

        var phasesComplete = criteria.RequiredPhases.All(
            phase => phaseRounds.TryGetValue(phase, out var rounds)
                && rounds >= criteria.ExpectedRoundsPerPhase);
        if (allPhasesPassed && phasesComplete)
        {
            return BuildResult(
                success: true,
                isTerminal: true,
                stage: "done",
                reason: "三阶段测试全部通过。",
                registeredNodeCount,
                phaseRounds,
                syncStat,
                evidence);
        }

        if (allPhasesPassed)
        {
            return BuildResult(
                success: false,
                isTerminal: true,
                stage: "incomplete",
                reason: "出现全部通过标记，但阶段轮数记录不完整。",
                registeredNodeCount,
                phaseRounds,
                syncStat,
                evidence);
        }

        return BuildResult(
            success: false,
            isTerminal: false,
            stage: DetectWaitingStage(phaseRounds),
            reason: "测试仍在运行。",
            registeredNodeCount,
            phaseRounds,
            syncStat,
            evidence);
    }

    public static EcoLinkTestAnalysisResult Timeout(
        IReadOnlyDictionary<string, IReadOnlyList<string>> logsByNode,
        EcoLinkTestCriteria criteria,
        int timeoutSeconds)
    {
        var result = Analyze(logsByNode, criteria);
        if (result.IsTerminal)
        {
            return result;
        }

        var latest = FindNodeLines(logsByNode, criteria.ExtenderNodeName)
            .TakeLast(5)
            .ToArray();
        return result with
        {
            IsTerminal = true,
            Stage = "timeout",
            Reason = $"等待测试完成超过 {timeoutSeconds} 秒。",
            Evidence = latest
        };
    }

    private static EcoLinkTestAnalysisResult BuildResult(
        bool success,
        bool isTerminal,
        string stage,
        string reason,
        int registeredNodeCount,
        IReadOnlyDictionary<int, int> phaseRounds,
        SyncStat? syncStat,
        IReadOnlyList<string> evidence)
    {
        return new EcoLinkTestAnalysisResult
        {
            Success = success,
            IsTerminal = isTerminal,
            Stage = stage,
            Reason = reason,
            RegisteredNodeCount = registeredNodeCount,
            PhaseRounds = phaseRounds,
            SyncSubmitCount = syncStat?.Submit,
            SyncSuccessCount = syncStat?.Success,
            SyncFailedCount = syncStat?.Failed,
            Evidence = evidence
        };
    }

    private static IReadOnlyList<NodeLine> Flatten(
        IReadOnlyDictionary<string, IReadOnlyList<string>> logsByNode)
    {
        return logsByNode
            .SelectMany(pair => pair.Value.Select(line => new NodeLine(pair.Key, line)))
            .ToArray();
    }

    private static IReadOnlyList<string> FindNodeLines(
        IReadOnlyDictionary<string, IReadOnlyList<string>> logsByNode,
        string nodeName)
    {
        var pair = logsByNode.FirstOrDefault(
            item => string.Equals(item.Key, nodeName, StringComparison.OrdinalIgnoreCase));
        return pair.Value ?? [];
    }

    private static PhaseFailure? FindPhaseFailure(IEnumerable<NodeLine> lines)
    {
        foreach (var item in lines)
        {
            var match = PhaseFailureRegex().Match(item.Line);
            if (match.Success)
            {
                return new PhaseFailure(
                    int.Parse(match.Groups["phase"].Value),
                    int.Parse(match.Groups["round"].Value),
                    $"{item.Node}: {item.Line}");
            }
        }

        return null;
    }

    private static string? FindAbnormal(
        IEnumerable<NodeLine> lines,
        IEnumerable<string> patterns)
    {
        var normalizedPatterns = patterns
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .ToArray();
        foreach (var item in lines)
        {
            var pattern = normalizedPatterns.FirstOrDefault(
                value => item.Line.Contains(value, StringComparison.OrdinalIgnoreCase));
            if (pattern is not null)
            {
                return $"{item.Node}: {item.Line}";
            }
        }

        return null;
    }

    private static int CountRegisteredNodes(
        IEnumerable<string> lines,
        int expectedNodeCount)
    {
        var nodeIndexes = new HashSet<int>();
        foreach (var line in lines)
        {
            var match = RegisterRegex().Match(line);
            if (match.Success)
            {
                nodeIndexes.Add(int.Parse(match.Groups["node"].Value));
            }

            if (line.Contains(
                    $"nb_test: {expectedNodeCount} nodes registered",
                    StringComparison.OrdinalIgnoreCase))
            {
                return expectedNodeCount;
            }
        }

        return nodeIndexes.Count;
    }

    private static IReadOnlyDictionary<int, int> ParsePhaseRounds(IEnumerable<string> lines)
    {
        var roundsByPhase = new Dictionary<int, int>();
        foreach (var line in lines)
        {
            var match = PhasePassedRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var phase = int.Parse(match.Groups["phase"].Value);
            var rounds = int.Parse(match.Groups["rounds"].Value);
            roundsByPhase[phase] = Math.Max(roundsByPhase.GetValueOrDefault(phase), rounds);
        }

        return roundsByPhase;
    }

    private static SyncStat? ParseSyncStat(IEnumerable<string> lines)
    {
        SyncStat? latest = null;
        foreach (var line in lines)
        {
            var match = SyncStatRegex().Match(line);
            if (match.Success)
            {
                latest = new SyncStat(
                    int.Parse(match.Groups["submit"].Value),
                    int.Parse(match.Groups["success"].Value),
                    int.Parse(match.Groups["failed"].Value));
            }
        }

        return latest;
    }

    private static string DetectWaitingStage(IReadOnlyDictionary<int, int> phaseRounds)
    {
        if (phaseRounds.ContainsKey(2))
        {
            return "phase3";
        }

        if (phaseRounds.ContainsKey(1))
        {
            return "phase2";
        }

        return "phase1";
    }

    [GeneratedRegex(@"nb_test:\s*FAIL\s+phase(?<phase>\d+)\s+round(?<round>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PhaseFailureRegex();

    [GeneratedRegex(@"nb_test:\s*register\s+node(?<node>\d+)=", RegexOptions.IgnoreCase)]
    private static partial Regex RegisterRegex();

    [GeneratedRegex(@"nb_test:\s*phase(?<phase>\d+)\s+passed\s+(?<rounds>\d+)\s+rounds", RegexOptions.IgnoreCase)]
    private static partial Regex PhasePassedRegex();

    [GeneratedRegex(@"nb_test:\s*sync tx submit=(?<submit>\d+)\s+ok=(?<success>\d+)\s+fail=(?<failed>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SyncStatRegex();

    private sealed record NodeLine(string Node, string Line);

    private sealed record PhaseFailure(int Phase, int Round, string Line);

    private sealed record SyncStat(int Submit, int Success, int Failed);
}
