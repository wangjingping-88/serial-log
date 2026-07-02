namespace SerialLog.App.ViewModels;

public sealed record PcColorOption(string Name, string Hex)
{
    public override string ToString()
    {
        return Name;
    }
}
