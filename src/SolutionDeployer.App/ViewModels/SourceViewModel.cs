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
