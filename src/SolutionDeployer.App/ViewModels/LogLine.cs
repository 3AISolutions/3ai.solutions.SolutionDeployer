using Avalonia.Media;
using SolutionDeployer.Core.Models;

namespace SolutionDeployer.App.ViewModels;

/// <summary>
/// A single line in the live output console. Lines a target wrote carry that target (its row), so the output
/// can show one target's lines only, and a coloured <see cref="Prefix"/> naming it; run-wide lines have none.
/// </summary>
public sealed class LogLine
{
    public LogLine(string text, bool isError, BackupHostViewModel? target = null, string? targetName = null,
        IBrush? targetBrush = null)
    {
        Text = text;
        IsError = isError;
        Target = target;
        Prefix = target is null ? string.Empty : $"[{targetName ?? target.Name}] ";
        TargetBrush = targetBrush;
    }

    /// <summary>The message, without the target prefix.</summary>
    public string Text { get; }

    public bool IsError { get; }

    /// <summary>The row this line belongs to; null for run-wide lines (headings, totals, errors).</summary>
    public BackupHostViewModel? Target { get; }

    /// <summary>"[project → target] " for a target's line, empty otherwise.</summary>
    public string Prefix { get; }

    /// <summary>The colour the output gives this line's target, so interleaved parallel output stays readable.</summary>
    public IBrush? TargetBrush { get; }

    public static LogLine System(string text) => new(text, false);

    /// <summary>"Project → Profile" (or script): profile names like "Prod" repeat across projects.</summary>
    public static string NameOf(BackupHostViewModel target) => target switch
    {
        ProfileViewModel p => $"{p.Parent.Name} → {p.Name}",
        ScriptTargetViewModel s => $"{s.Parent.Name} → {s.Name}",
        _ => target.Name,
    };

    public override string ToString() => Prefix + Text;
}
