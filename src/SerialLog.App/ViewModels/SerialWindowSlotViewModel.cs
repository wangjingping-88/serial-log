using SerialLog.App.Infrastructure;

namespace SerialLog.App.ViewModels;

public sealed class SerialWindowSlotViewModel : ObservableObject
{
    public SerialWindowSlotViewModel(
        SerialWindowViewModel? window,
        int pageIndex = 0,
        int pagePosition = 0,
        int gridColumnCount = 3,
        int gridRowSpan = 1,
        bool canExpand = false,
        bool isExpanded = false,
        int gridColumnSpan = 1)
    {
        Window = window;
        PageIndex = pageIndex;
        PagePosition = pagePosition;
        GridColumnCount = Math.Max(1, gridColumnCount);
        GridRowSpan = Math.Max(1, gridRowSpan);
        GridColumnSpan = Math.Max(1, gridColumnSpan);
        CanExpand = canExpand;
        IsExpanded = isExpanded;
    }

    public SerialWindowViewModel? Window { get; }

    public bool IsAddSlot => Window is null;

    public int PageIndex { get; private set; }

    public int PagePosition { get; private set; }

    public int GridColumnCount { get; private set; }

    public int GridRow => PagePosition / GridColumnCount;

    public int GridColumn => PagePosition % GridColumnCount;

    public int GridRowSpan { get; private set; }

    public int GridColumnSpan { get; private set; }

    public bool CanExpand { get; private set; }

    public bool IsExpanded { get; private set; }

    public int LayoutRevision { get; private set; }

    internal void UpdateLayout(SerialWindowSlotViewModel next)
    {
        var previous = (PageIndex, PagePosition, GridColumnCount, GridRowSpan, GridColumnSpan, CanExpand, IsExpanded);
        var current = (next.PageIndex, next.PagePosition, next.GridColumnCount, next.GridRowSpan, next.GridColumnSpan, next.CanExpand, next.IsExpanded);
        if (previous == current) return;
        (PageIndex, PagePosition, GridColumnCount, GridRowSpan, GridColumnSpan, CanExpand, IsExpanded) = current;
        // 只更新布局和按钮绑定，保持 Window、日志列表及其视觉容器不变。
        if (previous.PageIndex != PageIndex) OnPropertyChanged(nameof(PageIndex));
        if (previous.PagePosition != PagePosition) OnPropertyChanged(nameof(PagePosition));
        if (previous.GridColumnCount != GridColumnCount) OnPropertyChanged(nameof(GridColumnCount));
        if (previous.PagePosition != PagePosition || previous.GridColumnCount != GridColumnCount)
        {
            OnPropertyChanged(nameof(GridRow));
            OnPropertyChanged(nameof(GridColumn));
        }
        if (previous.GridRowSpan != GridRowSpan) OnPropertyChanged(nameof(GridRowSpan));
        if (previous.GridColumnSpan != GridColumnSpan) OnPropertyChanged(nameof(GridColumnSpan));
        if (previous.CanExpand != CanExpand) OnPropertyChanged(nameof(CanExpand));
        if (previous.IsExpanded != IsExpanded)
        {
            OnPropertyChanged(nameof(IsExpanded));
            OnPropertyChanged(nameof(ExpandActionText));
        }
        LayoutRevision++;
        OnPropertyChanged(nameof(LayoutRevision));
    }

    public string ExpandActionText => IsExpanded ? "还原" : "放大";
}
