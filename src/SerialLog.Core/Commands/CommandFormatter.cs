namespace SerialLog.Core.Commands;

public static class CommandFormatter
{
    public static string ApplyLineEnding(string command, LineEnding lineEnding)
    {
        return command + lineEnding switch
        {
            LineEnding.None => string.Empty,
            LineEnding.Cr => "\r",
            LineEnding.Lf => "\n",
            LineEnding.CrLf => "\r\n",
            _ => throw new ArgumentOutOfRangeException(nameof(lineEnding), lineEnding, "未知换行方式。")
        };
    }
}
