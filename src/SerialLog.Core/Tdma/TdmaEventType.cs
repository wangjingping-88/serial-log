namespace SerialLog.Core.Tdma;

public enum TdmaEventType
{
    Unknown,
    SyncLost,
    BizLate,
    DataTx,
    DataRx,
    DataLocal,
    DataForward,
    AckEnqueue,
    AckTx,
    AckRx,
    AckMatch,
    SendResult
}
