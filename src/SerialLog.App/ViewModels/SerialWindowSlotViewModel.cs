namespace SerialLog.App.ViewModels;

public sealed class SerialWindowSlotViewModel
{
    public SerialWindowSlotViewModel(
        SerialWindowViewModel? window,
        int pageIndex = 0,
        int pagePosition = 0,
        int gridColumnCount = 3,
        int gridRowSpan = 1,
        bool canExpand = false,
        bool isExpanded = false)
    {
        Window = window;
        PageIndex = pageIndex;
        PagePosition = pagePosition;
        GridColumnCount = Math.Max(1, gridColumnCount);
        GridRowSpan = gridRowSpan;
        CanExpand = canExpand;
        IsExpanded = isExpanded;
    }

    public SerialWindowViewModel? Window { get; }

    public bool IsAddSlot => Window is null;

    public int PageIndex { get; }

    public int PagePosition { get; }

    public int GridColumnCount { get; }

    public int GridRow => PagePosition / GridColumnCount;

    public int GridColumn => PagePosition % GridColumnCount;

    public int GridRowSpan { get; }

    public int GridColumnSpan => 1;

    public bool CanExpand { get; }

    public bool IsExpanded { get; }

    public string ExpandActionText => IsExpanded ? "还原" : "放大";
}
