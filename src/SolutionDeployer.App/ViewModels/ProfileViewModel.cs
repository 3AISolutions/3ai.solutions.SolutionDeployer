using CommunityToolkit.Mvvm.ComponentModel;
using SolutionDeployer.Core.Models;

namespace SolutionDeployer.App.ViewModels;

/// <summary>
/// A selectable publish profile. Carries its own engine choice and (non-persisted) credentials so
/// that any combination of profiles can be queued with per-target settings.
/// </summary>
public partial class ProfileViewModel : ObservableObject
{
    public ProfileViewModel(ProjectViewModel parent, PublishProfile profile, PublishEngineKind defaultEngine, string? rememberedUserName)
    {
        Parent = parent;
        Profile = profile;
        _engine = defaultEngine;
        _userName = rememberedUserName ?? profile.UserName ?? string.Empty;
    }

    public ProjectViewModel Parent { get; }

    public PublishProfile Profile { get; }

    public string Name => Profile.Name;

    public string FormatLabel => Profile.Format == PublishProfileFormat.PublishSettings ? ".PublishSettings" : ".pubxml";

    public string Method => Profile.WebPublishMethod ?? "—";

    public string Target => Profile.ServerUrl ?? Profile.SiteName ?? string.Empty;

    public bool RequiresCredentials => Profile.RequiresCredentials;

    /// <summary>Engines selectable per profile (bound by the row's ComboBox).</summary>
    public static IReadOnlyList<PublishEngineKind> Engines { get; } =
        [PublishEngineKind.Dotnet, PublishEngineKind.MsBuild];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusGlyph))]
    private PublishStatus _status = PublishStatus.Pending;

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

    public string StatusGlyph => Status switch
    {
        PublishStatus.Running => "…",
        PublishStatus.Succeeded => "✔",
        PublishStatus.Failed => "✘",
        PublishStatus.Cancelled => "⊘",
        _ => "•",
    };

    partial void OnIsSelectedChanged(bool value) => Parent.RefreshSelectionState();

    public PublishCredentials BuildCredentials() => new()
    {
        UserName = string.IsNullOrWhiteSpace(UserName) ? null : UserName,
        Password = string.IsNullOrEmpty(Password) ? null : Password,
    };
}
