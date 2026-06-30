namespace SerialLog.Core.Commands;

public static class CommandGroupExecutor
{
    public static async Task<CommandGroupExecutionResult> ExecuteAsync(
        CommandGroup group,
        IReadOnlyList<ICommandTarget> targets,
        CancellationToken cancellationToken)
    {
        var targetMap = targets.ToDictionary(target => target.Id);
        var steps = new List<CommandSendStep>();

        for (var commandIndex = 0; commandIndex < group.Commands.Count; commandIndex++)
        {
            var command = group.Commands[commandIndex];
            var payload = CommandFormatter.ApplyLineEnding(command, group.LineEnding);

            foreach (var targetId in group.TargetIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!targetMap.TryGetValue(targetId, out var target) || !target.IsConnected)
                {
                    steps.Add(new CommandSendStep(targetId, command, CommandSendStatus.SkippedDisconnected, null));
                    continue;
                }

                try
                {
                    await target.SendAsync(payload, cancellationToken).ConfigureAwait(false);
                    steps.Add(new CommandSendStep(targetId, command, CommandSendStatus.Sent, null));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    steps.Add(new CommandSendStep(targetId, command, CommandSendStatus.Failed, ex.Message));
                }
            }

            if (group.Delay > TimeSpan.Zero && commandIndex < group.Commands.Count - 1)
            {
                await Task.Delay(group.Delay, cancellationToken).ConfigureAwait(false);
            }
        }

        return new CommandGroupExecutionResult(steps);
    }
}

public sealed record CommandGroupExecutionResult(IReadOnlyList<CommandSendStep> Steps)
{
    public int SentCount => Steps.Count(step => step.Status == CommandSendStatus.Sent);

    public int SkippedCount => Steps.Count(step => step.Status == CommandSendStatus.SkippedDisconnected);

    public int FailedCount => Steps.Count(step => step.Status == CommandSendStatus.Failed);
}

public sealed record CommandSendStep(string TargetId, string Command, CommandSendStatus Status, string? ErrorMessage);

public enum CommandSendStatus
{
    Sent,
    SkippedDisconnected,
    Failed
}
