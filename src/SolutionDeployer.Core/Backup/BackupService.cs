using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SolutionDeployer.Core.Models;
using SolutionDeployer.Core.Publishing;

namespace SolutionDeployer.Core.Backup;

/// <summary>
/// Snapshots deployment targets to zip packages, then hands them to the <see cref="IBackupStore"/>
/// the profile is configured to use (local disk or an S3-compatible bucket). MSDeploy targets are
/// pulled/pushed with <c>msdeploy.exe</c> (Web Deploy is bidirectional); FileSystem targets are
/// zipped/extracted directly. Passwords are redacted in every logged command line and never stored.
/// </summary>
public sealed class BackupService(
    ProcessRunner processRunner,
    MsDeployLocator msDeployLocator,
    IBackupStoreProvider storeProvider,
    int retention = 10) : IBackupService
{
    private readonly int _retention = Math.Max(1, retention);

    public bool CanBackUp(PublishProfile profile, string projectDirectory, out string? reason)
    {
        var target = ResolveTarget(profile, projectDirectory);
        switch (target)
        {
            case MsDeployTarget when !msDeployLocator.IsSupported:
                reason = "MSDeploy backups require msdeploy.exe, which is only available on Windows.";
                return false;
            case MsDeployTarget when msDeployLocator.Locate() is null:
                reason = "Could not locate msdeploy.exe. Install Web Deploy (the IIS \"Microsoft Web Deploy\" component).";
                return false;
            case MsDeployTarget:
            case FileSystemTarget:
                reason = null;
                return true;
            default:
                reason = $"Backup is only supported for MSDeploy and FileSystem profiles (this one is '{profile.WebPublishMethod ?? "unknown"}').";
                return false;
        }
    }

    public async Task<DeploymentBackup?> BackUpAsync(
        PublishJob job,
        Action<OutputLine> onOutput,
        CancellationToken cancellationToken = default)
    {
        var profile = job.Profile
            ?? throw new InvalidOperationException("BackUpAsync requires a job with a Profile.");

        var target = ResolveTarget(profile, job.Project.ProjectDirectory)
            ?? throw new InvalidOperationException(
                $"Profile '{profile.Name}' ({profile.WebPublishMethod ?? "unknown"}) cannot be backed up.");

        var store = storeProvider.ForProfile(profile);
        var profileKey = KeyFor(profile);
        var existing = await store.ListAsync(profileKey, cancellationToken).ConfigureAwait(false);

        var createdUtc = DateTimeOffset.UtcNow;
        var sequence = existing.Count == 0 ? 1 : existing.Max(b => b.Sequence) + 1;
        var id = Guid.NewGuid().ToString("N");
        var fileName = $"{createdUtc:yyyyMMdd-HHmmss}_{sequence:D6}_{id[..8]}.zip";
        var tempPackage = Path.Combine(Path.GetTempPath(), $"sd_backup_{id}.zip");

        onOutput(OutputLine.Info($"[backup] Capturing current deployment of '{profile.Name}' → {store.Description} …"));

        bool captured;
        try
        {
            captured = target switch
            {
                FileSystemTarget fs => await Task.Run(() => BackUpFileSystem(fs, tempPackage, onOutput), cancellationToken)
                    .ConfigureAwait(false),
                MsDeployTarget md => await BackUpMsDeployAsync(md, job, tempPackage, onOutput, cancellationToken)
                    .ConfigureAwait(false),
                _ => false,
            };

            if (!captured)
            {
                onOutput(OutputLine.Info("[backup] Nothing to back up (no existing deployment found)."));
                return null;
            }

            var previous = existing.FirstOrDefault();
            var contentHash = ComputeContentFingerprint(tempPackage);

            var backup = new DeploymentBackup
            {
                Id = id,
                ProfileKey = profileKey,
                ProfileName = profile.Name,
                ProjectName = job.Project.Name,
                Kind = target is MsDeployTarget ? BackupKind.MsDeploy : BackupKind.FileSystem,
                CreatedUtc = createdUtc,
                Sequence = sequence,
                StorageTargetId = store.TargetId,
                PackagePath = store.ResolveKey(profileKey, fileName),
                SizeBytes = new FileInfo(tempPackage).Length,
                Target = target.DisplayTarget,
                ContentHash = contentHash,
            };

            await store.SaveAsync(backup, tempPackage, cancellationToken).ConfigureAwait(false);
            onOutput(OutputLine.Info($"[backup] Saved snapshot #{sequence} ({backup.SizeText}) to {store.Description}."));

            if (previous?.ContentHash is { } priorHash && priorHash == contentHash)
            {
                onOutput(OutputLine.Info(
                    $"[backup] NOTE: this snapshot is identical to snapshot #{previous.Sequence} — the deployed " +
                    "content has not changed since then (nothing new was deployed)."));
            }

            await PruneAsync(store, profileKey, onOutput, cancellationToken).ConfigureAwait(false);
            return backup;
        }
        finally
        {
            TryDeleteFile(tempPackage);
        }
    }

    public async Task<IReadOnlyList<DeploymentBackup>> ListAsync(
        PublishProfile profile, string projectDirectory, CancellationToken cancellationToken = default)
    {
        var store = storeProvider.ForProfile(profile);
        return await store.ListAsync(KeyFor(profile), cancellationToken).ConfigureAwait(false);
    }

    public async Task RestoreAsync(
        DeploymentBackup backup,
        PublishProfile profile,
        string projectDirectory,
        PublishCredentials credentials,
        bool allowUntrustedCertificate,
        Action<OutputLine> onOutput,
        CancellationToken cancellationToken = default)
    {
        var target = ResolveTarget(profile, projectDirectory)
            ?? throw new InvalidOperationException($"Profile '{profile.Name}' cannot be restored to.");

        var store = storeProvider.ForTargetId(backup.StorageTargetId);
        onOutput(OutputLine.Info(
            $"[restore] Restoring snapshot #{backup.Sequence} from {backup.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} " +
            $"({backup.SizeText}, from {store.Description}) to '{profile.Name}' …"));

        var download = await store.DownloadAsync(backup, cancellationToken).ConfigureAwait(false);
        try
        {
            switch (target)
            {
                case FileSystemTarget fs:
                    await Task.Run(() => RestoreFileSystem(fs, download.LocalPath, onOutput), cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case MsDeployTarget md:
                    await RestoreMsDeployAsync(md, download.LocalPath, credentials, allowUntrustedCertificate, onOutput, cancellationToken)
                        .ConfigureAwait(false);
                    break;
            }
        }
        finally
        {
            if (download.IsTemporary)
                TryDeleteFile(download.LocalPath);
        }

        onOutput(OutputLine.Info("[restore] Done."));
    }

    public async Task<bool> DeleteAsync(DeploymentBackup backup, CancellationToken cancellationToken = default)
    {
        try
        {
            await storeProvider.ForTargetId(backup.StorageTargetId).DeleteAsync(backup, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task PruneAsync(IBackupStore store, string profileKey, Action<OutputLine> onOutput, CancellationToken cancellationToken)
    {
        var all = await store.ListAsync(profileKey, cancellationToken).ConfigureAwait(false);
        foreach (var stale in all.Skip(_retention))
        {
            await store.DeleteAsync(stale, cancellationToken).ConfigureAwait(false);
            onOutput(OutputLine.Info($"[backup] Pruned old snapshot from {stale.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}."));
        }
    }

    // ---- FileSystem -------------------------------------------------------

    private static bool BackUpFileSystem(FileSystemTarget target, string packagePath, Action<OutputLine> onOutput)
    {
        if (!Directory.Exists(target.Folder) ||
            !Directory.EnumerateFileSystemEntries(target.Folder).Any())
        {
            return false;
        }

        onOutput(OutputLine.Info($"[backup] Zipping {target.Folder}"));
        ZipFile.CreateFromDirectory(target.Folder, packagePath, CompressionLevel.Optimal, includeBaseDirectory: false);
        return true;
    }

    private static void RestoreFileSystem(FileSystemTarget target, string packagePath, Action<OutputLine> onOutput)
    {
        Directory.CreateDirectory(target.Folder);

        // A restore makes the folder match the snapshot exactly, so clear it first.
        onOutput(OutputLine.Info($"[restore] Clearing {target.Folder}"));
        foreach (var file in Directory.EnumerateFiles(target.Folder))
            File.Delete(file);
        foreach (var dir in Directory.EnumerateDirectories(target.Folder))
            Directory.Delete(dir, recursive: true);

        onOutput(OutputLine.Info("[restore] Extracting snapshot"));
        ZipFile.ExtractToDirectory(packagePath, target.Folder, overwriteFiles: true);
    }

    // ---- MSDeploy ---------------------------------------------------------

    private async Task<bool> BackUpMsDeployAsync(
        MsDeployTarget target,
        PublishJob job,
        string packagePath,
        Action<OutputLine> onOutput,
        CancellationToken cancellationToken)
    {
        // Pull: the remote site is the source, the local package is the destination.
        var source = BuildMsDeployProvider("contentPath", target.ContentPath, target, job.Credentials);
        var (args, redacted) = ComposeMsDeployArgs(
            source,
            ($"-dest:package={packagePath}", $"-dest:package={packagePath}"),
            job.AllowUntrustedCertificate);

        var exit = await RunMsDeployAsync(args, redacted, onOutput, cancellationToken).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"msdeploy backup failed (exit code {exit}).");

        return File.Exists(packagePath);
    }

    private async Task RestoreMsDeployAsync(
        MsDeployTarget target,
        string packagePath,
        PublishCredentials credentials,
        bool allowUntrustedCertificate,
        Action<OutputLine> onOutput,
        CancellationToken cancellationToken)
    {
        // Push: the saved package is the source, the remote site is the destination.
        var dest = BuildMsDeployProvider("contentPath", target.ContentPath, target, credentials);
        var (args, redacted) = ComposeMsDeployArgs(
            ($"-source:package={packagePath}", $"-source:package={packagePath}"),
            dest,
            allowUntrustedCertificate,
            extraFlags: ["-enableRule:AppOffline"]);

        var exit = await RunMsDeployAsync(args, redacted, onOutput, cancellationToken).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"msdeploy restore failed (exit code {exit}).");
    }

    private async Task<int> RunMsDeployAsync(
        IReadOnlyList<string> args,
        string redactedCommandLine,
        Action<OutputLine> onOutput,
        CancellationToken cancellationToken)
    {
        var msdeploy = msDeployLocator.Locate()
            ?? throw new InvalidOperationException("msdeploy.exe not found.");

        onOutput(OutputLine.Info($"$ \"{msdeploy}\" {redactedCommandLine}"));
        var result = await processRunner
            .RunAsync(msdeploy, args, workingDirectory: null, onOutput, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return result.ExitCode;
    }

    /// <summary>Builds a single <c>-source:</c>/<c>-dest:</c> provider argument plus a redacted copy.</summary>
    private static (string Arg, string Redacted) BuildMsDeployProvider(
        string providerKey,
        string providerValue,
        MsDeployTarget target,
        PublishCredentials credentials)
    {
        // Provider settings are one comma-separated argument; the password lives inside it.
        var common = new List<string>
        {
            $"{providerKey}={providerValue}",
            $"computerName={target.ComputerName}",
        };

        var hasUser = !string.IsNullOrEmpty(credentials.UserName);
        common.Add($"authType={(hasUser ? "Basic" : "NTLM")}");
        if (hasUser)
            common.Add($"userName={credentials.UserName}");

        var real = new List<string>(common);
        var safe = new List<string>(common);
        if (!string.IsNullOrEmpty(credentials.Password))
        {
            real.Add($"password={credentials.Password}");
            safe.Add("password=***");
        }

        // Comma-joined provider body only; the caller prepends "-source:"/"-dest:".
        return (string.Join(',', real), string.Join(',', safe));
    }

    private static (List<string> Args, string Redacted) ComposeMsDeployArgs(
        (string Arg, string Redacted) source,
        (string Arg, string Redacted) dest,
        bool allowUntrusted,
        IReadOnlyList<string>? extraFlags = null)
    {
        var sourceArg = source.Arg.StartsWith('-') ? source.Arg : $"-source:{source.Arg}";
        var sourceRedacted = source.Redacted.StartsWith('-') ? source.Redacted : $"-source:{source.Redacted}";
        var destArg = dest.Arg.StartsWith('-') ? dest.Arg : $"-dest:{dest.Arg}";
        var destRedacted = dest.Redacted.StartsWith('-') ? dest.Redacted : $"-dest:{dest.Redacted}";

        var args = new List<string> { "-verb:sync", sourceArg, destArg };
        var redacted = new List<string> { "-verb:sync", sourceRedacted, destRedacted };

        if (allowUntrusted)
        {
            args.Add("-allowUntrusted");
            redacted.Add("-allowUntrusted");
        }

        if (extraFlags is not null)
        {
            args.AddRange(extraFlags);
            redacted.AddRange(extraFlags);
        }

        return (args, string.Join(' ', redacted));
    }

    // ---- Target resolution ------------------------------------------------

    private abstract record DeploymentTarget(string DisplayTarget);

    private sealed record FileSystemTarget(string Folder) : DeploymentTarget(Folder);

    private sealed record MsDeployTarget(string ComputerName, string ContentPath, string Display)
        : DeploymentTarget(Display);

    private static DeploymentTarget? ResolveTarget(PublishProfile profile, string projectDirectory)
    {
        var method = profile.WebPublishMethod;

        if (string.Equals(method, "MSDeploy", StringComparison.OrdinalIgnoreCase))
        {
            var serviceUrl = profile.ServerUrl;
            var contentPath = profile.SiteName;
            if (string.IsNullOrWhiteSpace(serviceUrl) || string.IsNullOrWhiteSpace(contentPath))
                return null;

            return new MsDeployTarget(
                ComputerName: NormalizeComputerName(serviceUrl, contentPath),
                ContentPath: contentPath,
                Display: $"{serviceUrl} → {contentPath}");
        }

        if (string.Equals(method, "FileSystem", StringComparison.OrdinalIgnoreCase) || method is null)
        {
            var raw = profile.Properties.GetValueOrDefault("publishUrl")
                      ?? profile.Properties.GetValueOrDefault("DestinationPath");
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var folder = Path.IsPathRooted(raw) ? raw : Path.GetFullPath(Path.Combine(projectDirectory, raw));
            return new FileSystemTarget(folder);
        }

        return null;
    }

    /// <summary>WMSvc endpoints need the site scoped via <c>?site=</c>; add it when missing.</summary>
    private static string NormalizeComputerName(string serviceUrl, string contentPath)
    {
        if (serviceUrl.Contains("msdeploy.axd", StringComparison.OrdinalIgnoreCase) &&
            !serviceUrl.Contains("site=", StringComparison.OrdinalIgnoreCase))
        {
            var siteRoot = contentPath.Split('/', '\\')[0];
            var separator = serviceUrl.Contains('?') ? '&' : '?';
            return $"{serviceUrl}{separator}site={siteRoot}";
        }

        return serviceUrl;
    }

    // ---- Helpers ----------------------------------------------------------

    private static string KeyFor(PublishProfile profile)
    {
        var hash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(profile.FilePath.ToLowerInvariant())))[..8];
        return $"{Sanitize(profile.Name)}_{hash}";
    }

    private static string Sanitize(string name)
    {
        var chars = name.Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c).ToArray();
        return new string(chars);
    }

    // MSDeploy package metadata that changes on every pull even when the payload is identical.
    private static readonly HashSet<string> PackageMetadata =
        new(StringComparer.OrdinalIgnoreCase) { "archive.xml", "systemInfo.xml", "parameters.xml" };

    /// <summary>
    /// A stable fingerprint of the captured payload: SHA-256 over each content entry's name and
    /// uncompressed size, sorted, ignoring volatile package metadata.
    /// </summary>
    private static string ComputeContentFingerprint(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var sb = new StringBuilder();
        foreach (var entry in archive.Entries
                     .Where(e => !PackageMetadata.Contains(e.FullName))
                     .OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            sb.Append(entry.FullName).Append(':').Append(entry.Length).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort.
        }
    }
}
