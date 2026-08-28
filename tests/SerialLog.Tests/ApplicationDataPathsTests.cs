using SerialLog.Core.Configuration;

namespace SerialLog.Tests;

public sealed class ApplicationDataPathsTests
{
    [Fact]
    public void Data_directory_is_below_application_directory()
    {
        var applicationDirectory = Path.Combine(Path.GetTempPath(), "serial-log-app-" + Guid.NewGuid().ToString("N"));

        var result = ApplicationDataPaths.GetDataDirectory(applicationDirectory);

        Assert.Equal(Path.Combine(Path.GetFullPath(applicationDirectory), "data"), result);
    }

    [Fact]
    public void Legacy_default_log_directory_is_recognized_case_insensitively()
    {
        Assert.True(ApplicationDataPaths.IsLegacyDefaultLogDirectory(@"d:\SERIAL-LOG-DATA\logs\"));
        Assert.False(ApplicationDataPaths.IsLegacyDefaultLogDirectory(@"D:\other\logs"));
    }

    [Fact]
    public void Legacy_workspace_is_copied_only_when_destination_does_not_exist()
    {
        var root = Path.Combine(Path.GetTempPath(), "serial-log-paths-" + Guid.NewGuid().ToString("N"));
        var legacy = Path.Combine(root, "legacy", "workspace.json");
        var destination = Path.Combine(root, "portable", "data", "workspace.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        File.WriteAllText(legacy, "legacy");

        try
        {
            Assert.True(ApplicationDataPaths.MigrateLegacyWorkspaceIfNeeded(destination, legacy));
            Assert.Equal("legacy", File.ReadAllText(destination));

            File.WriteAllText(legacy, "changed");
            Assert.False(ApplicationDataPaths.MigrateLegacyWorkspaceIfNeeded(destination, legacy));
            Assert.Equal("legacy", File.ReadAllText(destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
