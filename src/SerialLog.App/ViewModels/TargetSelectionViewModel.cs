using SerialLog.App.Infrastructure;

namespace SerialLog.App.ViewModels;

public sealed class TargetSelectionViewModel : ObservableObject
{
    private bool _isSelected;

    public TargetSelectionViewModel(string targetId, string title, bool isSelected = false)
    {
        TargetId = targetId;
        Title = title;
        _isSelected = isSelected;
    }

    public string TargetId { get; }

    public string Title { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
