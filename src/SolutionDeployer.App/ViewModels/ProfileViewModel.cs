using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolutionDeployer.Core.Backup;
using SolutionDeployer.Core.Models;

namespace SolutionDeployer.App.ViewModels;

/// <summary>
/// A selectable publish profile. Carries its own engine choice and (non-persisted) credentials so
/// that any combination of profiles can be queued with per-target settings.
/// </summary>
public partial class ProfileViewModel : BackupHostViewModel, ISelectableTarget
{
    public ProfileViewModel(
        ProjectViewModel parent,
        PublishProfile profile,
        PublishEngineKind defaultEngine,
        string? rememberedUserName,
        string? rememberedPassword,
        bool credentialStoreAvailable)
    {
        Parent = parent;
        Profile = profile;
        _engine = parent.RequiresMsBuild ? PublishEngineKind.MsBuild : defaultEngine;
        _userName = rememberedUserName ?? profile.UserName ?? string.Empty;
        CredentialStoreAvailable = credentialStoreAvailable;
        if (rememberedPassword is not null)
        {
            _password = rememberedPassword;
            _rememberPassword = true;
        }

        // A saved password means there's nothing to fill in, so keep the fields tucked away in the row menu.
        _showCredentials = rememberedPassword is null;
    }

    public ProjectViewModel Parent { get; }

    /// <summary>Whether a secure OS credential store exists (controls the "remember" checkbox).</summary>
    public bool CredentialStoreAvailable { get; }

    /// <summary>Show the remember-password checkbox only for credentialed profiles when storage exists.</summary>
    public bool CanRememberPassword => RequiresCredentials && CredentialStoreAvailable;

    public PublishProfile Profile { get; }

    public override string Name => Profile.Name;

    public override BackupOwner BackupOwner => BackupOwner.ForProfile(Profile, Parent.Project.ProjectDirectory);

    public string FormatLabel => Profile.Format == PublishProfileFormat.PublishSettings ? ".PublishSettings" : ".pubxml";

    public string Method => Profile.WebPublishMethod ?? "—";

    public string Target => Profile.ServerUrl ?? Profile.SiteName ?? string.Empty;

    public bool RequiresCredentials => Profile.RequiresCredentials;

    private static readonly IReadOnlyList<PublishEngineKind> AllEngines = [PublishEngineKind.Dotnet, PublishEngineKind.MsBuild];
    private static readonly IReadOnlyList<PublishEngineKind> MsBuildOnly = [PublishEngineKind.MsBuild];

    /// <summary>Engines selectable for this profile (bound by the row's ComboBox).</summary>
    public IReadOnlyList<PublishEngineKind> Engines => Parent.RequiresMsBuild ? MsBuildOnly : AllEngines;

    public string EngineHint => Parent.RequiresMsBuild
        ? Parent.MsBuildReason!
        : "dotnet publish, or full msbuild (needed for .NET Framework / classic Web Deploy projects)";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private PublishStatus _status = PublishStatus.Pending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isQueued;

    public string StatusText => TargetStatusText.Describe(Status, IsQueued);

    /// <summary>False when hidden by the active filter.</summary>
    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EngineLabel), nameof(MethodSummary), nameof(Details), nameof(IsDotnetEngine), nameof(IsMsBuildEngine))]
    private PublishEngineKind _engine;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CredentialsSummary))]
    private string _userName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CredentialsSummary))]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _resultText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CredentialsSummary))]
    private bool _rememberPassword;

    /// <summary>Whether the username/password fields are expanded on the row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CredentialsMenuText))]
    private bool _showCredentials;

    public string CredentialsMenuText => ShowCredentials ? "Hide credentials" : "Edit credentials…";

    /// <summary>Who the row deploys as, shown at the top of the row menu.</summary>
    public string CredentialsSummary =>
        string.IsNullOrWhiteSpace(UserName) ? "No username set"
        : RememberPassword && !string.IsNullOrEmpty(Password) ? $"{UserName} (password saved)"
        : UserName;

    public string EngineLabel => Engine == PublishEngineKind.MsBuild ? "MSBuild" : "dotnet";

    /// <summary>Publish method plus the engine that will run it, e.g. "MSDeploy · MSBuild".</summary>
    public string MethodSummary => $"{Method} · {EngineLabel}";

    /// <summary>One trimmable line for the row: method, engine and target.</summary>
    public string Details => string.IsNullOrEmpty(Target) ? MethodSummary : $"{MethodSummary} · {Target}";

    public bool IsDotnetEngine => Engine == PublishEngineKind.Dotnet;

    public bool IsMsBuildEngine => Engine == PublishEngineKind.MsBuild;

    public bool CanUseDotnet => !Parent.RequiresMsBuild;

    [RelayCommand]
    private void ToggleCredentials() => ShowCredentials = !ShowCredentials;

    [RelayCommand]
    private void SetEngine(PublishEngineKind engine) => Engine = engine;

    partial void OnIsSelectedChanged(bool value) => Parent.RefreshSelectionState();

    // Persist engine changes too (re-selecting a target with a different engine should be remembered).
    partial void OnEngineChanged(PublishEngineKind value)
    {
        // A saved selection may still say "dotnet" for a classic web project; that can never build.
        if (Parent.RequiresMsBuild && value != PublishEngineKind.MsBuild)
        {
            Engine = PublishEngineKind.MsBuild;
            return;
        }

        Parent.RaiseStateChanged();
    }

    public override PublishCredentials BuildCredentials() => new()
    {
        UserName = string.IsNullOrWhiteSpace(UserName) ? null : UserName,
        Password = string.IsNullOrEmpty(Password) ? null : Password,
    };
}
