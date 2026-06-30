using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolutionDeployer.Core.Git;

namespace SolutionDeployer.App.ViewModels;

/// <summary>Read-only view of a <see cref="ReleaseSummary"/> for the release-summary window.</summary>
public partial class ReleaseSummaryViewModel(ReleaseSummary summary) : ObservableObject
{
    public string Title => $"Release summary — {summary.DeployedProjectName}";

    public IReadOnlyList<ProjectHistory> Projects => summary.Projects;

    /// <summary>Plain-text rendering, used for copy-to-clipboard.</summary>
    public string PlainText => summary.ToPlainText();

    public event Action? CloseRequested;

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();
}
