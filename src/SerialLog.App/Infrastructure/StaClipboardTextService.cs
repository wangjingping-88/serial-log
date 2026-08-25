using System.Windows;

namespace SerialLog.App.Infrastructure;

public sealed class StaClipboardTextService
{
    private readonly object _operationLock = new();
    private readonly Action<string> _setText;
    private Task _activeOperation = Task.CompletedTask;

    public StaClipboardTextService(Action<string>? setText = null)
    {
        _setText = setText ?? Clipboard.SetText;
    }

    public bool IsBusy
    {
        get
        {
            lock (_operationLock)
            {
                return !_activeOperation.IsCompleted;
            }
        }
    }

    public Task SetTextAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        lock (_operationLock)
        {
            if (!_activeOperation.IsCompleted)
            {
                throw new InvalidOperationException("已有复制任务正在写入剪贴板。");
            }

            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    _setText(text);
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            })
            {
                IsBackground = true,
                Name = "SerialLog Clipboard Writer"
            };
            thread.SetApartmentState(ApartmentState.STA);
            _activeOperation = completion.Task;
            try
            {
                thread.Start();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
                throw;
            }

            return _activeOperation;
        }
    }
}
