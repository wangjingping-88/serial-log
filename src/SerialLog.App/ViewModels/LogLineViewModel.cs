using SerialLog.App.Infrastructure;
using SerialLog.Core.Logging;

namespace SerialLog.App.ViewModels;

public sealed class LogLineViewModel : ObservableObject
{
    private bool _isMatch;

    public LogLineViewModel(ReceivedLogLine line)
    {
        Line = line;
    }

    public ReceivedLogLine Line { get; }

    public string Text => Line.FormattedText;

    public bool IsMatch
    {
        get => _isMatch;
        set => SetProperty(ref _isMatch, value);
    }
}
