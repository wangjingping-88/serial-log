using SerialLog.App.Infrastructure;

namespace SerialLog.App.ViewModels;

public sealed class TargetSelectionViewModel : ObservableObject
{
    private bool _isSelected;
    private string _title;

    public TargetSelectionViewModel(string targetId, string title, bool isSelected = false)
    {
        TargetId = targetId;
        _title = title;
        _isSelected = isSelected;
    }

    public string TargetId { get; }

    public string Title { get => _title; internal set => SetProperty(ref _title, value); }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
