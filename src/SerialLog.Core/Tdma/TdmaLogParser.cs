using System.Globalization;

namespace SerialLog.Core.Tdma;

public static class TdmaLogParser
{
    private static readonly string[] KnownPrefixes =
    [
        "TDMA_SYNC_LOST",
        "TDMA_BIZ_LATE",
        "TDMA_DATA_ENQ",
        "TDMA_DATA_TX",
        "TDMA_DATA_RX",
        "TDMA_DATA_LOCAL",
        "TDMA_DATA_FWD",
        "TDMA_ACK_ENQ",
        "TDMA_ACK_TX",
        "TDMA_ACK_RX",
        "TDMA_ACK_MATCH",
        "+SEND:"
    ];

    public static bool TryParse(string nodeName, string line, out TdmaLogEvent? logEvent)
    {
        logEvent = null;
        var payload = ExtractPayload(line);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        var parts = payload.Split(',');
        var name = parts[0].Trim();

        logEvent = name switch
        {
            "TDMA_SYNC_LOST" => BuildSimple(TdmaEventType.SyncLost, nodeName, line),
            "TDMA_BIZ_LATE" => BuildSimple(TdmaEventType.BizLate, nodeName, line),
            "TDMA_DATA_TX" => BuildData(parts, TdmaEventType.DataTx, nodeName, line),
            "TDMA_DATA_RX" => BuildData(parts, TdmaEventType.DataRx, nodeName, line),
            "TDMA_DATA_LOCAL" => BuildData(parts, TdmaEventType.DataLocal, nodeName, line),
            "TDMA_DATA_FWD" => BuildData(parts, TdmaEventType.DataForward, nodeName, line),
            "TDMA_ACK_ENQ" => BuildAck(parts, TdmaEventType.AckEnqueue, nodeName, line),
            "TDMA_ACK_TX" => BuildAck(parts, TdmaEventType.AckTx, nodeName, line),
            "TDMA_ACK_RX" => BuildAck(parts, TdmaEventType.AckRx, nodeName, line),
            "TDMA_ACK_MATCH" => BuildAckMatch(parts, nodeName, line),
            _ when name.StartsWith("+SEND:", StringComparison.OrdinalIgnoreCase) => BuildSendResult(parts, nodeName, line),
            _ => null
        };

        return logEvent is not null;
    }

    public static IReadOnlyList<TdmaLogEvent> ParseLines(string nodeName, IEnumerable<string> lines)
    {
        var events = new List<TdmaLogEvent>();
        foreach (var line in lines)
        {
            if (TryParse(nodeName, line, out var logEvent) && logEvent is not null)
            {
                events.Add(logEvent);
            }
        }

        return events;
    }

    private static string ExtractPayload(string line)
    {
        foreach (var prefix in KnownPrefixes)
        {
            var index = line.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                return line[index..].Trim();
            }
        }

        return string.Empty;
    }

    private static TdmaLogEvent BuildSimple(TdmaEventType type, string nodeName, string rawLine)
    {
        return new TdmaLogEvent
        {
            Type = type,
            NodeName = nodeName,
            RawLine = rawLine
        };
    }

    private static TdmaLogEvent? BuildData(string[] parts, TdmaEventType type, string nodeName, string rawLine)
    {
        if (parts.Length < 8)
        {
            return null;
        }

        return new TdmaLogEvent
        {
            Type = type,
            NodeName = nodeName,
            RawLine = rawLine,
            LocalAddress = Normalize(parts[1]),
            Frame = ParseInt(parts[2]),
            Slot = ParseInt(parts[3]),
            SourceAddress = Normalize(parts[4]),
            DestinationAddress = Normalize(parts[5]),
            PacketNumber = ParseInt(parts[6]),
            Direction = parts[7].Trim(),
            ReturnCode = parts.Length > 9 ? ParseInt(parts[9]) : null
        };
    }

    private static TdmaLogEvent? BuildAck(string[] parts, TdmaEventType type, string nodeName, string rawLine)
    {
        if (parts.Length < 8)
        {
            return null;
        }

        return new TdmaLogEvent
        {
            Type = type,
            NodeName = nodeName,
            RawLine = rawLine,
            LocalAddress = Normalize(parts[1]),
            Frame = ParseInt(parts[2]),
            Slot = ParseInt(parts[3]),
            SourceAddress = Normalize(parts[4]),
            DestinationAddress = Normalize(parts[5]),
            PacketNumber = ParseInt(parts[6]),
            Direction = parts[7].Trim(),
            ReturnCode = type == TdmaEventType.AckTx && parts.Length > 8 ? ParseInt(parts[8]) : null,
            Result = type == TdmaEventType.AckRx && parts.Length > 8 ? ParseInt(parts[8]) : null
        };
    }

    private static TdmaLogEvent? BuildAckMatch(string[] parts, string nodeName, string rawLine)
    {
        if (parts.Length < 7)
        {
            return null;
        }

        return new TdmaLogEvent
        {
            Type = TdmaEventType.AckMatch,
            NodeName = nodeName,
            RawLine = rawLine,
            LocalAddress = Normalize(parts[1]),
            SourceAddress = Normalize(parts[2]),
            DestinationAddress = Normalize(parts[3]),
            PacketNumber = ParseInt(parts[4]),
            Matched = ParseInt(parts[5]) == 1,
            Result = ParseInt(parts[6])
        };
    }

    private static TdmaLogEvent BuildSendResult(string[] parts, string nodeName, string rawLine)
    {
        var result = parts.Length > 1 ? parts[1].Trim() : string.Empty;
        return new TdmaLogEvent
        {
            Type = TdmaEventType.SendResult,
            NodeName = nodeName,
            RawLine = rawLine,
            SendResult = result
        };
    }

    private static int? ParseInt(string value)
    {
        return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static string Normalize(string value)
    {
        return TdmaLoopCriteria.NormalizeAddress(value);
    }
}
