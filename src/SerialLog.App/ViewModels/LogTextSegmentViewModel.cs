namespace SerialLog.App.ViewModels;

public sealed record LogTextSegmentViewModel(string Text, string? Foreground = null)
{
    public string EffectiveForeground => string.IsNullOrWhiteSpace(Foreground) ? "#111827" : Foreground;
}
