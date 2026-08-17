using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SerialLog.App.ViewModels;

namespace SerialLog.App.Controls;

/// <summary>
/// Provides character-level selection across colored log lines while preserving follow and scroll state.
/// </summary>
public sealed class SelectableLogViewer : RichTextBox
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource),
        typeof(IEnumerable),
        typeof(SelectableLogViewer),
        new PropertyMetadata(null, OnItemsSourceChanged));

    public static readonly DependencyProperty IsAutoScrollPausedProperty = DependencyProperty.Register(
        nameof(IsAutoScrollPaused),
        typeof(bool),
        typeof(SelectableLogViewer),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsAutoScrollPausedChanged));

    public static readonly DependencyProperty SavedHorizontalOffsetProperty = DependencyProperty.Register(
        nameof(SavedHorizontalOffset),
        typeof(double),
        typeof(SelectableLogViewer),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty SavedVerticalOffsetProperty = DependencyProperty.Register(
        nameof(SavedVerticalOffset),
        typeof(double),
        typeof(SelectableLogViewer),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty HasStoredPositionProperty = DependencyProperty.Register(
        nameof(HasStoredPosition),
        typeof(bool),
        typeof(SelectableLogViewer),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    private readonly List<LogLineViewModel> _renderedLines = [];
    private readonly Dictionary<LogLineViewModel, Paragraph> _paragraphs = [];
    private INotifyCollectionChanged? _collection;
    private ScrollViewer? _scrollViewer;
    private bool _isRestoringPosition;
    private bool _followScheduled;
    private bool _reconcileScheduled;

    public string? ContextLineText { get; private set; }

    public SelectableLogViewer()
    {
        IsReadOnly = true;
        IsUndoEnabled = false;
        AcceptsTab = false;
        BorderThickness = new Thickness(1);
        Padding = new Thickness(4, 2, 4, 2);
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        InputMethod.SetIsInputMethodEnabled(this, false);
        Document = CreateDocument();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        AddHandler(PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPreviewMouseWheel), handledEventsToo: true);
        AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), handledEventsToo: true);
    }

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public bool IsAutoScrollPaused
    {
        get => (bool)GetValue(IsAutoScrollPausedProperty);
        set => SetValue(IsAutoScrollPausedProperty, value);
    }

    public double SavedHorizontalOffset
    {
        get => (double)GetValue(SavedHorizontalOffsetProperty);
        set => SetValue(SavedHorizontalOffsetProperty, value);
    }

    public double SavedVerticalOffset
    {
        get => (double)GetValue(SavedVerticalOffsetProperty);
        set => SetValue(SavedVerticalOffsetProperty, value);
    }

    public bool HasStoredPosition
    {
        get => (bool)GetValue(HasStoredPositionProperty);
        set => SetValue(HasStoredPositionProperty, value);
    }

    public void ResumeAutoScroll()
    {
        IsAutoScrollPaused = false;
        ScheduleFollow();
    }

    public void UpdateContextLine(Point position)
    {
        var textPointer = GetPositionFromPoint(position, snapToText: true);
        ContextLineText = textPointer?.Paragraph is { } paragraph
            ? new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.TrimEnd('\r', '\n')
            : null;
    }

    private static void OnItemsSourceChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var viewer = (SelectableLogViewer)dependencyObject;
        viewer.DetachCollection();
        viewer.AttachCollection();
        viewer.ScheduleReconcile();
    }

    private static void OnIsAutoScrollPausedChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is SelectableLogViewer { IsLoaded: true, IsAutoScrollPaused: false } viewer)
        {
            viewer.ScheduleFollow();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        AttachCollection();
        Dispatcher.BeginInvoke(() =>
        {
            ReconcileDocument();
            _scrollViewer = FindVisualChild<ScrollViewer>(this);
            if (_scrollViewer is not null)
            {
                _scrollViewer.ScrollChanged += OnScrollChanged;
            }

            RestoreScrollPosition();
        }, DispatcherPriority.Loaded);
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        StoreScrollPosition();
        if (_scrollViewer is not null)
        {
            _scrollViewer.ScrollChanged -= OnScrollChanged;
            _scrollViewer = null;
        }

        DetachCollection();
    }

    private void AttachCollection()
    {
        if (_collection is not null || ItemsSource is not INotifyCollectionChanged collection)
        {
            return;
        }

        _collection = collection;
        _collection.CollectionChanged += OnCollectionChanged;
    }

    private void DetachCollection()
    {
        if (_collection is not null)
        {
            _collection.CollectionChanged -= OnCollectionChanged;
            _collection = null;
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (!IsLoaded)
        {
            return;
        }

        if (args.Action == NotifyCollectionChangedAction.Add && args.NewItems is not null)
        {
            foreach (var line in args.NewItems.OfType<LogLineViewModel>())
            {
                AppendLine(line);
            }

            ScheduleFollow();
            return;
        }

        ScheduleReconcile();
    }

    private void ScheduleReconcile()
    {
        if (_reconcileScheduled || !IsLoaded)
        {
            return;
        }

        _reconcileScheduled = true;
        Dispatcher.BeginInvoke(() =>
        {
            _reconcileScheduled = false;
            ReconcileDocument();
        }, DispatcherPriority.Background);
    }

    private void ReconcileDocument()
    {
        var sourceLines = ItemsSource?.OfType<LogLineViewModel>().ToArray() ?? [];
        if (sourceLines.Length == 0)
        {
            ResetDocument();
            return;
        }

        if (_renderedLines.Count == 0)
        {
            foreach (var line in sourceLines)
            {
                AppendLine(line);
            }

            ScheduleFollow();
            return;
        }

        var overlapStart = _renderedLines.FindIndex(line => ReferenceEquals(line, sourceLines[0]));
        if (overlapStart < 0)
        {
            ResetDocument();
            foreach (var line in sourceLines)
            {
                AppendLine(line);
            }

            ScheduleFollow();
            return;
        }

        var overlapLength = Math.Min(_renderedLines.Count - overlapStart, sourceLines.Length);
        for (var index = 0; index < overlapLength; index++)
        {
            if (!ReferenceEquals(_renderedLines[overlapStart + index], sourceLines[index]))
            {
                ResetDocument();
                foreach (var line in sourceLines)
                {
                    AppendLine(line);
                }

                ScheduleFollow();
                return;
            }
        }

        RemoveHeadLines(overlapStart);
        for (var index = overlapLength; index < sourceLines.Length; index++)
        {
            AppendLine(sourceLines[index]);
        }

        ScheduleFollow();
    }

    private void ResetDocument()
    {
        foreach (var line in _renderedLines)
        {
            line.PropertyChanged -= OnLinePropertyChanged;
        }

        _renderedLines.Clear();
        _paragraphs.Clear();
        Document = CreateDocument();
        UpdateScrollBarVisibility();
    }

    private void RemoveHeadLines(int count)
    {
        for (var index = 0; index < count && _renderedLines.Count > 0; index++)
        {
            var line = _renderedLines[0];
            line.PropertyChanged -= OnLinePropertyChanged;
            if (_paragraphs.Remove(line, out var paragraph))
            {
                Document.Blocks.Remove(paragraph);
            }

            _renderedLines.RemoveAt(0);
        }
    }

    private void AppendLine(LogLineViewModel line)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0),
            Padding = new Thickness(0),
            Background = line.IsMatch ? CreateBrush("#FDE68A") : Brushes.Transparent
        };

        foreach (var segment in line.DisplaySegments)
        {
            paragraph.Inlines.Add(new Run(segment.Text)
            {
                Foreground = CreateBrush(segment.EffectiveForeground)
            });
        }

        Document.Blocks.Add(paragraph);
        _renderedLines.Add(line);
        _paragraphs[line] = paragraph;
        line.PropertyChanged += OnLinePropertyChanged;
        UpdateScrollBarVisibility();
    }

    private void OnLinePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(LogLineViewModel.IsMatch) || sender is not LogLineViewModel line)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_paragraphs.TryGetValue(line, out var paragraph))
            {
                paragraph.Background = line.IsMatch ? CreateBrush("#FDE68A") : Brushes.Transparent;
            }
        }, DispatcherPriority.Background);
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs args)
    {
        IsAutoScrollPaused = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            ResumeAutoScroll();
            args.Handled = true;
        }
    }

    private void ScheduleFollow()
    {
        if (IsAutoScrollPaused || _followScheduled || !IsLoaded)
        {
            return;
        }

        _followScheduled = true;
        Dispatcher.BeginInvoke(() =>
        {
            _followScheduled = false;
            if (!IsAutoScrollPaused)
            {
                ScrollToEnd();
            }
        }, DispatcherPriority.Background);
    }

    private void RestoreScrollPosition()
    {
        if (_scrollViewer is null)
        {
            return;
        }

        _isRestoringPosition = true;
        UpdateLayout();
        if (IsAutoScrollPaused && HasStoredPosition)
        {
            _scrollViewer.ScrollToVerticalOffset(SavedVerticalOffset);
        }
        else if (!IsAutoScrollPaused)
        {
            _scrollViewer.ScrollToBottom();
        }

        if (HasStoredPosition)
        {
            _scrollViewer.ScrollToHorizontalOffset(SavedHorizontalOffset);
        }

        _isRestoringPosition = false;
    }

    private void StoreScrollPosition()
    {
        var scrollViewer = _scrollViewer ?? FindVisualChild<ScrollViewer>(this);
        if (scrollViewer is null)
        {
            return;
        }

        SavedHorizontalOffset = scrollViewer.HorizontalOffset;
        SavedVerticalOffset = scrollViewer.VerticalOffset;
        HasStoredPosition = true;
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs args)
    {
        if (_isRestoringPosition || _scrollViewer is null || (args.HorizontalChange == 0 && args.VerticalChange == 0))
        {
            return;
        }

        SavedHorizontalOffset = _scrollViewer.HorizontalOffset;
        SavedVerticalOffset = _scrollViewer.VerticalOffset;
        HasStoredPosition = true;
    }

    private void UpdateScrollBarVisibility()
    {
        HorizontalScrollBarVisibility = _renderedLines.Count == 0
            ? ScrollBarVisibility.Hidden
            : ScrollBarVisibility.Auto;
    }

    private static FlowDocument CreateDocument()
    {
        return new FlowDocument
        {
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left
        };
    }

    private static Brush CreateBrush(string color)
    {
        return new BrushConverter().ConvertFromInvariantString(color) as Brush ?? Brushes.Black;
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }
}
