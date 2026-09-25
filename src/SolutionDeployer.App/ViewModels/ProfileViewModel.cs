using CommunityToolkit.Mvvm.ComponentModel;
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
        _engine = parent.IsClassicWebProject ? PublishEngineKind.MsBuild : defaultEngine;
        _userName = rememberedUserName ?? profile.UserName ?? string.Empty;
        CredentialStoreAvailable = credentialStoreAvailable;
        if (rememberedPassword is not null)
        {
            _password = rememberedPassword;
            _rememberPassword = true;
        }
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
    public IReadOnlyList<PublishEngineKind> Engines => Parent.IsClassicWebProject ? MsBuildOnly : AllEngines;

    public string EngineHint => Parent.IsClassicWebProject
        ? "Classic ASP.NET (.NET Framework) project — only msbuild can build it"
        : "dotnet publish, or full msbuild (needed for .NET Framework / classic Web Deploy projects)";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusGlyph))]
    private PublishStatus _status = PublishStatus.Pending;

    /// <summary>False when hidden by the active filter.</summary>
    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private PublishEngineKind _engine;

    [ObservableProperty]
    private string _userName = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _resultText = string.Empty;

    [ObservableProperty]
    private bool _rememberPassword;

    public string StatusGlyph => Status switch
    {
        PublishStatus.Running => "…",
        PublishStatus.Succeeded => "✔",
        PublishStatus.Failed => "✘",
        PublishStatus.Cancelled => "⊘",
        _ => "•",
    };

    partial void OnIsSelectedChanged(bool value) => Parent.RefreshSelectionState();

    // Persist engine changes too (re-selecting a target with a different engine should be remembered).
    partial void OnEngineChanged(PublishEngineKind value)
    {
        // A saved selection may still say "dotnet" for a classic web project; that can never build.
        if (Parent.IsClassicWebProject && value != PublishEngineKind.MsBuild)
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
