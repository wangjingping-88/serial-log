using SerialLog.App.Infrastructure;
using SerialLog.Core.Logging;

namespace SerialLog.App.ViewModels;

public sealed class LogLineViewModel : ObservableObject
{
    private bool _isMatch;

    public LogLineViewModel(ReceivedLogLine line)
    {
        Line = line;
        DisplaySegments = BuildDisplaySegments(line);
    }

    public ReceivedLogLine Line { get; }

    public string Text => Line.FormattedText;

    public string DisplayText => $"[{Line.Timestamp:HH:mm:ss.fff}] {AnsiLogTextParser.Strip(Line.Text)}";

    public string CopyText => $"[{Line.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] {AnsiLogTextParser.Strip(Line.Text)}";

    public IReadOnlyList<LogTextSegmentViewModel> DisplaySegments { get; }

    public bool IsMatch
    {
        get => _isMatch;
        set => SetProperty(ref _isMatch, value);
    }

    private static IReadOnlyList<LogTextSegmentViewModel> BuildDisplaySegments(ReceivedLogLine line)
    {
        var segments = new List<LogTextSegmentViewModel>
        {
            new($"[{line.Timestamp:HH:mm:ss.fff}] ", "#6B7280")
        };
        segments.AddRange(AnsiLogTextParser.Parse(line.Text));
        return segments;
    }
}
