using System.Windows;
using System.Windows.Controls;
using SerialLog.App.ViewModels;

namespace SerialLog.App.Controls;

public sealed class SerialWindowGridPanel : Panel
{
    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows),
        typeof(int),
        typeof(SerialWindowGridPanel),
        new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsArrange | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns),
        typeof(int),
        typeof(SerialWindowGridPanel),
        new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsArrange | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public int Rows
    {
        get => (int)GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var rows = Math.Max(1, Rows);
        var columns = Math.Max(1, Columns);
        var cellWidth = GetCellLength(availableSize.Width, columns);
        var cellHeight = GetCellLength(availableSize.Height, rows);

        foreach (UIElement child in InternalChildren)
        {
            var slot = GetSlot(child);
            var desiredColumns = Math.Max(1, slot?.GridColumnSpan ?? 1);
            var desiredRows = Math.Max(1, slot?.GridRowSpan ?? 1);
            child.Measure(new Size(cellWidth * desiredColumns, cellHeight * desiredRows));
        }

        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var rows = Math.Max(1, Rows);
        var columns = Math.Max(1, Columns);
        var cellWidth = finalSize.Width / columns;
        var cellHeight = finalSize.Height / rows;

        var index = 0;
        foreach (UIElement child in InternalChildren)
        {
            var slot = GetSlot(child);
            var row = slot?.GridRow ?? index / columns;
            var column = slot?.GridColumn ?? index % columns;
            var rowSpan = Math.Max(1, slot?.GridRowSpan ?? 1);
            var columnSpan = Math.Max(1, slot?.GridColumnSpan ?? 1);

            row = Math.Clamp(row, 0, rows - 1);
            column = Math.Clamp(column, 0, columns - 1);
            rowSpan = Math.Min(rowSpan, rows - row);
            columnSpan = Math.Min(columnSpan, columns - column);

            child.Arrange(new Rect(
                column * cellWidth,
                row * cellHeight,
                cellWidth * columnSpan,
                cellHeight * rowSpan));
            index++;
        }

        return finalSize;
    }

    private static double GetCellLength(double availableLength, int count)
    {
        return double.IsInfinity(availableLength) || double.IsNaN(availableLength)
            ? double.PositiveInfinity
            : availableLength / count;
    }

    private static SerialWindowSlotViewModel? GetSlot(UIElement child)
    {
        return child is FrameworkElement { DataContext: SerialWindowSlotViewModel slot } ? slot : null;
    }
}
