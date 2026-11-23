using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace SolutionDeployer.App.ViewModels;

/// <summary>
/// The output console's lines: all of them (<see cref="All"/>, capped), and what is shown (<see cref="Visible"/>):
/// everything, or only one target's lines when <see cref="Target"/> is set. Each target gets its own prefix colour.
/// </summary>
public sealed class OutputLog(int maxLines)
{
    /// <summary>
    /// Colours for target prefixes, handed out in the order targets are first seen. Mid-tone, so they read on both
    /// the light and the dark theme; red is left for errors.
    /// </summary>
    private static readonly IBrush[] Palette =
    [
        new ImmutableSolidColorBrush(Color.Parse("#3B8EEA")),
        new ImmutableSolidColorBrush(Color.Parse("#B160D8")),
        new ImmutableSolidColorBrush(Color.Parse("#D9822B")),
        new ImmutableSolidColorBrush(Color.Parse("#1FA39A")),
        new ImmutableSolidColorBrush(Color.Parse("#D6478A")),
        new ImmutableSolidColorBrush(Color.Parse("#7F9A2E")),
        new ImmutableSolidColorBrush(Color.Parse("#6F7FD6")),
        new ImmutableSolidColorBrush(Color.Parse("#B8862B")),
    ];

    private readonly Dictionary<BackupHostViewModel, IBrush> _brushes = [];
    private BackupHostViewModel? _target;

    public ObservableCollection<LogLine> All { get; } = [];

    public ObservableCollection<LogLine> Visible { get; } = [];

    /// <summary>The target whose lines alone are shown; null shows every line.</summary>
    public BackupHostViewModel? Target
    {
        get => _target;
        set
        {
            if (ReferenceEquals(_target, value))
                return;
            _target = value;
            Visible.Clear();
            foreach (var line in All)
                if (IsVisible(line))
                    Visible.Add(line);
        }
    }

    /// <summary>The target's prefix colour, assigning the next one on first use.</summary>
    public IBrush BrushFor(BackupHostViewModel target)
    {
        if (!_brushes.TryGetValue(target, out var brush))
            _brushes[target] = brush = Palette[_brushes.Count % Palette.Length];
        return brush;
    }

    /// <summary>A line written by (or about) a target, with its name and colour.</summary>
    public LogLine Line(BackupHostViewModel target, string text, bool isError = false) =>
        new(text, isError, target, LogLine.NameOf(target), BrushFor(target));

    public void Append(LogLine line)
    {
        All.Add(line);
        if (IsVisible(line))
            Visible.Add(line);

        if (All.Count > maxLines)
        {
            var dropped = All[0];
            All.RemoveAt(0);
            if (Visible.Count > 0 && ReferenceEquals(Visible[0], dropped))
                Visible.RemoveAt(0);
        }
    }

    /// <summary>Empties the output and forgets the colours (the next run hands them out afresh).</summary>
    public void Clear()
    {
        All.Clear();
        Visible.Clear();
        _brushes.Clear();
    }

    private bool IsVisible(LogLine line) => _target is null || ReferenceEquals(line.Target, _target);
}
