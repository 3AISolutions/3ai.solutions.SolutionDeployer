using System.Text.Json.Serialization;

namespace SolutionDeployer.Core.Configuration;

/// <summary>
/// A named, S3-compatible storage destination for deployment snapshots (AWS S3, MinIO, Backblaze B2,
/// Wasabi, …). The secret key is never stored here — it lives in the OS credential store under
/// <see cref="SecretCredentialKey"/>.
/// </summary>
public sealed class S3BackupTarget
{
    /// <summary>The reserved id of the built-in local-disk destination.</summary>
    public const string LocalId = "local";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    /// <summary>Service endpoint, e.g. <c>https://s3.eu-west-1.amazonaws.com</c> or <c>http://minio:9000</c>.</summary>
    public string ServiceUrl { get; set; } = string.Empty;

    public string Region { get; set; } = "us-east-1";

    public string Bucket { get; set; } = string.Empty;

    /// <summary>Optional key prefix within the bucket (a "folder").</summary>
    public string Prefix { get; set; } = string.Empty;

    public string AccessKey { get; set; } = string.Empty;

    /// <summary>Path-style addressing (<c>endpoint/bucket/key</c>); required by most non-AWS providers.</summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>Credential-store key under which this target's secret key is kept.</summary>
    [JsonIgnore]
    public string SecretCredentialKey => $"s3-secret:{Id}";

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Bucket : Name;
}
