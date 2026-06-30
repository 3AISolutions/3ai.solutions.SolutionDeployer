using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SolutionDeployer.Core.Models;

namespace SolutionDeployer.App.ViewModels;

/// <summary>
/// A selectable publish profile. Carries its own engine choice and (non-persisted) credentials so
/// that any combination of profiles can be queued with per-target settings.
/// </summary>
public partial class ProfileViewModel : ObservableObject, ISelectableTarget
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
        _engine = defaultEngine;
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

    public string Name => Profile.Name;

    public string FormatLabel => Profile.Format == PublishProfileFormat.PublishSettings ? ".PublishSettings" : ".pubxml";

    public string Method => Profile.WebPublishMethod ?? "—";

    public string Target => Profile.ServerUrl ?? Profile.SiteName ?? string.Empty;

    public bool RequiresCredentials => Profile.RequiresCredentials;

    /// <summary>Whether this profile's deployment target can be snapshotted/restored.</summary>
    [ObservableProperty]
    private bool _supportsBackup;

    /// <summary>Previously-captured snapshots for this profile, newest first.</summary>
    public ObservableCollection<BackupEntryViewModel> Backups { get; } = [];

    public bool HasBackups => Backups.Count > 0;

    /// <summary>The snapshot chosen in the restore picker.</summary>
    [ObservableProperty]
    private BackupEntryViewModel? _selectedBackup;

    public void SetBackups(IEnumerable<BackupEntryViewModel> entries)
    {
        Backups.Clear();
        foreach (var entry in entries)
            Backups.Add(entry);
        SelectedBackup = Backups.FirstOrDefault();
        OnPropertyChanged(nameof(HasBackups));
    }

    /// <summary>Engines selectable per profile (bound by the row's ComboBox).</summary>
    public static IReadOnlyList<PublishEngineKind> Engines { get; } =
        [PublishEngineKind.Dotnet, PublishEngineKind.MsBuild];

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
    partial void OnEngineChanged(PublishEngineKind value) => Parent.RaiseStateChanged();

    public PublishCredentials BuildCredentials() => new()
    {
        UserName = string.IsNullOrWhiteSpace(UserName) ? null : UserName,
        Password = string.IsNullOrEmpty(Password) ? null : Password,
    };
}
