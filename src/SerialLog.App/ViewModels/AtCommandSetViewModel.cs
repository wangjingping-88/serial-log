using System.Collections.ObjectModel;
using SerialLog.App.Infrastructure;
using SerialLog.Core.Configuration;

namespace SerialLog.App.ViewModels;

public sealed class AtCommandSetViewModel : ObservableObject
{
    private string _name;

    public AtCommandSetViewModel(string name, IEnumerable<string>? commands = null)
    {
        _name = NormalizeName(name);

        if (commands is null)
        {
            return;
        }

        foreach (var command in commands.Where(command => !string.IsNullOrWhiteSpace(command)))
        {
            Commands.Add(command.Trim());
        }
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, NormalizeName(value));
    }

    public ObservableCollection<string> Commands { get; } = [];

    public AtCommandSetConfig ToConfig()
    {
        return new AtCommandSetConfig
        {
            Name = Name,
            Commands = Commands.ToList()
        };
    }

    public override string ToString()
    {
        return Name;
    }

    private static string NormalizeName(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "命令集" : value.Trim();
    }
}
