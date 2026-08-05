using SerialLog.Update;

var arguments = Environment.GetCommandLineArgs();
var jobIndex = Array.FindIndex(
    arguments,
    argument => string.Equals(argument, "--job", StringComparison.OrdinalIgnoreCase));
if (jobIndex < 0 || jobIndex + 1 >= arguments.Length)
{
    Environment.ExitCode = 64;
    return;
}

var engine = new UpdaterEngine();
Environment.ExitCode = await engine.RunAsync(arguments[jobIndex + 1]);
