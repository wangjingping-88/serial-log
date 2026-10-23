namespace SerialLog.Core.Commands;

public static class CommandFormatter
{
    public static string ApplyLineEnding(string command, LineEnding lineEnding, bool interpretEscapes = true)
    {
        if (interpretEscapes) command = DecodeEscapes(command);
        return command + lineEnding switch
        {
            LineEnding.None => string.Empty,
            LineEnding.Cr => "\r",
            LineEnding.Lf => "\n",
            LineEnding.CrLf => "\r\n",
            _ => throw new ArgumentOutOfRangeException(nameof(lineEnding), lineEnding, "未知换行方式。")
        };
    }

    public static string DecodeEscapes(string command)
    {
        var result = new System.Text.StringBuilder(command.Length);
        for (var i = 0; i < command.Length; i++)
        {
            if (command[i] == '\\' && i + 1 < command.Length)
            {
                var decoded = command[i + 1] switch { 'r' => '\r', 'n' => '\n', 't' => '\t', '\\' => '\\', _ => '\0' };
                if (decoded != '\0') { result.Append(decoded); i++; continue; }
            }
            result.Append(command[i]);
        }
        return result.ToString();
    }
}
