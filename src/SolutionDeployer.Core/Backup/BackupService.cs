using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SolutionDeployer.Core.Models;
using SolutionDeployer.Core.Publishing;

namespace SolutionDeployer.Core.Backup;

/// <summary>
/// Snapshots deployment targets to local zip packages. MSDeploy targets are pulled/pushed with
/// <c>msdeploy.exe</c> (Web Deploy is bidirectional); FileSystem targets are zipped/extracted directly.
/// Passwords are redacted in every logged command line and never written to a manifest.
/// </summary>
public sealed class BackupService : IBackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ProcessRunner _processRunner;
    private readonly MsDeployLocator _msDeployLocator;
    private readonly string _root;
    private readonly int _retention;

    public BackupService(
        ProcessRunner processRunner,
        MsDeployLocator msDeployLocator,
        string? rootOverride = null,
        int retention = 10)
    {
        _processRunner = processRunner;
        _msDeployLocator = msDeployLocator;
        _retention = Math.Max(1, retention);
        _root = rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "3ai.SolutionDeployer",
            "Backups");
    }

    public bool CanBackUp(PublishProfile profile, string projectDirectory, out string? reason)
    {
        var target = ResolveTarget(profile, projectDirectory);
        switch (target)
        {
            case MsDeployTarget when !_msDeployLocator.IsSupported:
                reason = "MSDeploy backups require msdeploy.exe, which is only available on Windows.";
                return false;
            case MsDeployTarget when _msDeployLocator.Locate() is null:
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

        var folder = EnsureProfileFolder(profile);
        var createdUtc = DateTimeOffset.UtcNow;
        var sequence = NextSequence(folder);
        var id = Guid.NewGuid().ToString("N");
        // Sequence keeps the file name unique even for several snapshots within the same second.
        var packagePath = Path.Combine(folder, $"{createdUtc:yyyyMMdd-HHmmss}_{sequence:D6}_{id[..8]}.zip");

        onOutput(OutputLine.Info($"[backup] Capturing current deployment of '{profile.Name}' …"));

        bool captured;
        try
        {
            captured = target switch
            {
                FileSystemTarget fs => await Task.Run(() => BackUpFileSystem(fs, packagePath, onOutput), cancellationToken)
                    .ConfigureAwait(false),
                MsDeployTarget md => await BackUpMsDeployAsync(md, job, packagePath, onOutput, cancellationToken)
                    .ConfigureAwait(false),
                _ => false,
            };
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(packagePath);
            throw;
        }

        if (!captured)
        {
            TryDeleteFile(packagePath);
            onOutput(OutputLine.Info("[backup] Nothing to back up (no existing deployment found)."));
            return null;
        }

        var backup = new DeploymentBackup
        {
            Id = id,
            ProfileKey = KeyFor(profile),
            ProfileName = profile.Name,
            ProjectName = job.Project.Name,
            Kind = target is MsDeployTarget ? BackupKind.MsDeploy : BackupKind.FileSystem,
            CreatedUtc = createdUtc,
            Sequence = sequence,
            PackagePath = packagePath,
            SizeBytes = new FileInfo(packagePath).Length,
            Target = target.DisplayTarget,
        };

        WriteManifest(backup);
        onOutput(OutputLine.Info($"[backup] Saved snapshot ({backup.SizeText})."));
        Prune(folder, onOutput);
        return backup;
    }

    public IReadOnlyList<DeploymentBackup> List(PublishProfile profile, string projectDirectory)
    {
        var folder = Path.Combine(_root, KeyFor(profile));
        if (!Directory.Exists(folder))
            return [];

        return ReadAll(folder).Where(b => File.Exists(b.PackagePath)).ToList();
    }

    /// <summary>All snapshots in a folder, newest first. Ordering is total and stable: by sequence,
    /// then timestamp, then id — so same-second snapshots never sort arbitrarily.</summary>
    private static List<DeploymentBackup> ReadAll(string folder) =>
        Directory.EnumerateFiles(folder, "*.json")
            .Select(ReadManifest)
            .Where(b => b is not null)
            .Select(b => b!)
            .OrderByDescending(b => b.Sequence)
            .ThenByDescending(b => b.CreatedUtc)
            .ThenBy(b => b.Id, StringComparer.Ordinal)
            .ToList();

    public async Task RestoreAsync(
        DeploymentBackup backup,
        PublishProfile profile,
        string projectDirectory,
        PublishCredentials credentials,
        bool allowUntrustedCertificate,
        Action<OutputLine> onOutput,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(backup.PackagePath))
            throw new FileNotFoundException($"Backup package not found: {backup.PackagePath}");

        var target = ResolveTarget(profile, projectDirectory)
            ?? throw new InvalidOperationException($"Profile '{profile.Name}' cannot be restored to.");

        onOutput(OutputLine.Info(
            $"[restore] Restoring snapshot from {backup.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} to '{profile.Name}' …"));

        switch (target)
        {
            case FileSystemTarget fs:
                await Task.Run(() => RestoreFileSystem(fs, backup.PackagePath, onOutput), cancellationToken)
                    .ConfigureAwait(false);
                break;
            case MsDeployTarget md:
                await RestoreMsDeployAsync(md, backup.PackagePath, credentials, allowUntrustedCertificate, onOutput, cancellationToken)
                    .ConfigureAwait(false);
                break;
        }

        onOutput(OutputLine.Info("[restore] Done."));
    }

    public bool Delete(DeploymentBackup backup)
    {
        try
        {
            TryDeleteFile(backup.PackagePath);
            TryDeleteFile(ManifestPath(backup));
            return true;
        }
        catch
        {
            return false;
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
        var msdeploy = _msDeployLocator.Locate()
            ?? throw new InvalidOperationException("msdeploy.exe not found.");

        onOutput(OutputLine.Info($"$ \"{msdeploy}\" {redactedCommandLine}"));
        var result = await _processRunner
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
        // source/dest may already be a full "-source:package=…" token, or just a provider body that
        // still needs its "-source:"/"-dest:" prefix. Normalise both forms.
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

    // ---- Storage ----------------------------------------------------------

    private static string KeyFor(PublishProfile profile)
    {
        var hash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(profile.FilePath.ToLowerInvariant())))[..8];
        return $"{Sanitize(profile.Name)}_{hash}";
    }

    private string EnsureProfileFolder(PublishProfile profile)
    {
        var folder = Path.Combine(_root, KeyFor(profile));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string Sanitize(string name)
    {
        var chars = name.Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c).ToArray();
        return new string(chars);
    }

    private static string ManifestPath(DeploymentBackup backup) =>
        Path.ChangeExtension(backup.PackagePath, ".json");

    private static void WriteManifest(DeploymentBackup backup) =>
        File.WriteAllText(ManifestPath(backup), JsonSerializer.Serialize(backup, JsonOptions));

    private static DeploymentBackup? ReadManifest(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<DeploymentBackup>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private void Prune(string folder, Action<OutputLine> onOutput)
    {
        foreach (var stale in ReadAll(folder).Skip(_retention))
        {
            Delete(stale);
            onOutput(OutputLine.Info($"[backup] Pruned old snapshot from {stale.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}."));
        }
    }

    /// <summary>Next monotonic sequence for a profile folder: one more than the highest seen so far
    /// (pruning never lowers it, so sequences stay strictly increasing across the profile's lifetime).</summary>
    private static long NextSequence(string folder)
    {
        var manifests = Directory.EnumerateFiles(folder, "*.json")
            .Select(ReadManifest)
            .Where(b => b is not null)
            .Select(b => b!.Sequence)
            .DefaultIfEmpty(0)
            .Max();
        return manifests + 1;
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
