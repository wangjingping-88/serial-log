using SerialLog.Update;

namespace SerialLog.Tests;

public sealed class UpdaterEngineTests
{
    [Fact]
    public async Task RunAsync_SwapsDirectoriesAndCommitsAfterStartupConfirmation()
    {
        using var temp = new TemporaryDirectory();
        var fixture = CreateFixture(temp.Path);
        var runtime = new FakeUpdaterProcessRuntime(confirmStartup: true);
        var engine = CreateEngine(runtime, fixture.UpdateRoot);

        var result = await engine.RunAsync(fixture.JobFile);

        Assert.Equal(0, result);
        Assert.Equal("new", File.ReadAllText(Path.Combine(fixture.InstallDirectory, UpdatePaths.ApplicationFileName)));
        Assert.False(Directory.Exists(fixture.BackupDirectory));
        Assert.Single(runtime.StartedApplications);
    }

    [Fact]
    public async Task RunAsync_RollsBackAndRestartsOldVersionWhenNewVersionExitsEarly()
    {
        using var temp = new TemporaryDirectory();
        var fixture = CreateFixture(temp.Path);
        var runtime = new FakeUpdaterProcessRuntime(confirmStartup: false);
        var engine = CreateEngine(runtime, fixture.UpdateRoot);

        var result = await engine.RunAsync(fixture.JobFile);

        Assert.Equal(3, result);
        Assert.Equal("old", File.ReadAllText(Path.Combine(fixture.InstallDirectory, UpdatePaths.ApplicationFileName)));
        Assert.Equal(2, runtime.StartedApplications.Count);
        var state = new UpdateStateStore(fixture.StateFile).Load();
        Assert.Null(state.PendingUpdate);
    }

    [Fact]
    public async Task RunAsync_RejectsJobOutsideConfiguredUpdateRootWithoutStartingProcess()
    {
        using var temp = new TemporaryDirectory();
        var fixture = CreateFixture(temp.Path);
        var runtime = new FakeUpdaterProcessRuntime(confirmStartup: true);
        var otherRoot = Path.Combine(temp.Path, "other-updates");
        Directory.CreateDirectory(otherRoot);
        var engine = CreateEngine(runtime, otherRoot);

        var result = await engine.RunAsync(fixture.JobFile);

        Assert.Equal(1, result);
        Assert.Empty(runtime.StartedApplications);
        Assert.Equal("old", File.ReadAllText(Path.Combine(fixture.InstallDirectory, UpdatePaths.ApplicationFileName)));
    }

    private static UpdaterEngine CreateEngine(FakeUpdaterProcessRuntime runtime, string updateRoot)
    {
        return new UpdaterEngine(
            runtime,
            updateRoot,
            applicationExitTimeout: TimeSpan.FromMilliseconds(20),
            startupConfirmationTimeout: TimeSpan.FromMilliseconds(20),
            confirmationPollInterval: TimeSpan.FromMilliseconds(1));
    }

    private static UpdateFixture CreateFixture(string root)
    {
        var install = Path.Combine(root, "SerialLog");
        var stage = Path.Combine(root, ".serial-log-stage-test");
        var backup = Path.Combine(root, ".serial-log-backup-test");
        var updateRoot = Path.Combine(root, "updates");
        var jobDirectory = Path.Combine(updateRoot, "jobs", "test");
        var jobFile = Path.Combine(jobDirectory, "update-job.json");
        var stateFile = Path.Combine(updateRoot, "update-state.json");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(install, UpdatePaths.ApplicationFileName), "old");
        File.WriteAllText(Path.Combine(stage, UpdatePaths.ApplicationFileName), "new");
        new UpdateStateStore(stateFile).Save(new UpdateState
        {
            PendingUpdate = new PendingUpdateState { TargetVersion = "0.3.0" }
        });
        UpdateJobStore.Save(jobFile, new UpdateJob
        {
            CurrentProcessId = 123,
            InstallDirectory = install,
            StagingDirectory = stage,
            BackupDirectory = backup,
            TargetVersion = "0.3.0",
            ConfirmationFile = Path.Combine(jobDirectory, "confirmed.txt"),
            LogFilePath = Path.Combine(updateRoot, "updater.log"),
            UpdateStateFilePath = stateFile
        });
        return new UpdateFixture(install, backup, updateRoot, jobFile, stateFile);
    }

    private sealed record UpdateFixture(
        string InstallDirectory,
        string BackupDirectory,
        string UpdateRoot,
        string JobFile,
        string StateFile);

    private sealed class FakeUpdaterProcessRuntime : IUpdaterProcessRuntime
    {
        private readonly bool _confirmStartup;

        public FakeUpdaterProcessRuntime(bool confirmStartup)
        {
            _confirmStartup = confirmStartup;
        }

        public List<string> StartedApplications { get; } = [];

        public Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public int Start(string executablePath, IReadOnlyList<string> arguments, string workingDirectory)
        {
            StartedApplications.Add(executablePath);
            if (_confirmStartup && arguments.Count == 2)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(arguments[1])!);
                File.WriteAllText(arguments[1], "confirmed");
            }

            return StartedApplications.Count + 100;
        }

        public bool HasExited(int processId) => !_confirmStartup;

        public void Kill(int processId)
        {
        }
    }
}
