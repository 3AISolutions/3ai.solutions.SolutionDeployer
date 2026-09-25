using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SolutionDeployer.Core.Models;
using SolutionDeployer.Core.Projects;

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
        IsClassicWebProject = ProjectFormat.IsClassicWebProject(project.ProjectPath);
        RequiresMsBuild = ProjectFormat.RequiresMsBuild(project.ProjectPath, out var classicProject);
        MsBuildReason = !RequiresMsBuild ? null
            : IsClassicWebProject ? "Classic ASP.NET (.NET Framework) project — only msbuild can build it"
            : $"References {Path.GetFileName(classicProject)}, a classic .NET Framework project — only msbuild builds it reliably";
        Profiles = new ObservableCollection<ProfileViewModel>();
        ScriptTargets = new ObservableCollection<ScriptTargetViewModel>();

        // Keep the "has scripts/targets" visibility in sync with the collections however they change.
        ScriptTargets.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasScripts));
            OnPropertyChanged(nameof(HasTargets));
        };
        Profiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTargets));
    }

    public DeploymentProject Project { get; }

    /// <summary>
    /// A classic (.NET Framework) ASP.NET project: only full msbuild can build it — <c>dotnet</c> lacks
    /// the Web Application targets (MSB4019).
    /// </summary>
    public bool IsClassicWebProject { get; }

    /// <summary>
    /// Only full msbuild can build it: a classic web project, or one that references a classic .NET
    /// Framework project (dotnet can't resolve that project's NuGet packages).
    /// </summary>
    public bool RequiresMsBuild { get; }

    /// <summary>Why <see cref="RequiresMsBuild"/> is set, for the engine picker's tooltip.</summary>
    public string? MsBuildReason { get; }

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

    /// <summary>
    /// The only project in its source: drawn without its own header (the source header carries its
    /// controls), so it must stay expanded — there'd be no header left to re-open it.
    /// </summary>
    [ObservableProperty]
    private bool _isFlattened;

    partial void OnIsFlattenedChanged(bool value)
    {
        if (value)
            IsExpanded = true;
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (!value && IsFlattened)
            IsExpanded = true;
    }

    /// <summary>False when hidden by the active filter.</summary>
    [ObservableProperty]
    private bool _isVisible = true;

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
