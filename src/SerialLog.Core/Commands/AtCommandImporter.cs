namespace SerialLog.Core.Commands;

using System.Text.RegularExpressions;

public static class AtCommandImporter
{
    private static readonly Regex ExportRegex = new(
        "AT_CMD_EXPORT\\s*\\(\\s*\"(?<name>[^\"]+)\"\\s*,\\s*(?:\"(?<args>[^\"]*)\"|RT_NULL|NULL)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex LogCommandRegex = new(
        "\\bAT(?:[+&][A-Z0-9_]+)?(?:=[^\\s]+)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyList<string> Import(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".c", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".h", StringComparison.OrdinalIgnoreCase))
        {
            return ImportFromCSource(File.ReadAllText(path));
        }

        return ImportPlainTextFile(path);
    }

    public static IReadOnlyList<string> ImportFromText(string text)
    {
        var commands = new List<string>();
        foreach (var line in NormalizeLineBreaks(text).Split('\n'))
        {
            var cleanLine = StripTimestamp(line.Trim());
            var match = LogCommandRegex.Match(cleanLine);
            if (match.Success)
            {
                commands.Add(match.Value);
            }
        }

        return DistinctInOrder(commands);
    }

    public static IReadOnlyList<string> AppendDistinct(IEnumerable<string> existingCommands, IEnumerable<string> newCommands)
    {
        return AppendDistinct(existingCommands, newCommands, _ => AtCommandConflictChoice.KeepExisting);
    }

    public static IReadOnlyList<string> AppendDistinct(
        IEnumerable<string> existingCommands,
        IEnumerable<string> newCommands,
        Func<AtCommandConflict, AtCommandConflictChoice> resolveConflict)
    {
        var result = new List<string>();
        var fullCommandIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var commandNameIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var command in existingCommands)
        {
            AddOrUpdateCommand(command, result, fullCommandIndexes, commandNameIndexes);
        }

        foreach (var command in newCommands)
        {
            var trimmed = command.Trim();
            if (trimmed.Length == 0 || fullCommandIndexes.ContainsKey(trimmed))
            {
                continue;
            }

            var commandName = GetCommandName(trimmed);
            if (commandNameIndexes.TryGetValue(commandName, out var existingIndex))
            {
                var existing = result[existingIndex];
                var choice = resolveConflict(new AtCommandConflict(commandName, existing, trimmed));
                if (choice == AtCommandConflictChoice.UseNew)
                {
                    fullCommandIndexes.Remove(existing);
                    result[existingIndex] = trimmed;
                    fullCommandIndexes[trimmed] = existingIndex;
                }

                continue;
            }

            AddOrUpdateCommand(trimmed, result, fullCommandIndexes, commandNameIndexes);
        }

        return result;
    }

    private static IReadOnlyList<string> ImportPlainTextFile(string path)
    {
        return DistinctInOrder(NormalizeLineBreaks(File.ReadAllText(path))
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#')));
    }

    private static string NormalizeLineBreaks(string text)
    {
        return text.Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Replace("\\r\\n", "\n")
            .Replace("\\n", "\n")
            .Replace("\\r", "\n");
    }

    private static IReadOnlyList<string> ImportFromCSource(string source)
    {
        var sourceWithoutComments = StripCComments(source);
        var commands = ExportRegex.Matches(sourceWithoutComments)
            .Select(match => BuildCommand(match.Groups["name"].Value, match.Groups["args"].Value));

        return DistinctInOrder(commands);
    }

    private static string BuildCommand(string name, string args)
    {
        return string.IsNullOrWhiteSpace(args) ? name : name + args.Trim();
    }

    private static string StripCComments(string source)
    {
        var withoutBlockComments = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlockComments, @"//.*?$", string.Empty, RegexOptions.Multiline);
    }

    private static string StripTimestamp(string line)
    {
        return Regex.Replace(line, @"^\[[^\]]+\]\s*", string.Empty);
    }

    private static IReadOnlyList<string> DistinctInOrder(IEnumerable<string> commands)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var command in commands)
        {
            var trimmed = command.Trim();
            if (trimmed.Length == 0 || !seen.Add(trimmed))
            {
                continue;
            }

            result.Add(trimmed);
        }

        return result;
    }

    private static void AddOrUpdateCommand(
        string command,
        List<string> result,
        Dictionary<string, int> fullCommandIndexes,
        Dictionary<string, int> commandNameIndexes)
    {
        var trimmed = command.Trim();
        if (trimmed.Length == 0 || fullCommandIndexes.ContainsKey(trimmed))
        {
            return;
        }

        var index = result.Count;
        result.Add(trimmed);
        fullCommandIndexes[trimmed] = index;
        commandNameIndexes.TryAdd(GetCommandName(trimmed), index);
    }

    private static string GetCommandName(string command)
    {
        var delimiterIndex = command.IndexOfAny(['=', '<', ' ', '\t']);
        return delimiterIndex < 0 ? command : command[..delimiterIndex];
    }
}

public sealed record AtCommandConflict(string CommandName, string ExistingCommand, string NewCommand);

public enum AtCommandConflictChoice
{
    KeepExisting,
    UseNew
}
