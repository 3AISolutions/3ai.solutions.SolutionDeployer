using Amazon.S3;
using Amazon.S3.Model;
using SolutionDeployer.Core.Configuration;

namespace SolutionDeployer.Core.Backup;

/// <summary>
/// Stores snapshot packages and manifests in an S3-compatible bucket. Works against AWS S3 and
/// third-party providers (MinIO, Backblaze B2, Wasabi, …) via a custom <c>ServiceURL</c> and
/// path-style addressing.
/// </summary>
public sealed class S3BackupStore : IBackupStore
{
    private readonly S3BackupTarget _config;
    private readonly string _secretKey;
    private readonly string _prefix;

    public S3BackupStore(S3BackupTarget config, string secretKey)
    {
        _config = config;
        _secretKey = secretKey;
        _prefix = config.Prefix.Trim().Trim('/');
    }

    public string TargetId => _config.Id;

    public string Description =>
        $"s3://{_config.Bucket}{(_prefix.Length > 0 ? "/" + _prefix : "")} ({_config.ServiceUrl})";

    public string ResolveKey(string profileKey, string fileName)
    {
        var parts = new[] { _prefix, profileKey, fileName }.Where(p => p.Length > 0);
        return string.Join('/', parts);
    }

    public async Task<IReadOnlyList<DeploymentBackup>> ListAsync(string profileKey, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        var folderPrefix = ResolveKey(profileKey, string.Empty).TrimEnd('/') + "/";

        var manifests = new List<DeploymentBackup>();
        var request = new ListObjectsV2Request { BucketName = _config.Bucket, Prefix = folderPrefix };

        ListObjectsV2Response response;
        do
        {
            response = await client.ListObjectsV2Async(request, cancellationToken).ConfigureAwait(false);
            foreach (var obj in response.S3Objects ?? [])
            {
                if (!obj.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    continue;

                using var get = await client.GetObjectAsync(_config.Bucket, obj.Key, cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(get.ResponseStream);
                var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                if (BackupManifest.Deserialize(json) is { } backup)
                    manifests.Add(backup);
            }

            request.ContinuationToken = response.NextContinuationToken;
        }
        while (response.IsTruncated == true);

        return BackupManifest.SortNewestFirst(manifests);
    }

    public async Task SaveAsync(DeploymentBackup backup, string localPackagePath, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();

        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _config.Bucket,
            Key = backup.PackagePath,
            FilePath = localPackagePath,
        }, cancellationToken).ConfigureAwait(false);

        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _config.Bucket,
            Key = BackupManifest.ManifestKey(backup.PackagePath),
            ContentBody = BackupManifest.Serialize(backup),
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DownloadedPackage> DownloadAsync(DeploymentBackup backup, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        var tempPath = Path.Combine(Path.GetTempPath(), $"sd_restore_{Guid.NewGuid():N}.zip");

        using var response = await client.GetObjectAsync(_config.Bucket, backup.PackagePath, cancellationToken).ConfigureAwait(false);
        await response.WriteResponseStreamToFileAsync(tempPath, append: false, cancellationToken).ConfigureAwait(false);

        return new DownloadedPackage(tempPath, IsTemporary: true);
    }

    public async Task DeleteAsync(DeploymentBackup backup, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        await client.DeleteObjectAsync(_config.Bucket, backup.PackagePath, cancellationToken).ConfigureAwait(false);
        await client.DeleteObjectAsync(_config.Bucket, BackupManifest.ManifestKey(backup.PackagePath), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Verifies the connection/credentials by listing the bucket (used by the "Test" button).</summary>
    public async Task TestAsync(CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        await client.ListObjectsV2Async(
            new ListObjectsV2Request { BucketName = _config.Bucket, MaxKeys = 1 }, cancellationToken).ConfigureAwait(false);
    }

    private AmazonS3Client CreateClient()
    {
        var config = new AmazonS3Config
        {
            ServiceURL = _config.ServiceUrl,
            ForcePathStyle = _config.ForcePathStyle,
            AuthenticationRegion = string.IsNullOrWhiteSpace(_config.Region) ? "us-east-1" : _config.Region,
        };

        return new AmazonS3Client(_config.AccessKey, _secretKey, config);
    }
}
