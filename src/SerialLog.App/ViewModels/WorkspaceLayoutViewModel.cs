using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using SerialLog.App.Infrastructure;
using SerialLog.Core.Configuration;

namespace SerialLog.App.ViewModels;

public sealed class WorkspaceLayoutViewModel : ObservableObject
{
    private const int PageSize = 6;
    private readonly ObservableCollection<SerialWindowViewModel> _serialWindows;
    private readonly Action<string> _setStatus;
    private readonly HashSet<string> _expandedWindowIds = [];
    private int _currentPageIndex;
    private int _explicitPageCount = 1;
    private CommandPanelDock _commandPanelDock = CommandPanelDock.Bottom;
    private bool _isCommandPanelFloating;

    public WorkspaceLayoutViewModel(
        ObservableCollection<SerialWindowViewModel> serialWindows,
        Action<string> setStatus)
    {
        _serialWindows = serialWindows;
        _setStatus = setStatus;
        PreviousPageCommand = new RelayCommand(() => CurrentPageIndex--, () => CurrentPageIndex > 0);
        NextPageCommand = new RelayCommand(() => CurrentPageIndex++, () => CurrentPageIndex < PageCount - 1);
        AddPageCommand = new RelayCommand(AddPage);
        RemoveCurrentPageCommand = new RelayCommand(RemoveCurrentPage, CanRemoveCurrentPage);
        ToggleWindowExpansionCommand = new RelayCommand(
            ToggleWindowExpansion,
            parameter => parameter is SerialWindowSlotViewModel { IsAddSlot: false } slot
                && (slot.CanExpand || slot.IsExpanded));
        SetCommandPanelDockCommand = new RelayCommand(SetCommandPanelDock);
        FloatCommandPanelCommand = new RelayCommand(FloatCommandPanel, () => !IsCommandPanelFloating);
        RestoreCommandPanelCommand = new RelayCommand(RestoreCommandPanel, () => IsCommandPanelFloating);

        _serialWindows.CollectionChanged += SerialWindows_CollectionChanged;
        RebuildCurrentPage();
    }

    public ObservableCollection<SerialWindowSlotViewModel> CurrentPageWindows { get; } = [];

    public RelayCommand PreviousPageCommand { get; }

    public RelayCommand NextPageCommand { get; }

    public RelayCommand AddPageCommand { get; }

    public RelayCommand RemoveCurrentPageCommand { get; }

    public RelayCommand ToggleWindowExpansionCommand { get; }

    public RelayCommand SetCommandPanelDockCommand { get; }

    public RelayCommand FloatCommandPanelCommand { get; }

    public RelayCommand RestoreCommandPanelCommand { get; }

