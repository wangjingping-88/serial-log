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
    private bool _isCommandPanelHidden;

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
        ToggleCommandPanelVisibilityCommand = new RelayCommand(ToggleCommandPanelVisibility);

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

    public RelayCommand ToggleCommandPanelVisibilityCommand { get; }

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

    public bool IsCommandPanelHidden
    {
        get => _isCommandPanelHidden;
        set
        {
            if (!SetProperty(ref _isCommandPanelHidden, value))
            {
                return;
            }

            if (value && IsCommandPanelFloating)
            {
                _isCommandPanelFloating = false;
                OnPropertyChanged(nameof(IsCommandPanelFloating));
                FloatCommandPanelCommand.RaiseCanExecuteChanged();
                RestoreCommandPanelCommand.RaiseCanExecuteChanged();
            }

            RaiseCommandPanelShapeChanged();
        }
    }

    public string CommandPanelVisibilityActionText => IsCommandPanelHidden ? "显示" : "隐藏";

    private bool IsCommandPanelDocked => !IsCommandPanelFloating && !IsCommandPanelHidden;

    public bool IsCommandPanelVerticalShape =>
        IsCommandPanelDocked && (CommandPanelDock is CommandPanelDock.Left or CommandPanelDock.Right);

    public bool IsCommandPanelHorizontalShape => !IsCommandPanelVerticalShape;

    public bool IsCommandPanelDockedBottom => IsCommandPanelDocked && CommandPanelDock == CommandPanelDock.Bottom;

    public bool IsCommandPanelDockedTop => IsCommandPanelDocked && CommandPanelDock == CommandPanelDock.Top;

    public bool IsCommandPanelDockedLeft => IsCommandPanelDocked && CommandPanelDock == CommandPanelDock.Left;

    public bool IsCommandPanelDockedRight => IsCommandPanelDocked && CommandPanelDock == CommandPanelDock.Right;

    public bool IsCommandPanelDockedVertical => IsCommandPanelDocked && IsCommandPanelVerticalShape;

    public bool IsCommandPanelDockedHorizontal => IsCommandPanelDocked && IsCommandPanelHorizontalShape;

    public int SerialGridRows => IsCommandPanelDockedVertical ? 3 : 2;

    public int SerialGridColumns => IsCommandPanelDockedVertical ? 2 : 3;

    public Dock CommandPanelDockEdge => CommandPanelDock switch
    {
        CommandPanelDock.Top => Dock.Top,
        CommandPanelDock.Left => Dock.Left,
        CommandPanelDock.Right => Dock.Right,
        _ => Dock.Bottom
    };

    public double CommandPanelWidth => IsCommandPanelFloating || IsCommandPanelHidden ? 0 : IsCommandPanelDockedVertical ? 540 : double.NaN;

    public double CommandPanelHeight => IsCommandPanelFloating || IsCommandPanelHidden ? 0 : IsCommandPanelDockedHorizontal ? 300 : double.NaN;

    public double FloatingCommandPanelWidth => 1180;

    public double FloatingCommandPanelHeight => 360;

    public double FloatingCommandPanelMinWidth => 900;

    public double FloatingCommandPanelMinHeight => 300;

    public Thickness CommandPanelMargin => IsCommandPanelFloating || IsCommandPanelHidden ? new Thickness(0) : CommandPanelDock switch
    {
        CommandPanelDock.Top => new Thickness(0, 12, 0, 8),
        CommandPanelDock.Left => new Thickness(0, 12, 8, 12),
        CommandPanelDock.Right => new Thickness(8, 12, 0, 12),
        _ => new Thickness(0, 4, 0, 8)
    };

    public Visibility CommandPanelVisibility => IsCommandPanelFloating || IsCommandPanelHidden ? Visibility.Collapsed : Visibility.Visible;

    public string CommandPanelOrientationLabel => IsCommandPanelHidden ? "命令区：隐藏" : IsCommandPanelFloating ? "命令区：浮动" : CommandPanelDock switch
    {
        CommandPanelDock.Top => "命令区：顶部",
        CommandPanelDock.Left => "命令区：左侧",
        CommandPanelDock.Right => "命令区：右侧",
        _ => "命令区：底部"
    };

    public IReadOnlyList<string> ExpandedWindowIds => _expandedWindowIds.ToArray();

    public void RestoreExpandedWindowIds(IEnumerable<string>? expandedWindowIds)
    {
        _expandedWindowIds.Clear();
        if (expandedWindowIds is not null)
        {
            foreach (var id in expandedWindowIds.Where(id => !string.IsNullOrWhiteSpace(id)))
            {
                _expandedWindowIds.Add(id);
            }
        }

        RebuildCurrentPage();
    }

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
        NormalizePagePositions(_serialWindows[clampedTarget].PageIndex);
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

        window.PageIndex = targetPage;
        window.PagePosition = targetPosition;
        if (pageWindows.Any(item => item.PagePosition == targetPosition))
        {
            pageWindows.Insert(Math.Clamp(targetPosition, 0, pageWindows.Count), window);
            ReorderPage(targetPage, pageWindows);
        }

        RebuildCurrentPage();
        _setStatus($"宸茬Щ鍔ㄧ獥鍙ｏ細{window.Title}");
    }

    public int CurrentPageWindowCount => GetPageWindows(CurrentPageIndex).Count;

    public bool CurrentPageHasFreeSlot => GetFreePagePositions(CurrentPageIndex).Count > 0;

    public SerialWindowSlotViewModel GetFirstFreeSlot(int pageIndex)
    {
        var page = Math.Clamp(pageIndex, 0, Math.Max(0, PageCount - 1));
        var position = GetFreePagePositions(page).FirstOrDefault(0);
        return new SerialWindowSlotViewModel(null, page, position, SerialGridColumns);
    }

    public void EnsurePageCount(int pageCount)
    {
        _explicitPageCount = Math.Max(1, pageCount);
        CurrentPageIndex = Math.Min(CurrentPageIndex, PageCount - 1);
        RaisePageStateChanged();
    }

    public void RebuildCurrentPage()
    {
        CurrentPageWindows.Clear();
        var pageWindows = GetAssignedPageWindows(CurrentPageIndex).ToArray();
        var occupiedPositions = new HashSet<int>();
        var visiblePositions = new HashSet<int>();
        var visibleWindows = new List<SerialWindowSlotViewModel>();

        foreach (var item in pageWindows)
        {
            var position = item.Position;
            if (!_expandedWindowIds.Contains(item.Window.Id) && occupiedPositions.Contains(position))
            {
                var openPosition = Enumerable.Range(0, PageSize)
                    .FirstOrDefault(candidate => !occupiedPositions.Contains(candidate) && !visiblePositions.Contains(candidate), -1);
                if (openPosition < 0)
                {
                    continue;
                }

                position = openPosition;
            }

            var canExpand = CanExpandAtPosition(position, pageWindows);
            var wantsExpanded = _expandedWindowIds.Contains(item.Window.Id);
            var isExpanded = wantsExpanded &&
                (canExpand || CanPreserveExpansionAtPosition(position, pageWindows, item.Window.Id));
            var rowSpan = isExpanded ? GetExpandedRowSpan(position) : 1;
            foreach (var occupiedPosition in GetOccupiedPositions(position, rowSpan))
            {
                occupiedPositions.Add(occupiedPosition);
            }

            visiblePositions.Add(position);
            visibleWindows.Add(new SerialWindowSlotViewModel(
                item.Window,
                CurrentPageIndex,
                position,
                SerialGridColumns,
                rowSpan,
                canExpand,
                isExpanded));
        }

        foreach (var slot in visibleWindows)
        {
            CurrentPageWindows.Add(slot);
        }

        foreach (var addPosition in Enumerable.Range(0, PageSize).Where(position => !occupiedPositions.Contains(position)))
        {
            if (visibleWindows.All(slot => slot.PagePosition != addPosition))
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
            RaiseSerialGridStateChanged();
            return;
        }

        if (!slot.CanExpand)
        {
            _setStatus("当前窗口下方没有空槽，无法放大");
            return;
        }

        _expandedWindowIds.Add(window.Id);
        RaiseSerialGridStateChanged();
    }

    private void SetCommandPanelDock(object? parameter)
    {
        if (parameter is CommandPanelDock dock)
        {
            IsCommandPanelFloating = false;
            IsCommandPanelHidden = false;
            CommandPanelDock = dock;
            _setStatus(CommandPanelOrientationLabel);
            return;
        }

        if (parameter is string text && Enum.TryParse<CommandPanelDock>(text, ignoreCase: true, out var parsed))
        {
            IsCommandPanelFloating = false;
            IsCommandPanelHidden = false;
            CommandPanelDock = parsed;
            _setStatus(CommandPanelOrientationLabel);
        }
    }

    private void FloatCommandPanel()
    {
        IsCommandPanelHidden = false;
        IsCommandPanelFloating = true;
        _setStatus(CommandPanelOrientationLabel);
    }

    private void RestoreCommandPanel()
    {
        IsCommandPanelFloating = false;
        _setStatus(CommandPanelOrientationLabel);
    }

    private void ToggleCommandPanelVisibility()
    {
        IsCommandPanelHidden = !IsCommandPanelHidden;
        _setStatus(IsCommandPanelHidden ? "命令区：隐藏" : CommandPanelOrientationLabel);
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

    private IReadOnlyList<PageWindowPlacement> GetAssignedPageWindows(int pageIndex)
    {
        var usedPositions = new HashSet<int>();
        var result = new List<PageWindowPlacement>();
        foreach (var window in GetPageWindows(pageIndex).Take(PageSize))
        {
            var position = window.PagePosition;
            if (position < 0 || position >= PageSize || !usedPositions.Add(position))
            {
                position = Enumerable.Range(0, PageSize).First(item => usedPositions.Add(item));
                window.PagePosition = position;
            }

            result.Add(new PageWindowPlacement(window, position));
        }

        return result.OrderBy(item => item.Position).ToArray();
    }

    private IReadOnlyList<int> GetFreePagePositions(int pageIndex)
    {
        var usedPositions = GetAssignedPageWindows(pageIndex)
            .Select(item => item.Position)
            .ToHashSet();
        return Enumerable.Range(0, PageSize)
            .Where(position => !usedPositions.Contains(position))
            .ToArray();
    }

    private int GetExpandedRowSpan(int position)
    {
        var remainingRows = SerialGridRows - position / SerialGridColumns;
        var requestedRows = IsCommandPanelHidden ? SerialGridRows : 2;
        return Math.Min(requestedRows, remainingRows);
    }

    private IEnumerable<int> GetOccupiedPositions(int position, int rowSpan)
    {
        var column = position % SerialGridColumns;
        var row = position / SerialGridColumns;
        return Enumerable.Range(row, rowSpan)
            .Select(occupiedRow => occupiedRow * SerialGridColumns + column)
            .Where(occupiedPosition => occupiedPosition is >= 0 and < PageSize);
    }

    private bool CanExpandAtPosition(int position, IReadOnlyList<PageWindowPlacement> pageWindows)
    {
        var rowSpan = GetExpandedRowSpan(position);
        if (rowSpan <= 1)
        {
            return false;
        }

        var occupiedPositions = GetOccupiedPositions(position, rowSpan).Skip(1);
        return occupiedPositions.All(occupiedPosition => pageWindows.All(item => item.Position != occupiedPosition));
    }

    private bool CanPreserveExpansionAtPosition(
        int position,
        IReadOnlyList<PageWindowPlacement> pageWindows,
        string expandedWindowId)
    {
        var rowSpan = GetExpandedRowSpan(position);
        if (rowSpan <= 1)
        {
            return false;
        }

        var expandedPositions = GetOccupiedPositions(position, rowSpan).ToHashSet();
        var blockers = pageWindows
            .Where(item => item.Window.Id != expandedWindowId && expandedPositions.Contains(item.Position))
            .ToArray();
        if (blockers.Length == 0)
        {
            return true;
        }

        var usedPositions = pageWindows
            .Where(item => item.Window.Id != expandedWindowId)
            .Select(item => item.Position)
            .ToHashSet();
        var freePositions = Enumerable.Range(0, PageSize)
            .Count(candidate => !expandedPositions.Contains(candidate) && !usedPositions.Contains(candidate));

        return freePositions >= blockers.Length;
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

            window.PagePosition = Math.Clamp(targetOffset, 0, PageSize - 1);
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

    private void NormalizePagePositions(int pageIndex)
    {
        var position = 0;
        foreach (var window in GetPageWindows(pageIndex).Take(PageSize))
        {
            window.PagePosition = position++;
        }
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

    private void RaiseSerialGridStateChanged()
    {
        OnPropertyChanged(nameof(SerialGridRows));
        OnPropertyChanged(nameof(SerialGridColumns));
        RebuildCurrentPage();
    }

    private void RaiseCommandPanelShapeChanged()
    {
        OnPropertyChanged(nameof(IsCommandPanelDockedBottom));
        OnPropertyChanged(nameof(IsCommandPanelDockedTop));
        OnPropertyChanged(nameof(IsCommandPanelDockedLeft));
        OnPropertyChanged(nameof(IsCommandPanelDockedRight));
        OnPropertyChanged(nameof(IsCommandPanelHidden));
        OnPropertyChanged(nameof(IsCommandPanelVerticalShape));
        OnPropertyChanged(nameof(IsCommandPanelHorizontalShape));
        OnPropertyChanged(nameof(IsCommandPanelDockedVertical));
        OnPropertyChanged(nameof(IsCommandPanelDockedHorizontal));
        OnPropertyChanged(nameof(CommandPanelDockEdge));
        OnPropertyChanged(nameof(CommandPanelWidth));
        OnPropertyChanged(nameof(CommandPanelHeight));
        OnPropertyChanged(nameof(FloatingCommandPanelWidth));
        OnPropertyChanged(nameof(FloatingCommandPanelHeight));
        OnPropertyChanged(nameof(FloatingCommandPanelMinWidth));
        OnPropertyChanged(nameof(FloatingCommandPanelMinHeight));
        OnPropertyChanged(nameof(CommandPanelMargin));
        OnPropertyChanged(nameof(CommandPanelVisibility));
        OnPropertyChanged(nameof(CommandPanelVisibilityActionText));
        OnPropertyChanged(nameof(CommandPanelOrientationLabel));
        OnPropertyChanged(nameof(SerialGridRows));
        OnPropertyChanged(nameof(SerialGridColumns));
        RebuildCurrentPage();
    }

    private sealed record PageWindowPlacement(SerialWindowViewModel Window, int Position);
}
