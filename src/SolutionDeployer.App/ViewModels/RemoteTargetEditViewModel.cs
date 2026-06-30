using CommunityToolkit.Mvvm.ComponentModel;
using SolutionDeployer.Core.Configuration;

namespace SolutionDeployer.App.ViewModels;

/// <summary>Editable form state for one S3-compatible backup destination.</summary>
public partial class RemoteTargetEditViewModel : ObservableObject
{
    public RemoteTargetEditViewModel(S3BackupTarget target, string? secretKey)
    {
        Id = target.Id;
        _name = target.Name;
        _serviceUrl = target.ServiceUrl;
        _region = target.Region;
        _bucket = target.Bucket;
        _prefix = target.Prefix;
        _accessKey = target.AccessKey;
        _secretKey = secretKey ?? string.Empty;
        _forcePathStyle = target.ForcePathStyle;
    }

    public string Id { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string _name;

    [ObservableProperty]
    private string _serviceUrl;

    [ObservableProperty]
    private string _region;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string _bucket;

    [ObservableProperty]
    private string _prefix;

    [ObservableProperty]
    private string _accessKey;

    [ObservableProperty]
    private string _secretKey;

    [ObservableProperty]
    private bool _forcePathStyle;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? (string.IsNullOrWhiteSpace(Bucket) ? "(new remote)" : Bucket) : Name;

    /// <summary>The persisted form (without the secret, which goes to the credential store).</summary>
    public S3BackupTarget ToTarget() => new()
    {
        Id = Id,
        Name = Name.Trim(),
        ServiceUrl = ServiceUrl.Trim(),
        Region = string.IsNullOrWhiteSpace(Region) ? "us-east-1" : Region.Trim(),
        Bucket = Bucket.Trim(),
        Prefix = Prefix.Trim(),
        AccessKey = AccessKey.Trim(),
        ForcePathStyle = ForcePathStyle,
    };
}
