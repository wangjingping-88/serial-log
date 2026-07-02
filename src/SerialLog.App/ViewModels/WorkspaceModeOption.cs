using SerialLog.Core.Configuration;

namespace SerialLog.App.ViewModels;

public sealed record WorkspaceModeOption(WorkspaceMode Mode, string Name)
{
    public override string ToString()
    {
        return Name;
    }
}
