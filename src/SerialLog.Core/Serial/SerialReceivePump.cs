namespace SerialLog.Core.Serial;

// 同步串口 Read 不占用共享线程池，DataReceived 只唤醒；同一连接只有一个接收工作线程。
internal sealed class SerialReceivePump : IDisposable
{
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private bool _stopped;
    internal SerialReceivePump(Action receive, TimeSpan interval, Action<Exception> failed)
    {
        _thread = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    lock (_gate) { if (_stopped) break; }
                    try { receive(); } catch (Exception ex) { failed(ex); }
                    _wake.WaitOne(interval);
                }
            }
            finally { lock (_gate) { _stopped = true; _wake.Dispose(); } }
        }) { IsBackground = true, Name = "SerialLog 接收" };
        _thread.Start();
    }

    internal void Signal() { lock (_gate) { if (!_stopped) _wake.Set(); } }
    public void Dispose() { lock (_gate) { if (_stopped) return; _stopped = true; _wake.Set(); } }
    internal bool WaitForExit(TimeSpan timeout) => _thread.Join(timeout);
}
