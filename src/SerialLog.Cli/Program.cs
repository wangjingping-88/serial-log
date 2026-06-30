using System.Text.Json;
using SerialLog.Core.Tdma;

namespace SerialLog.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        return await MainAsync(args).ConfigureAwait(false);
    }

    public static async Task<int> MainAsync(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintUsage();
            return 1;
        }

        return args[0] switch
        {
            "tdma-analyze" => await RunTdmaAnalyzeAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
            "tdma-loop" => await RunTdmaLoopAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
            _ => Unknown(args[0])
        };
    }

    private static async Task<int> RunTdmaLoopAsync(string[] args)
    {
        var options = ParseOptions(args);
        if (!options.TryGetValue("--config", out var configPath))
        {
            Console.Error.WriteLine("tdma-loop requires --config.");
            PrintUsage();
            return 1;
        }

        if (!File.Exists(configPath))
        {
            Console.Error.WriteLine($"Config file not found: {configPath}");
            return 1;
        }

        var json = await File.ReadAllTextAsync(configPath).ConfigureAwait(false);
        var config = JsonSerializer.Deserialize<TdmaLoopRunConfig>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (config is null)
        {
            Console.Error.WriteLine($"Invalid config file: {configPath}");
            return 1;
        }

        var runner = new TdmaLoopRunner(config);
        return await runner.RunAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<int> RunTdmaAnalyzeAsync(string[] args)
    {
        var options = ParseOptions(args);
        if (!options.TryGetValue("--log-dir", out var logDir)
            || !options.TryGetValue("--center", out var center)
            || !options.TryGetValue("--target", out var target))
        {
            Console.Error.WriteLine("tdma-analyze requires --log-dir, --center and --target.");
            PrintUsage();
            return 1;
        }

        if (!Directory.Exists(logDir))
        {
            Console.Error.WriteLine($"Log directory not found: {logDir}");
            return 1;
        }

        var criteria = new TdmaLoopCriteria
        {
            CenterAddress = center,
            TargetAddress = target,
            CenterNodeName = options.GetValueOrDefault("--center-node", "center"),
            TargetNodeName = options.GetValueOrDefault("--target-node", "R4")
        };

        if (options.TryGetValue("--data-slot", out var dataSlot) && int.TryParse(dataSlot, out var parsedDataSlot))
        {
            criteria.ExpectedTargetDataSlot = parsedDataSlot;
        }

        if (options.TryGetValue("--ack-tx-slot", out var ackTxSlot) && int.TryParse(ackTxSlot, out var parsedAckTxSlot))
        {
            criteria.ExpectedTargetAckSlot = parsedAckTxSlot;
        }

        if (options.TryGetValue("--ack-rx-slot", out var ackRxSlot) && int.TryParse(ackRxSlot, out var parsedAckRxSlot))
        {
            criteria.ExpectedCenterAckSlot = parsedAckRxSlot;
        }

        var events = await ReadLogEventsAsync(logDir).ConfigureAwait(false);
        var result = TdmaLoopAnalyzer.Analyze(events, criteria);
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(result, jsonOptions);

        if (options.TryGetValue("--out", out var outPath))
        {
            var outDirectory = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrWhiteSpace(outDirectory))
            {
                Directory.CreateDirectory(outDirectory);
            }

            await File.WriteAllTextAsync(outPath, json).ConfigureAwait(false);
        }

        Console.WriteLine(json);
        return result.Success ? 0 : 2;
    }

    private static async Task<IReadOnlyList<TdmaLogEvent>> ReadLogEventsAsync(string logDir)
    {
        var events = new List<TdmaLogEvent>();
        var files = Directory.EnumerateFiles(logDir, "*.log", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var nodeName = GuessNodeName(file);
            var lines = await File.ReadAllLinesAsync(file).ConfigureAwait(false);
            events.AddRange(TdmaLogParser.ParseLines(nodeName, lines));
        }

        return events;
    }

    private static string GuessNodeName(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var marker = name.IndexOf("_202", StringComparison.OrdinalIgnoreCase);
        return marker > 0 ? name[..marker] : name;
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var key = args[index];
            if (!key.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options[key] = string.Empty;
                continue;
            }

            options[key] = args[index + 1];
            index++;
        }

        return options;
    }

    private static bool IsHelp(string value)
    {
        return string.Equals(value, "-h", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "--help", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "help", StringComparison.OrdinalIgnoreCase);
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  SerialLog.Cli tdma-analyze --log-dir <dir> --center <addr> --target <addr> [--center-node center] [--target-node R4] [--out result.json]");
        Console.WriteLine("  SerialLog.Cli tdma-loop --config <tdma-loop.json>");
        Console.WriteLine();
        Console.WriteLine("Example:");
        Console.WriteLine("  SerialLog.Cli tdma-analyze --log-dir D:\\serial-log-data\\logs\\2026-06-30 --center 0xd2b0 --target 0x03d5 --out result.json");
        Console.WriteLine("  SerialLog.Cli tdma-loop --config docs\\tdma-loop-config.example.json");
    }
}
