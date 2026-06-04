using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SolutionDeployer.Core.Models;

namespace SolutionDeployer.App.ViewModels;

/// <summary>
/// A project node in the tree, owning its publish profiles and script targets. Tracks an aggregate
/// selection state so the header checkbox shows checked / unchecked / indeterminate.
/// </summary>
public partial class ProjectViewModel : ObservableObject
{
    private bool _suppressCascade;

    public ProjectViewModel(DeploymentProject project)
    {
        Project = project;
        Profiles = new ObservableCollection<ProfileViewModel>();
        ScriptTargets = new ObservableCollection<ScriptTargetViewModel>();
    }

    public DeploymentProject Project { get; }

    public string Name => Project.Name;

    public string ProjectPath => Project.ProjectPath;

    public string ProjectDirectory => Project.ProjectDirectory;

    public ObservableCollection<ProfileViewModel> Profiles { get; }

    public ObservableCollection<ScriptTargetViewModel> ScriptTargets { get; }

    /// <summary>True when the project has at least one selectable target (profile or script).</summary>
    public bool HasTargets => Profiles.Count > 0 || ScriptTargets.Count > 0;

    public bool HasScripts => ScriptTargets.Count > 0;

    private IEnumerable<ISelectableTarget> SelectableTargets =>
        Profiles.Cast<ISelectableTarget>().Concat(ScriptTargets);

    /// <summary>Raised whenever a child target's selection or engine changes.</summary>
    public event Action? SelectionChanged;

    /// <summary>Notify listeners of a state change (e.g. a child engine choice) without recomputing tri-state.</summary>
    public void RaiseStateChanged() => SelectionChanged?.Invoke();

    public void NotifyScriptsChanged()
    {
        OnPropertyChanged(nameof(HasScripts));
        OnPropertyChanged(nameof(HasTargets));
        RefreshSelectionState();
    }

    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>Tri-state: true = all selected, false = none, null = some.</summary>
    [ObservableProperty]
    private bool? _isSelected = false;

    partial void OnIsSelectedChanged(bool? value)
    {
        if (_suppressCascade || value is null)
            return;

        foreach (var target in SelectableTargets)
            target.IsSelected = value.Value;
    }

    /// <summary>Recomputes the header state from the children (called by child checkboxes).</summary>
    public void RefreshSelectionState()
    {
        var targets = SelectableTargets.ToList();
        if (targets.Count == 0)
        {
            SelectionChanged?.Invoke();
            return;
        }

        var selectedCount = targets.Count(t => t.IsSelected);
        _suppressCascade = true;
        IsSelected = selectedCount == 0 ? false
            : selectedCount == targets.Count ? true
            : null;
        _suppressCascade = false;
        SelectionChanged?.Invoke();
    }
}
