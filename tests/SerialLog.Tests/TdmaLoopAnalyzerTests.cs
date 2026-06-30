using SerialLog.Core.Tdma;

namespace SerialLog.Tests;

public class TdmaLoopAnalyzerTests
{
    private static readonly TdmaLoopCriteria Criteria = new()
    {
        CenterAddress = "0xd2b0",
        TargetAddress = "0x03d5",
        CenterNodeName = "center",
        TargetNodeName = "R4",
        ExpectedDataTxSlot = 0,
        ExpectedTargetDataSlot = 3,
        ExpectedTargetAckSlot = 4,
        ExpectedCenterAckSlot = 7,
        RequireOneFrameLoop = true
    };

    [Fact]
    public void Analyzer_accepts_data_and_ack_completed_in_one_frame()
    {
        var events = Parse([
            ("center", "TDMA_DATA_TX,0xd2b0,12,0,0xd2b0,0x03d5,77,0,0,0"),
            ("R4", "TDMA_DATA_LOCAL,0x03d5,12,3,0xd2b0,0x03d5,77,0,0"),
            ("R4", "TDMA_ACK_TX,0x03d5,12,4,0xd2b0,0x03d5,77,1,0"),
            ("center", "TDMA_ACK_RX,0xd2b0,12,7,0xd2b0,0x03d5,77,1,0"),
            ("center", "TDMA_ACK_MATCH,0xd2b0,0xd2b0,0x03d5,77,1,0")
        ]);

        var result = TdmaLoopAnalyzer.Analyze(events, Criteria);

        Assert.True(result.Success);
        Assert.Equal("done", result.Stage);
        Assert.Equal(77, result.PacketNumber);
        Assert.Equal(12, result.DataFrame);
    }

    [Fact]
    public void Analyzer_rejects_ack_that_returns_in_next_frame()
    {
        var events = Parse([
            ("center", "TDMA_DATA_TX,0xd2b0,12,0,0xd2b0,0x03d5,77,0,0,0"),
            ("R4", "TDMA_DATA_LOCAL,0x03d5,12,3,0xd2b0,0x03d5,77,0,0"),
            ("R4", "TDMA_ACK_TX,0x03d5,13,4,0xd2b0,0x03d5,77,1,0"),
            ("center", "TDMA_ACK_RX,0xd2b0,13,7,0xd2b0,0x03d5,77,1,0"),
            ("center", "TDMA_ACK_MATCH,0xd2b0,0xd2b0,0x03d5,77,1,0")
        ]);

        var result = TdmaLoopAnalyzer.Analyze(events, Criteria);

        Assert.False(result.Success);
        Assert.Equal("ack_tx", result.Stage);
    }

    [Fact]
    public void Analyzer_accepts_wrapped_rx_frame_when_tx_frame_exceeds_sync_header_width()
    {
        var events = Parse([
            ("center", "TDMA_DATA_TX,0xd2b0,352,0,0xd2b0,0x03d5,251,0,3,0"),
            ("R4", "TDMA_DATA_LOCAL,0x03d5,96,3,0xd2b0,0x03d5,251,0"),
            ("R4", "TDMA_ACK_TX,0x03d5,352,4,0xd2b0,0x03d5,251,1,0"),
            ("center", "TDMA_ACK_RX,0xd2b0,96,7,0xd2b0,0x03d5,251,1,0"),
            ("center", "TDMA_ACK_MATCH,0xd2b0,0xd2b0,0x03d5,251,1,0")
        ]);

        var result = TdmaLoopAnalyzer.Analyze(events, Criteria);

        Assert.True(result.Success);
        Assert.Equal("done", result.Stage);
        Assert.Equal(251, result.PacketNumber);
        Assert.Equal(352, result.DataFrame);
    }

    [Fact]
    public void Analyzer_rejects_route_ack_match_without_center_ack()
    {
        var events = Parse([
            ("center", "TDMA_DATA_TX,0xd2b0,60,0,0xd2b0,0x03d5,59,0,1,0"),
            ("R1", "TDMA_DATA_TX,0xccdc,61,1,0xd2b0,0x03d5,59,0,1,0"),
            ("R4", "TDMA_DATA_LOCAL,0x03d5,61,3,0xd2b0,0x03d5,59,0"),
            ("R4", "TDMA_ACK_TX,0x03d5,61,4,0xd2b0,0x03d5,59,1,0"),
            ("R3", "TDMA_ACK_RX,0xfcb9,61,4,0xd2b0,0x03d5,59,1,0"),
            ("R3", "TDMA_ACK_MATCH,0xfcb9,0xd2b0,0x03d5,59,1,0")
        ]);

        var result = TdmaLoopAnalyzer.Analyze(events, Criteria);

        Assert.False(result.Success);
        Assert.Equal("data_local", result.Stage);
    }

    [Fact]
    public void Analyzer_stops_on_sync_lost()
    {
        var events = Parse([
            ("R2", "[2026-06-30 10:55:45.000] TDMA_SYNC_LOST,0xfc8e")
        ]);

        var result = TdmaLoopAnalyzer.Analyze(events, Criteria);

        Assert.False(result.Success);
        Assert.True(result.HasSyncLost);
        Assert.Equal("sync", result.Stage);
    }

    [Fact]
    public void Parser_reads_tdma_payload_from_timestamped_log_line()
    {
        var ok = TdmaLogParser.TryParse(
            "center",
            "[2026-06-30 11:20:00.123] TDMA_ACK_RX,0xd2b0,12,7,0xd2b0,0x03d5,77,1,0",
            out var logEvent);

        Assert.True(ok);
        Assert.NotNull(logEvent);
        Assert.Equal(TdmaEventType.AckRx, logEvent!.Type);
        Assert.Equal(12, logEvent.Frame);
        Assert.Equal(7, logEvent.Slot);
        Assert.Equal(0, logEvent.Result);
    }

    private static IReadOnlyList<TdmaLogEvent> Parse(IEnumerable<(string Node, string Line)> lines)
    {
        var events = new List<TdmaLogEvent>();
        foreach (var (node, line) in lines)
        {
            Assert.True(TdmaLogParser.TryParse(node, line, out var parsed));
            events.Add(parsed!);
        }

        return events;
    }
}
