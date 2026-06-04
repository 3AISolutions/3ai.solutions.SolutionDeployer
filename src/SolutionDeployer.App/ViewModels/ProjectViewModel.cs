using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SolutionDeployer.Core.Models;

namespace SolutionDeployer.App.ViewModels;

/// <summary>
/// A project node in the tree, owning its publish profiles. Tracks an aggregate selection state so
/// the header checkbox shows checked / unchecked / indeterminate.
/// </summary>
public partial class ProjectViewModel : ObservableObject
{
    private bool _suppressCascade;

    public ProjectViewModel(DeploymentProject project)
    {
        Project = project;
        Profiles = new ObservableCollection<ProfileViewModel>();
    }

    public DeploymentProject Project { get; }

    public string Name => Project.Name;

    public string ProjectPath => Project.ProjectPath;

    public bool HasProfiles => Profiles.Count > 0;

    public ObservableCollection<ProfileViewModel> Profiles { get; }

    /// <summary>Raised whenever a child profile's selection or engine changes.</summary>
    public event Action? SelectionChanged;

    /// <summary>Notify listeners of a state change (e.g. a child engine choice) without recomputing tri-state.</summary>
    public void RaiseStateChanged() => SelectionChanged?.Invoke();

    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>Tri-state: true = all selected, false = none, null = some.</summary>
    [ObservableProperty]
    private bool? _isSelected = false;

    partial void OnIsSelectedChanged(bool? value)
    {
        if (_suppressCascade || value is null)
            return;

        foreach (var profile in Profiles)
            profile.IsSelected = value.Value;
    }

    /// <summary>Recomputes the header state from the children (called by child checkboxes).</summary>
    public void RefreshSelectionState()
    {
        if (Profiles.Count == 0)
            return;

        var selectedCount = Profiles.Count(p => p.IsSelected);
        _suppressCascade = true;
        IsSelected = selectedCount == 0 ? false
            : selectedCount == Profiles.Count ? true
            : null;
        _suppressCascade = false;
        SelectionChanged?.Invoke();
    }
}
