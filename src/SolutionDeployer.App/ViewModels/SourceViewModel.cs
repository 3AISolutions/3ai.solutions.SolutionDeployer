using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SolutionDeployer.Core.Models;

namespace SolutionDeployer.App.ViewModels;

/// <summary>
/// A top-level node in the tree: an added solution or standalone project, grouping its projects.
/// </summary>
public partial class SourceViewModel : ObservableObject
{
    public SourceViewModel(DeploymentSource source)
    {
        Source = source;
        Projects.CollectionChanged += (_, _) => OnProjectsChanged();
    }

    /// <summary>
    /// A source with exactly one project shows that project's header controls (checkbox, "+ Script",
    /// profile count) itself, and the project renders without its own header.
    /// </summary>
    public bool IsSingleProject => Projects.Count == 1;

    public ProjectViewModel? SingleProject => IsSingleProject ? Projects[0] : null;

    /// <summary>What follows the source name in its header: the project count, or for a single project its
    /// name (when it differs from the source's) and profile count.</summary>
    public string HeaderDetail => SingleProject is { } project
        ? (string.Equals(project.Name, Name, StringComparison.OrdinalIgnoreCase) ? "" : $"{project.Name} · ")
          + $"{project.Profiles.Count} profiles"
        : $"({Projects.Count})";

    private void OnProjectsChanged()
    {
        foreach (var project in Projects)
            project.IsFlattened = IsSingleProject;

        OnPropertyChanged(nameof(IsSingleProject));
        OnPropertyChanged(nameof(SingleProject));
        OnPropertyChanged(nameof(HeaderDetail));
    }

    public DeploymentSource Source { get; }

    public string Name => Source.Name;

    public string Path => Source.Path;

    public SourceKind Kind => Source.Kind;

    public string KindLabel => Kind == SourceKind.Solution ? "Solution" : "Project";

    public ObservableCollection<ProjectViewModel> Projects { get; } = [];

    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>False when hidden by the active filter.</summary>
    [ObservableProperty]
    private bool _isVisible = true;

    /// <summary>Non-null when the source is missing on disk or failed to load (shown in the header).</summary>
    [ObservableProperty]
    private string? _problem;

    public bool HasProblem => !string.IsNullOrEmpty(Problem);

    partial void OnProblemChanged(string? value) => OnPropertyChanged(nameof(HasProblem));
}
