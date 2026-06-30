namespace SerialLog.App.ViewModels;

public sealed class SerialWindowSlotViewModel
{
    public SerialWindowSlotViewModel(SerialWindowViewModel? window)
    {
        Window = window;
    }

    public SerialWindowViewModel? Window { get; }

    public bool IsAddSlot => Window is null;
}
