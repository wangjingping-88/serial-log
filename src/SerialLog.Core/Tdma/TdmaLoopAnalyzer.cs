namespace SerialLog.Core.Tdma;

public static class TdmaLoopAnalyzer
{
    public static TdmaLoopAnalysisResult Analyze(IEnumerable<TdmaLogEvent> events, TdmaLoopCriteria criteria)
    {
        var ordered = events.ToArray();
        var syncLost = ordered.FirstOrDefault(item => item.Type == TdmaEventType.SyncLost);
        if (syncLost is not null)
        {
            return Fail("sync", "TDMA_SYNC_LOST appeared before loop success.", syncLost.RawLine, hasSyncLost: true);
        }

        var center = criteria.NormalizedCenterAddress;
        var target = criteria.NormalizedTargetAddress;
        var dataTxCandidates = ordered
            .Where(item => item.Type == TdmaEventType.DataTx
                && IsNodeAddress(item, criteria.CenterNodeName, center)
                && SameAddress(item.SourceAddress, center)
                && SameAddress(item.DestinationAddress, target)
                && (!criteria.RequireOneFrameLoop || item.Slot == criteria.ExpectedDataTxSlot))
            .OrderBy(item => item.Frame)
            .ThenBy(item => item.Slot)
            .ToArray();

        if (dataTxCandidates.Length == 0)
        {
            return Fail("data_tx", "No center DATA TX event found.", (string?)null);
        }

        foreach (var dataTx in dataTxCandidates)
        {
            var packetNumber = dataTx.PacketNumber;
            var dataFrame = dataTx.Frame;
            var evidence = new List<string> { dataTx.RawLine };

            var dataLocal = ordered.FirstOrDefault(item => item.Type == TdmaEventType.DataLocal
                && IsNodeAddress(item, criteria.TargetNodeName, target)
                && item.PacketNumber == packetNumber
                && SameAddress(item.SourceAddress, center)
                && SameAddress(item.DestinationAddress, target)
                && IsExpectedFrame(item, dataFrame, criteria.RequireOneFrameLoop));
            if (dataLocal is null)
            {
                continue;
            }

            evidence.Add(dataLocal.RawLine);
            if (criteria.RequireOneFrameLoop
                && (dataLocal.Slot is null || dataLocal.Slot > criteria.ExpectedTargetDataSlot))
            {
                return Fail("data_local", "DATA arrived at target outside expected downlink slot.", evidence, packetNumber, dataFrame);
            }

            var ackTx = ordered.FirstOrDefault(item => item.Type == TdmaEventType.AckTx
                && IsNodeAddress(item, criteria.TargetNodeName, target)
                && item.PacketNumber == packetNumber
                && SameAddress(item.SourceAddress, center)
                && SameAddress(item.DestinationAddress, target)
                && IsExpectedFrame(item, dataFrame, criteria.RequireOneFrameLoop));
            if (ackTx is null)
            {
                return Fail("ack_tx", "Target received DATA but did not transmit ACK in the same frame.", evidence, packetNumber, dataFrame);
            }

            evidence.Add(ackTx.RawLine);
            if (criteria.RequireOneFrameLoop
                && (ackTx.Slot is null || ackTx.Slot < criteria.ExpectedTargetAckSlot))
            {
                return Fail("ack_tx_slot", "ACK TX happened before expected uplink slot.", evidence, packetNumber, dataFrame);
            }

            var ackRx = ordered.FirstOrDefault(item => item.Type == TdmaEventType.AckRx
                && IsNodeAddress(item, criteria.CenterNodeName, center)
                && item.PacketNumber == packetNumber
                && SameAddress(item.SourceAddress, center)
                && SameAddress(item.DestinationAddress, target)
                && item.Result == 0
                && IsExpectedFrame(item, dataFrame, criteria.RequireOneFrameLoop));
            if (ackRx is null)
            {
                return Fail("ack_rx", "ACK was transmitted but center did not receive ACK in the same frame.", evidence, packetNumber, dataFrame);
            }

            evidence.Add(ackRx.RawLine);
            if (criteria.RequireOneFrameLoop
                && (ackRx.Slot is null || ackRx.Slot > criteria.ExpectedCenterAckSlot))
            {
                return Fail("ack_rx_slot", "Center ACK RX happened outside expected uplink slot.", evidence, packetNumber, dataFrame);
            }

            var ackMatch = ordered.FirstOrDefault(item => item.Type == TdmaEventType.AckMatch
                && IsNodeAddress(item, criteria.CenterNodeName, center)
                && item.PacketNumber == packetNumber
                && SameAddress(item.SourceAddress, center)
                && SameAddress(item.DestinationAddress, target)
                && item.Matched == true
                && item.Result == 0);
            if (ackMatch is null)
            {
                return Fail("ack_match", "Center received ACK but did not match pending DATA.", evidence, packetNumber, dataFrame);
            }

            evidence.Add(ackMatch.RawLine);
            return new TdmaLoopAnalysisResult
            {
                Success = true,
                Stage = "done",
                Reason = "DATA and ACK completed in one TDMA frame.",
                PacketNumber = packetNumber,
                DataFrame = dataFrame,
                Evidence = evidence
            };
        }

        var first = dataTxCandidates[0];
        return Fail(
            "data_local",
            "DATA TX was found, but target DATA_LOCAL was not found in the required frame.",
            first.RawLine,
            packetNumber: first.PacketNumber,
            dataFrame: first.Frame);
    }

    private static bool SameAddress(string? left, string right)
    {
        return string.Equals(TdmaLoopCriteria.NormalizeAddress(left), right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameNode(string left, string right)
    {
        return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNodeAddress(TdmaLogEvent item, string nodeName, string address)
    {
        return SameNode(item.NodeName, nodeName) && SameAddress(item.LocalAddress, address);
    }

    private static bool IsExpectedFrame(TdmaLogEvent item, int? dataFrame, bool requireOneFrame)
    {
        if (!requireOneFrame)
        {
            return true;
        }

        if (item.Frame is null || dataFrame is null)
        {
            return false;
        }

        if (item.Frame == dataFrame)
        {
            return true;
        }

        return dataFrame >= 256 && item.Frame == (dataFrame & 0xFF);
    }

    private static TdmaLoopAnalysisResult Fail(
        string stage,
        string reason,
        string? evidence,
        int? packetNumber = null,
        int? dataFrame = null,
        bool hasSyncLost = false)
    {
        return Fail(stage, reason, evidence is null ? [] : [evidence], packetNumber, dataFrame, hasSyncLost);
    }

    private static TdmaLoopAnalysisResult Fail(
        string stage,
        string reason,
        IReadOnlyList<string> evidence,
        int? packetNumber = null,
        int? dataFrame = null,
        bool hasSyncLost = false)
    {
        return new TdmaLoopAnalysisResult
        {
            Success = false,
            Stage = stage,
            Reason = reason,
            PacketNumber = packetNumber,
            DataFrame = dataFrame,
            HasSyncLost = hasSyncLost,
            Evidence = evidence
        };
    }
}
