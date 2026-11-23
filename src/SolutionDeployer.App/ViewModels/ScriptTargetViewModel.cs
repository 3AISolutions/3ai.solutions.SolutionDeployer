using CommunityToolkit.Mvvm.ComponentModel;
using SolutionDeployer.Core.Backup;
using SolutionDeployer.Core.Configuration;
using SolutionDeployer.Core.Models;
using SolutionDeployer.Core.Publishing;

namespace SolutionDeployer.App.ViewModels;

/// <summary>A selectable script-deployment row under a project.</summary>
public partial class ScriptTargetViewModel : BackupHostViewModel, ISelectableTarget, ICredentialTarget
{
    // Status and ResultText (below) satisfy ISelectableTarget.
    public ScriptTargetViewModel(
        ProjectViewModel parent,
        ScriptTarget target,
        string? rememberedUserName = null,
        string? rememberedPassword = null,
        bool credentialStoreAvailable = false)
    {
        Parent = parent;
        Target = target;
        _userName = rememberedUserName ?? string.Empty;
        CredentialStoreAvailable = credentialStoreAvailable;
        if (rememberedPassword is not null)
        {
            _password = rememberedPassword;
            _rememberPassword = true;
        }

        RefreshAvailability();
    }

    public ProjectViewModel Parent { get; }

    public ScriptTarget Target { get; private set; }

    /// <summary>Whether a secure OS credential store exists (controls the "remember" checkbox).</summary>
    public bool CredentialStoreAvailable { get; }

    public bool RequiresCredentials => Target.RequiresCredentials;

    /// <summary>Show the remember-password checkbox only for credentialed scripts when storage exists.</summary>
    public bool CanRememberPassword => RequiresCredentials && CredentialStoreAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CredentialsSummary))]
    private string _userName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CredentialsSummary), nameof(CredentialsReady), nameof(NeedsPassword))]
    private string _password = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CredentialsSummary))]
    private bool _rememberPassword;

    /// <summary>The library entry this script runs with, or null for its own credentials.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CredentialsSummary))]
    private SavedCredential? _credentialEntry;

    public string CredentialKey => Target.CredentialKey;

    public string? DefaultUserName => null;

    /// <summary>Who the script runs as, shown at the top of the row menu.</summary>
    public string CredentialsSummary => CredentialText.Summarize(UserName, Password, RememberPassword, CredentialEntry);

    public bool CredentialsReady => !RequiresCredentials || !string.IsNullOrEmpty(Password);

    /// <summary>A credentialed script without a password: the row offers to set one.</summary>
    public bool NeedsPassword => !CredentialsReady;

    public void ApplyCredentials(ResolvedCredentials credentials, bool rememberPassword)
    {
        CredentialEntry = credentials.Entry;
        UserName = credentials.UserName;
        Password = credentials.Password ?? string.Empty;
        RememberPassword = rememberPassword && !string.IsNullOrEmpty(Password);
    }

    public override string Name => Target.Name;

    public override BackupOwner BackupOwner => BackupOwner.ForScript(Target, Parent.ProjectDirectory);

    public string ScriptPath => Target.ScriptPath;

    public string? Arguments => Target.Arguments;

    public string Summary =>
        string.IsNullOrWhiteSpace(Target.Arguments) ? Target.ScriptPath : $"{Target.ScriptPath}  {Target.Arguments}";

    /// <summary>False when hidden by the active filter.</summary>
    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private PublishStatus _status = PublishStatus.Pending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isQueued;

    public string StatusText => TargetStatusText.Describe(Status, IsQueued);

    [ObservableProperty]
    private string _resultText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string? _problem;

    public bool HasProblem => !string.IsNullOrEmpty(Problem);

    partial void OnIsSelectedChanged(bool value) => Parent.RefreshSelectionState();

    /// <summary>Replaces the underlying target after editing and refreshes the row.</summary>
    public void Update(ScriptTarget target)
    {
        Target = target;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(ScriptPath));
        OnPropertyChanged(nameof(Arguments));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(RequiresCredentials));
        OnPropertyChanged(nameof(CanRememberPassword));
        OnPropertyChanged(nameof(CredentialsReady));
        OnPropertyChanged(nameof(NeedsPassword));
        RefreshAvailability();
    }

    /// <summary>Credentials passed to the script in its configured username/password variables (none unless opted in).</summary>
    public override PublishCredentials BuildCredentials() => !RequiresCredentials
        ? PublishCredentials.None
        : new()
        {
            UserName = string.IsNullOrWhiteSpace(UserName) ? null : UserName,
            Password = string.IsNullOrEmpty(Password) ? null : Password,
        };

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
