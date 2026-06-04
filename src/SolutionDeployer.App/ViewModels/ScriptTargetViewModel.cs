using CommunityToolkit.Mvvm.ComponentModel;
using SolutionDeployer.Core.Models;
using SolutionDeployer.Core.Publishing;

namespace SolutionDeployer.App.ViewModels;

/// <summary>A selectable script-deployment row under a project.</summary>
public partial class ScriptTargetViewModel : ObservableObject, ISelectableTarget
{
    // Status and ResultText (below) satisfy ISelectableTarget.
    public ScriptTargetViewModel(ProjectViewModel parent, ScriptTarget target)
    {
        Parent = parent;
        Target = target;
        RefreshAvailability();
    }

    public ProjectViewModel Parent { get; }

    public ScriptTarget Target { get; private set; }

    public string Name => Target.Name;

    public string ScriptPath => Target.ScriptPath;

    public string? Arguments => Target.Arguments;

    public string Summary =>
        string.IsNullOrWhiteSpace(Target.Arguments) ? Target.ScriptPath : $"{Target.ScriptPath}  {Target.Arguments}";

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusGlyph))]
    private PublishStatus _status = PublishStatus.Pending;

    [ObservableProperty]
    private string _resultText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string? _problem;

    public bool HasProblem => !string.IsNullOrEmpty(Problem);

    public string StatusGlyph => Status switch
    {
        PublishStatus.Running => "…",
        PublishStatus.Succeeded => "✔",
        PublishStatus.Failed => "✘",
        PublishStatus.Cancelled => "⊘",
        _ => "•",
    };

    partial void OnIsSelectedChanged(bool value) => Parent.RefreshSelectionState();

    /// <summary>Replaces the underlying target after editing and refreshes the row.</summary>
    public void Update(ScriptTarget target)
    {
        Target = target;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(ScriptPath));
        OnPropertyChanged(nameof(Arguments));
        OnPropertyChanged(nameof(Summary));
        RefreshAvailability();
    }

    /// <summary>Flags a missing script file or a missing interpreter for display.</summary>
    public void RefreshAvailability()
    {
        var scriptPath = Target.ResolveScriptPath(Parent.ProjectDirectory);
        if (!File.Exists(scriptPath))
            Problem = "Script file not found.";
        else if (!ScriptInterpreters.IsAvailable(scriptPath, out var reason))
            Problem = reason;
        else
            Problem = null;
    }
}
