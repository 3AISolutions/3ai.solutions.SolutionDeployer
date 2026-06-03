using SolutionDeployer.Core.Models;

namespace SolutionDeployer.App.ViewModels;

/// <summary>A single line in the live output console.</summary>
public sealed class LogLine(string text, bool isError)
{
    public string Text { get; } = text;

    public bool IsError { get; } = isError;

    public static LogLine From(string jobName, OutputLine line) =>
        new($"[{jobName}] {line.Text}", line.Severity == OutputSeverity.Error);

    public static LogLine System(string text) => new(text, false);
}
