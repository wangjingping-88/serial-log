using System.Text;
using SerialLog.Core.Tdma;

namespace SerialLog.Cli;

public sealed record TdmaLoopIterationRecord(
    int Iteration,
    string Directory,
    TdmaLoopAnalysisResult Result);

public static class TdmaLoopSummary
{
    public static string Build(
        IReadOnlyList<TdmaLoopIterationRecord> records,
        int maxIterations,
        bool isFinal)
    {
        var builder = new StringBuilder();
        var total = records.Count;
        var success = records.Count(item => item.Result.Success);
        var latest = records.LastOrDefault();

        builder.AppendLine(isFinal ? "# TDMA 自动迭代最终摘要" : "# TDMA 自动迭代阶段摘要");
        builder.AppendLine();
        builder.AppendLine($"已迭代: {total}/{maxIterations}");
        builder.AppendLine($"成功: {success}");
        builder.AppendLine($"失败: {total - success}");
        builder.AppendLine();
        builder.AppendLine("## 阶段分布");

        foreach (var group in records
            .GroupBy(item => item.Result.Stage)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"- {group.Key}: {group.Count()}");
        }

        if (latest is not null)
        {
            builder.AppendLine();
            builder.AppendLine("## 最近一轮");
            builder.AppendLine($"- 轮次: {latest.Iteration}");
            builder.AppendLine($"- 目录: {latest.Directory}");
            builder.AppendLine($"- 最近失败: {latest.Result.Stage}");
            builder.AppendLine($"- 原因: {latest.Result.Reason}");

            if (latest.Result.Evidence.Count > 0)
            {
                builder.AppendLine("- 证据:");
                foreach (var item in latest.Result.Evidence.Take(5))
                {
                    builder.AppendLine($"  - {item}");
                }
            }
        }

        var syncLostEvidence = records
            .SelectMany(item => item.Result.Evidence)
            .FirstOrDefault(item => item.Contains("TDMA_SYNC_LOST", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(syncLostEvidence))
        {
            builder.AppendLine();
            builder.AppendLine("## 同步异常证据");
            builder.AppendLine($"- {syncLostEvidence}");
        }

        return builder.ToString();
    }
}