    public int CurrentPageIndex
    {
        get => _currentPageIndex;
        set
        {
            var clamped = Math.Clamp(value, 0, Math.Max(0, PageCount - 1));
            if (SetProperty(ref _currentPageIndex, clamped))
            {
                OnPropertyChanged(nameof(PageLabel));
                RebuildCurrentPage();
                PreviousPageCommand.RaiseCanExecuteChanged();
                NextPageCommand.RaiseCanExecuteChanged();
                RemoveCurrentPageCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public int PageCount => Math.Max(_explicitPageCount, NaturalPageCount);

    private int NaturalPageCount
    {
        get
        {
            if (_serialWindows.Count == 0)
            {
                return 1;
            }

            return Math.Max(1, _serialWindows.Max(window => window.PageIndex) + 1);
        }
    }

    public string PageLabel => $"{CurrentPageIndex + 1} / {PageCount}";

    public CommandPanelDock CommandPanelDock
    {
        get => _commandPanelDock;
        set
        {
            if (SetProperty(ref _commandPanelDock, value))
            {
                RaiseCommandPanelShapeChanged();
            }
        }
    }

    public bool IsCommandPanelFloating
    {
        get => _isCommandPanelFloating;
        set
        {
            if (SetProperty(ref _isCommandPanelFloating, value))
            {
                RaiseCommandPanelShapeChanged();
                FloatCommandPanelCommand.RaiseCanExecuteChanged();
                RestoreCommandPanelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsCommandPanelDockedBottom => !IsCommandPanelFloating && CommandPanelDock == CommandPanelDock.Bottom;

    public bool IsCommandPanelDockedTop => !IsCommandPanelFloating && CommandPanelDock == CommandPanelDock.Top;

    public bool IsCommandPanelDockedLeft => !IsCommandPanelFloating && CommandPanelDock == CommandPanelDock.Left;

    public bool IsCommandPanelDockedRight => !IsCommandPanelFloating && CommandPanelDock == CommandPanelDock.Right;

    public bool IsCommandPanelDockedVertical => IsCommandPanelDockedLeft || IsCommandPanelDockedRight;

    public bool IsCommandPanelDockedHorizontal => !IsCommandPanelFloating && !IsCommandPanelDockedVertical;

    public int SerialGridRows => IsCommandPanelDockedVertical ? 3 : 2;

    public int SerialGridColumns => IsCommandPanelDockedVertical ? 2 : 3;

    public Dock CommandPanelDockEdge => CommandPanelDock switch
    {
        CommandPanelDock.Top => Dock.Top,
        CommandPanelDock.Left => Dock.Left,
        CommandPanelDock.Right => Dock.Right,
        _ => Dock.Bottom
    };

    public double CommandPanelWidth => IsCommandPanelFloating ? 0 : IsCommandPanelDockedVertical ? 540 : double.NaN;

    public double CommandPanelHeight => IsCommandPanelFloating ? 0 : IsCommandPanelDockedHorizontal ? 300 : double.NaN;

    public Thickness CommandPanelMargin => IsCommandPanelFloating ? new Thickness(0) : CommandPanelDock switch
    {
        CommandPanelDock.Top => new Thickness(0, 12, 0, 8),
        CommandPanelDock.Left => new Thickness(0, 12, 8, 12),
        CommandPanelDock.Right => new Thickness(8, 12, 0, 12),
        _ => new Thickness(0, 8, 0, 12)
    };

    public Visibility CommandPanelVisibility => IsCommandPanelFloating ? Visibility.Collapsed : Visibility.Visible;

    public string CommandPanelOrientationLabel => IsCommandPanelFloating ? "命令区：浮动" : CommandPanelDock switch
    {
        CommandPanelDock.Top => "命令区：顶部",
        CommandPanelDock.Left => "命令区：左侧",
        CommandPanelDock.Right => "命令区：右侧",
        _ => "命令区：底部"
    };

    public void MoveSerialWindow(string windowId, int targetIndex)
    {
        var sourceIndex = _serialWindows.ToList().FindIndex(window => window.Id == windowId);
        if (sourceIndex < 0)
        {
            return;
        }

        var clampedTarget = Math.Clamp(targetIndex, 0, _serialWindows.Count - 1);
        if (sourceIndex == clampedTarget)
        {
            return;
        }

        var title = _serialWindows[sourceIndex].Title;
        _serialWindows.Move(sourceIndex, clampedTarget);
        RebuildCurrentPage();
        _setStatus($"已移动窗口：{title}");
    }

    public void MoveSerialWindow(string windowId, int targetPageIndex, int targetPagePosition)
    {
        var window = _serialWindows.FirstOrDefault(item => item.Id == windowId);
        if (window is null)
        {
            return;
        }

        var targetPage = Math.Clamp(targetPageIndex, 0, PageCount - 1);
        var targetPosition = Math.Clamp(targetPagePosition, 0, PageSize - 1);
        var pageWindows = GetPageWindows(targetPage).Where(item => item.Id != window.Id).ToList();
        pageWindows.Insert(Math.Clamp(targetPosition, 0, pageWindows.Count), window);

        window.PageIndex = targetPage;
        ReorderPage(targetPage, pageWindows);
        RebuildCurrentPage();
        _setStatus($"宸茬Щ鍔ㄧ獥鍙ｏ細{window.Title}");
    }

    public int CurrentPageWindowCount => GetPageWindows(CurrentPageIndex).Count;

    public void EnsurePageCount(int pageCount)
    {
        _explicitPageCount = Math.Max(1, pageCount);
        CurrentPageIndex = Math.Min(CurrentPageIndex, PageCount - 1);
        RaisePageStateChanged();
    }

    public void RebuildCurrentPage()
    {
        CurrentPageWindows.Clear();
        var pageWindows = GetPageWindows(CurrentPageIndex).Take(PageSize).ToArray();
        var consumedPositions = new HashSet<int>();
        for (var position = 0; position < pageWindows.Length; position++)
        {
            var window = pageWindows[position];
            var canExpand = CanExpandAtPosition(position, pageWindows.Length);
            var isExpanded = canExpand && _expandedWindowIds.Contains(window.Id);
            var rowSpan = isExpanded ? 2 : 1;
            if (isExpanded)
            {
                consumedPositions.Add(position + SerialGridColumns);
            }

            CurrentPageWindows.Add(new SerialWindowSlotViewModel(
                window,
                CurrentPageIndex,
                position,
                SerialGridColumns,
                rowSpan,
                canExpand,
                isExpanded));
        }

        if (pageWindows.Length < PageSize)
        {
            var addPosition = Enumerable.Range(pageWindows.Length, PageSize - pageWindows.Length)
                .FirstOrDefault(position => !consumedPositions.Contains(position), -1);
            if (addPosition >= 0)
            {
                CurrentPageWindows.Add(new SerialWindowSlotViewModel(null, CurrentPageIndex, addPosition, SerialGridColumns));
            }
        }
    }

    private void AddPage()
    {
        _explicitPageCount = PageCount + 1;
        CurrentPageIndex = _explicitPageCount - 1;
        RaisePageStateChanged();
        _setStatus($"已新增页：{PageLabel}");
    }

    private bool CanRemoveCurrentPage()
    {
        return PageCount > 1 && CurrentPageWindowCount == 0;
    }

    private void RemoveCurrentPage()
    {
        if (!CanRemoveCurrentPage())
        {
            return;
        }

        var removedPageIndex = CurrentPageIndex;
        foreach (var window in _serialWindows.Where(window => window.PageIndex > removedPageIndex))
        {
            window.PageIndex--;
        }

        _explicitPageCount = Math.Max(1, PageCount - 1);
        _currentPageIndex = Math.Min(removedPageIndex, _explicitPageCount - 1);
        OnPropertyChanged(nameof(CurrentPageIndex));
        RaisePageStateChanged();
        _setStatus($"已删除空白页：{PageLabel}");
    }

    private void ToggleWindowExpansion(object? parameter)
    {
        if (parameter is not SerialWindowSlotViewModel { Window: { } window } slot)
        {
            return;
        }

        if (slot.IsExpanded)
        {
            _expandedWindowIds.Remove(window.Id);
            RebuildCurrentPage();
            return;
        }

        if (!slot.CanExpand)
        {
            _setStatus("当前窗口下方没有空槽，无法放大");
            return;
        }

        _expandedWindowIds.Add(window.Id);
        RebuildCurrentPage();
    }

    private void SetCommandPanelDock(object? parameter)
    {
        if (parameter is CommandPanelDock dock)
        {
            IsCommandPanelFloating = false;
            CommandPanelDock = dock;
            _setStatus(CommandPanelOrientationLabel);
            return;
        }

        if (parameter is string text && Enum.TryParse<CommandPanelDock>(text, ignoreCase: true, out var parsed))
        {
            IsCommandPanelFloating = false;
            CommandPanelDock = parsed;
            _setStatus(CommandPanelOrientationLabel);
        }
    }

    private void FloatCommandPanel()
    {
        IsCommandPanelFloating = true;
        _setStatus(CommandPanelOrientationLabel);
    }

    private void RestoreCommandPanel()
    {
        CommandPanelDock = CommandPanelDock.Bottom;
        IsCommandPanelFloating = false;
        _setStatus(CommandPanelOrientationLabel);
    }

    private void SerialWindows_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var id in _expandedWindowIds.ToArray())
        {
            if (_serialWindows.All(window => window.Id != id))
            {
                _expandedWindowIds.Remove(id);
            }
        }

        _explicitPageCount = Math.Max(_explicitPageCount, NaturalPageCount);
        OnPropertyChanged(nameof(PageCount));
        CurrentPageIndex = Math.Min(CurrentPageIndex, PageCount - 1);
        OnPropertyChanged(nameof(PageLabel));
        RebuildCurrentPage();
        PreviousPageCommand.RaiseCanExecuteChanged();
        NextPageCommand.RaiseCanExecuteChanged();
        RemoveCurrentPageCommand.RaiseCanExecuteChanged();
    }

    private IReadOnlyList<SerialWindowViewModel> GetPageWindows(int pageIndex)
    {
        return _serialWindows.Where(window => window.PageIndex == pageIndex).ToList();
    }

    private bool CanExpandAtPosition(int position, int pageWindowCount)
    {
        var belowPosition = position + SerialGridColumns;
        return belowPosition < PageSize && belowPosition >= pageWindowCount;
    }

    private void ReorderPage(int pageIndex, IReadOnlyList<SerialWindowViewModel> orderedPageWindows)
    {
        var orderedIds = orderedPageWindows.Select(window => window.Id).ToList();
        var pageWindows = _serialWindows.Where(window => window.PageIndex == pageIndex).ToList();
        foreach (var window in pageWindows)
        {
            var targetOffset = orderedIds.IndexOf(window.Id);
            if (targetOffset < 0)
            {
                continue;
            }

            var targetIndex = CountWindowsBeforePage(pageIndex) + targetOffset;
            var currentIndex = _serialWindows.IndexOf(window);
            if (currentIndex >= 0 && currentIndex != targetIndex)
            {
                _serialWindows.Move(currentIndex, targetIndex);
            }
        }
    }

    private int CountWindowsBeforePage(int pageIndex)
    {
        return _serialWindows.Count(window => window.PageIndex < pageIndex);
    }

    private void RaisePageStateChanged()
    {
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(PageLabel));
        RebuildCurrentPage();
        PreviousPageCommand.RaiseCanExecuteChanged();
        NextPageCommand.RaiseCanExecuteChanged();
        RemoveCurrentPageCommand.RaiseCanExecuteChanged();
    }

    private void RaiseCommandPanelShapeChanged()
    {
        OnPropertyChanged(nameof(IsCommandPanelDockedBottom));
        OnPropertyChanged(nameof(IsCommandPanelDockedTop));
        OnPropertyChanged(nameof(IsCommandPanelDockedLeft));
        OnPropertyChanged(nameof(IsCommandPanelDockedRight));
        OnPropertyChanged(nameof(IsCommandPanelDockedVertical));
        OnPropertyChanged(nameof(IsCommandPanelDockedHorizontal));
        OnPropertyChanged(nameof(CommandPanelDockEdge));
        OnPropertyChanged(nameof(CommandPanelWidth));
        OnPropertyChanged(nameof(CommandPanelHeight));
        OnPropertyChanged(nameof(CommandPanelMargin));
        OnPropertyChanged(nameof(CommandPanelVisibility));
        OnPropertyChanged(nameof(CommandPanelOrientationLabel));
        OnPropertyChanged(nameof(SerialGridRows));
        OnPropertyChanged(nameof(SerialGridColumns));
        RebuildCurrentPage();
    }
}
