namespace SolutionDeployer.Core.Models;

public enum OutputSeverity
{
    Info,
    Error,
}

/// <summary>
/// A single line emitted by a publish process (stdout or stderr), tagged with its source job.
/// </summary>
public readonly record struct OutputLine(string Text, OutputSeverity Severity)
{
    public static OutputLine Info(string text) => new(text, OutputSeverity.Info);

    public static OutputLine Error(string text) => new(text, OutputSeverity.Error);
}
