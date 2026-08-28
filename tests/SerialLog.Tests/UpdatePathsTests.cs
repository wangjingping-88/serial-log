using SerialLog.Update;

namespace SerialLog.Tests;

public sealed class UpdatePathsTests
{
    [Fact]
    public void Update_cache_uses_system_temporary_directory_instead_of_fixed_drive()
    {
        var expectedRoot = Path.Combine(Path.GetTempPath(), "SerialLog");

        Assert.Equal(Path.GetFullPath(expectedRoot), Path.GetFullPath(UpdatePaths.DefaultDataRoot));
        Assert.Equal(Path.Combine(expectedRoot, "updates"), UpdatePaths.DefaultUpdateRoot);
    }
}
