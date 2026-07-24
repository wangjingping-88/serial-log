using System.Text;
using SerialLog.Core.EcoLink;

namespace SerialLog.Cli;

public sealed record EcoLinkLoopIterationRecord(
    int Iteration,
    string Directory,
    EcoLinkTestAnalysisResult Result);

public static class EcoLinkLoopSummary
{
    public static string Build(
        IReadOnlyList<EcoLinkLoopIterationRecord> records,
        int maxIterations,
        bool isFinal)
    {
        var builder = new StringBuilder();
        var latest = records.LastOrDefault();

        builder.AppendLine(isFinal ? "# EcoLink 自动迭代最终摘要" : "# EcoLink 自动迭代阶段摘要");
        builder.AppendLine();
        builder.AppendLine($"已迭代: {records.Count}/{maxIterations}");
        builder.AppendLine($"成功: {records.Count(item => item.Result.Success)}");
        builder.AppendLine($"失败: {records.Count(item => item.Result.IsTerminal && !item.Result.Success)}");
        builder.AppendLine();
        builder.AppendLine("## 阶段分布");
        foreach (var group in records
            .GroupBy(item => item.Result.Stage)
            .OrderByDescending(group => group.Count()))
        {
            builder.AppendLine($"- {group.Key}: {group.Count()}");
        }

        if (latest is null)
        {
            return builder.ToString();
        }

        builder.AppendLine();
        builder.AppendLine("## 最近一轮");
        builder.AppendLine($"- 轮次: {latest.Iteration}");
        builder.AppendLine($"- 目录: {latest.Directory}");
        builder.AppendLine($"- 阶段: {latest.Result.Stage}");
        builder.AppendLine($"- 原因: {latest.Result.Reason}");
        builder.AppendLine($"- 注册节点: {latest.Result.RegisteredNodeCount}");
        if (latest.Result.SyncSubmitCount is not null)
        {
            builder.AppendLine(
                $"- 同步发送: submit={latest.Result.SyncSubmitCount} "
                + $"ok={latest.Result.SyncSuccessCount} "
                + $"fail={latest.Result.SyncFailedCount}");
        }

        if (latest.Result.Evidence.Count > 0)
        {
            builder.AppendLine("- 证据:");
            foreach (var line in latest.Result.Evidence.Take(8))
            {
                builder.AppendLine($"  - {line}");
            }
        }

        return builder.ToString();
    }
}
