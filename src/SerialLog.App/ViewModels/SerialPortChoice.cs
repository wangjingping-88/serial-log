using SerialLog.App.Infrastructure;

namespace SerialLog.App.ViewModels;

public sealed class SerialPortChoice(string name, string label) : ObservableObject
{
    private string _label = label;
    public string Name { get; } = name;
    public string Label { get => _label; internal set => SetProperty(ref _label, value); }
}
