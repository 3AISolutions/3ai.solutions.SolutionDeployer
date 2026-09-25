using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using SolutionDeployer.Core.Models;
using SolutionDeployer.Core.Publishing;

namespace SolutionDeployer.Core.Backup;

/// <summary>
/// Snapshots deployment targets to zip packages, then hands them to the <see cref="IBackupStore"/>
/// the owner is configured to use (local disk or an S3-compatible bucket). MSDeploy targets are
/// pulled/pushed with <c>msdeploy.exe</c> (Web Deploy is bidirectional); FileSystem targets are
/// zipped/extracted directly. For MSDeploy profiles only the server files the publish is about to
/// change are captured: the profile is first published to a temp folder, and <c>msdeploy -whatif</c>
/// reports what would be updated, deleted or added. Passwords are redacted in every logged command
/// line and never stored.
/// </summary>
public sealed class BackupService(
    ProcessRunner processRunner,
    MsDeployLocator msDeployLocator,
    IBackupStoreProvider storeProvider,
    int retention = 10,
    IPublishEngineFactory? engineFactory = null) : IBackupService
{
    private readonly int _retention = Math.Max(1, retention);

    public bool CanBackUp(BackupOwner owner, out string? reason)
    {
        var target = ResolveTarget(owner, out var unresolvedReason);
        switch (target)
        {
            case MsDeployTarget or ReportedTargets when !msDeployLocator.IsSupported:
                reason = "MSDeploy backups require msdeploy.exe, which is only available on Windows.";
                return false;
            case MsDeployTarget or ReportedTargets when msDeployLocator.Locate() is null:
                reason = "Could not locate msdeploy.exe. Install Web Deploy (the IIS \"Microsoft Web Deploy\" component).";
                return false;
            case ReportedTargets when engineFactory is null:
                reason = "Script -WhatIf backups need the script engine.";
                return false;
            case MsDeployTarget:
            case ReportedTargets:
            case FileSystemTarget:
                reason = null;
                return true;
            default:
                reason = unresolvedReason;
                return false;
        }
    }

    public async Task<DeploymentBackup?> BackUpAsync(
        PublishJob job,
        Action<OutputLine> onOutput,
        CancellationToken cancellationToken = default)
    {
        var owner = BackupOwner.ForJob(job)
            ?? throw new InvalidOperationException("BackUpAsync requires a job with a Profile or a Script.");

        var target = ResolveTarget(owner, out var reason)
            ?? throw new InvalidOperationException($"'{owner.Name}' cannot be backed up: {reason}");

        var store = storeProvider.ForOwner(owner);
        var ownerKey = KeyFor(owner);
        var existing = await store.ListAsync(ownerKey, cancellationToken).ConfigureAwait(false);

        var createdUtc = DateTimeOffset.UtcNow;
        var sequence = existing.Count == 0 ? 1 : existing.Max(b => b.Sequence) + 1;
        var id = Guid.NewGuid().ToString("N");
        var fileName = $"{createdUtc:yyyyMMdd-HHmmss}_{sequence:D6}_{id[..8]}.zip";
        var tempPackage = Path.Combine(Path.GetTempPath(), $"sd_backup_{id}.zip");

        // A profile publish can be previewed; a script can only report its changes itself (-WhatIf).
        // Otherwise a script's deployment is opaque, so it gets a full snapshot of its folder.
        var partial = (target is MsDeployTarget && owner.Profile is not null && engineFactory is not null) ||
                      target is ReportedTargets;

        onOutput(OutputLine.Info($"[backup] Capturing current deployment of '{owner.Name}' → {store.Description} …"));

        try
        {
            var capture = target switch
            {
                FileSystemTarget fs => await Task.Run(() => BackUpFileSystem(fs, tempPackage, onOutput), cancellationToken)
                    .ConfigureAwait(false)
                    ? Capture.Full(BackupKind.FileSystem)
                    : null,
                ReportedTargets => await BackUpScriptChangesAsync(job, tempPackage, onOutput, cancellationToken)
                    .ConfigureAwait(false),
                MsDeployTarget md when partial => await BackUpMsDeployChangesAsync(md, job, tempPackage, onOutput, cancellationToken)
                    .ConfigureAwait(false),
                MsDeployTarget md => await BackUpMsDeployAsync(md, job, tempPackage, onOutput, cancellationToken)
                    .ConfigureAwait(false)
                    ? Capture.Full(BackupKind.MsDeploy)
                    : null,
                _ => null,
            };

            if (capture is null)
            {
                // The partial path explains its own "nothing to save" outcome.
                if (!partial)
                    onOutput(OutputLine.Info("[backup] Nothing to back up (no existing deployment found)."));
                return null;
            }

            var previous = existing.FirstOrDefault();
            var contentHash = ComputeContentFingerprint(tempPackage);

            var backup = new DeploymentBackup
            {
                Id = id,
                ProfileKey = ownerKey,
                ProfileName = owner.Name,
                ProjectName = job.Project.Name,
                Kind = capture.Kind,
                CreatedUtc = createdUtc,
                Sequence = sequence,
                StorageTargetId = store.TargetId,
                PackagePath = store.ResolveKey(ownerKey, fileName),
                SizeBytes = new FileInfo(tempPackage).Length,
                Target = capture.Targets.Count > 0
                    ? string.Join(", ", capture.Targets.Select(t => t.ComputerName))
                    : target.DisplayTarget,
                ContentHash = contentHash,
                SavedPaths = capture.Saved,
                AddedPaths = capture.Added,
                Targets = capture.Targets,
            };

            await store.SaveAsync(backup, tempPackage, cancellationToken).ConfigureAwait(false);
            onOutput(OutputLine.Info($"[backup] Saved snapshot #{sequence} ({backup.SizeText}{backup.ChangeText}) to {store.Description}."));

            // A partial snapshot holds only what this deploy changes, so matching the previous one says nothing.
            if (capture.Kind is not (BackupKind.MsDeployPartial or BackupKind.ScriptPartial) &&
                previous?.ContentHash is { } priorHash && priorHash == contentHash)
            {
                onOutput(OutputLine.Info(
                    $"[backup] NOTE: this snapshot is identical to snapshot #{previous.Sequence} — the deployed " +
                    "content has not changed since then (nothing new was deployed)."));
            }

            await PruneAsync(store, ownerKey, onOutput, cancellationToken).ConfigureAwait(false);
            return backup;
        }
        finally
        {
            TryDeleteFile(tempPackage);
        }
    }

    public async Task<IReadOnlyList<DeploymentBackup>> ListAsync(
        BackupOwner owner, CancellationToken cancellationToken = default)
    {
        var store = storeProvider.ForOwner(owner);
        return await store.ListAsync(KeyFor(owner), cancellationToken).ConfigureAwait(false);
    }

    public async Task RestoreAsync(
        DeploymentBackup backup,
        BackupOwner owner,
        PublishCredentials credentials,
        bool allowUntrustedCertificate,
        Action<OutputLine> onOutput,
        CancellationToken cancellationToken = default)
    {
        var target = ResolveTarget(owner, out var reason)
            ?? throw new InvalidOperationException($"'{owner.Name}' cannot be restored to: {reason}");

        var store = storeProvider.ForTargetId(backup.StorageTargetId);

        // A partial snapshot only undoes its own deploy. To get back to the state before it, every newer
        // deploy must be undone first — newest first.
        var chain = new List<DeploymentBackup> { backup };
        if (backup.Kind is BackupKind.MsDeployPartial or BackupKind.ScriptPartial)
        {
            var all = await store.ListAsync(backup.ProfileKey, cancellationToken).ConfigureAwait(false);
            chain = all
                .Where(b => b.Sequence > backup.Sequence)
                .OrderByDescending(b => b.Sequence)
                .Append(backup)
                .ToList();

            if (chain.Count > 1)
            {
                onOutput(OutputLine.Info(
                    $"[restore] Rolling back {chain.Count} deployments (newest first) to return to the state " +
                    $"before snapshot #{backup.Sequence} …"));
            }
        }

        // Where a script's pre/post-restore commands run: its configured server, or every server the
        // snapshots being rolled back touched.
        var script = owner.Script;
        var commandTargets = target switch
        {
            MsDeployTarget single => new List<MsDeployTarget> { single },
            ReportedTargets => chain
                .SelectMany(b => b.Targets)
                .SelectMany(t => t.AllComputerNames)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => new MsDeployTarget(name, string.Empty, name))
                .ToList(),
            _ => new List<MsDeployTarget>(),
        };

        if (!string.IsNullOrWhiteSpace(script?.PreRestoreCommand))
        {
            foreach (var commandTarget in commandTargets)
            {
                // Usually stops a service so its files can be replaced; if it's already stopped, carry on.
                var exit = await RunRemoteCommandAsync(script.PreRestoreCommand, commandTarget, credentials, allowUntrustedCertificate, onOutput, cancellationToken)
                    .ConfigureAwait(false);
                if (exit != 0)
                    onOutput(OutputLine.Error($"[restore] Pre-restore command failed on {commandTarget.ComputerName} (exit code {exit}); continuing with the restore."));
            }
        }

        try
        {
            foreach (var snapshot in chain)
            {
                await RestoreOneAsync(snapshot, target, store, owner, credentials, allowUntrustedCertificate, onOutput, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            // Always attempt the post-restore command (e.g. restart the service), even after a failed restore.
            if (!string.IsNullOrWhiteSpace(script?.PostRestoreCommand))
            {
                foreach (var commandTarget in commandTargets)
                {
                    var exit = await RunRemoteCommandAsync(script.PostRestoreCommand, commandTarget, credentials, allowUntrustedCertificate, onOutput, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (exit != 0)
                        onOutput(OutputLine.Error($"[restore] Post-restore command failed on {commandTarget.ComputerName} (exit code {exit})."));
                }
            }
        }

        onOutput(OutputLine.Info("[restore] Done."));
    }

    private async Task RestoreOneAsync(
        DeploymentBackup backup,
        DeploymentTarget target,
        IBackupStore store,
        BackupOwner owner,
        PublishCredentials credentials,
        bool allowUntrustedCertificate,
        Action<OutputLine> onOutput,
        CancellationToken cancellationToken)
    {
        onOutput(OutputLine.Info(
            $"[restore] Restoring snapshot #{backup.Sequence} from {backup.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} " +
            $"({backup.SizeText}{backup.ChangeText}, from {store.Description}) to '{owner.Name}' …"));

        if (backup.Kind == BackupKind.MsDeployPartial)
        {
            if (target is not MsDeployTarget partialTarget)
                throw new InvalidOperationException("A partial Web Deploy snapshot can only be restored to a Web Deploy target.");

            await RestoreMsDeployChangesAsync(backup, partialTarget, store, credentials, allowUntrustedCertificate, onOutput, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (backup.Kind == BackupKind.ScriptPartial)
        {
            await RestoreScriptChangesAsync(backup, store, credentials, allowUntrustedCertificate, onOutput, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (target is ReportedTargets)
        {
            throw new InvalidOperationException(
                $"Snapshot #{backup.Sequence} was taken with different backup settings; set the script's backup back to " +
                "a server or local folder to restore it.");
        }

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
                    // app_offline.htm only makes sense for a web app, not e.g. a script's service folder.
                    await RestoreMsDeployAsync(md, download.LocalPath, credentials, allowUntrustedCertificate,
                            appOffline: owner.Profile is not null, onOutput, cancellationToken)
                        .ConfigureAwait(false);
                    break;
            }
        }
        finally
        {
            if (download.IsTemporary)
                TryDeleteFile(download.LocalPath);
        }
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

    /// <summary>What a capture produced: the snapshot kind plus, for a partial one, what it covers.</summary>
    private sealed record Capture(BackupKind Kind, IReadOnlyList<string> Saved, IReadOnlyList<string> Added)
    {
        /// <summary>Per-target details of a <see cref="BackupKind.ScriptPartial"/> capture.</summary>
        public IReadOnlyList<BackupTargetChanges> Targets { get; init; } = [];

        public static Capture Full(BackupKind kind) => new(kind, [], []);
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

    // ---- MSDeploy (full) --------------------------------------------------

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
            ($"-dest:package={Q(packagePath)}", $"-dest:package={Q(packagePath)}"),
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
        bool appOffline,
        Action<OutputLine> onOutput,
        CancellationToken cancellationToken)
    {
        // Push: the saved package is the source, the remote site is the destination.
        var dest = BuildMsDeployProvider("contentPath", target.ContentPath, target, credentials);
        var (args, redacted) = ComposeMsDeployArgs(
            ($"-source:package={Q(packagePath)}", $"-source:package={Q(packagePath)}"),
            dest,
            allowUntrustedCertificate,
            extraFlags: appOffline ? ["-enableRule:AppOffline"] : null);

        var exit = await RunMsDeployAsync(args, redacted, onOutput, cancellationToken).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"msdeploy restore failed (exit code {exit}).");
    }

    // ---- MSDeploy (only what the publish changes) -------------------------

    private async Task<Capture?> BackUpMsDeployChangesAsync(
        MsDeployTarget target,
        PublishJob job,
        string packagePath,
        Action<OutputLine> onOutput,
        CancellationToken cancellationToken)
    {
        // Kept short: web projects have deep content paths, and the copy fails past Windows' 260-char limit.
        var previewDir = Path.Combine(Path.GetTempPath(), $"sdp_{Guid.NewGuid().ToString("N")[..8]}");
        var manifestPath = Path.Combine(Path.GetTempPath(), $"sd_manifest_{Guid.NewGuid():N}.xml");
        try
        {
            onOutput(OutputLine.Info("[backup] Building a preview of this publish to find the server files it will change …"));
            await BuildPreviewAsync(job, previewDir, onOutput, cancellationToken).ConfigureAwait(false);

            var changes = await WhatIfAsync(target, job, previewDir, onOutput, cancellationToken).ConfigureAwait(false);

            if (changes.LooksUnparsed)
            {
                onOutput(OutputLine.Info("[backup] Could not read msdeploy's list of changes — taking a full snapshot instead."));
                return await BackUpMsDeployAsync(target, job, packagePath, onOutput, cancellationToken).ConfigureAwait(false)
                    ? Capture.Full(BackupKind.MsDeploy)
                    : null;
            }

            if (changes.IsEmpty)
            {
                onOutput(OutputLine.Info("[backup] This publish won't change any files on the server — no snapshot needed."));
                return null;
            }

            var toSave = changes.ToSave;
            onOutput(OutputLine.Info(
                $"[backup] The publish will update {changes.Updated.Count}, delete {changes.Deleted.Count} and add " +
                $"{changes.Added.Count} item(s) — saving the {toSave.Count} it will overwrite or delete."));

            if (toSave.Count > 0)
            {
                WriteManifest(manifestPath, toSave);
                var source = BuildMsDeployProvider("manifest", manifestPath, target, job.Credentials);
                var (args, redacted) = ComposeMsDeployArgs(
                    source,
                    ($"-dest:package={Q(packagePath)}", $"-dest:package={Q(packagePath)}"),
                    job.AllowUntrustedCertificate);

                var exit = await RunMsDeployAsync(args, redacted, onOutput, cancellationToken).ConfigureAwait(false);
                if (exit != 0)
                    throw new InvalidOperationException($"msdeploy backup failed (exit code {exit}).");
            }
            else
            {
                // Only additions: nothing to save, but the snapshot still records what to remove on restore.
                using (ZipFile.Open(packagePath, ZipArchiveMode.Create)) { }
            }

            return new Capture(BackupKind.MsDeployPartial, toSave, changes.Added);
        }
        finally
        {
            TryDeleteDirectory(previewDir);
            TryDeleteFile(manifestPath);
        }
    }

    /// <summary>
    /// Publishes the job's profile to a local folder instead of the server, producing exactly the files the
    /// real publish would send.
    /// </summary>
    private async Task BuildPreviewAsync(PublishJob job, string previewDir, Action<OutputLine> onOutput, CancellationToken cancellationToken)
    {
        var properties = new Dictionary<string, string>(job.AdditionalProperties)
        {
            ["WebPublishMethod"] = "FileSystem",
            ["PublishUrl"] = previewDir,
            ["DeleteExistingFiles"] = "true",
        };

        var preview = new PublishJob
        {
            Project = job.Project,
            Profile = job.Profile,
            Engine = job.Engine,
            Configuration = job.Configuration,
            AllowUntrustedCertificate = job.AllowUntrustedCertificate,
            AdditionalProperties = properties,
            // No credentials: nothing is sent to the server.
        };

        var result = await engineFactory!.Get(job.Engine).PublishAsync(preview, onOutput, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
            throw new InvalidOperationException($"Preview build failed: {result.ErrorMessage ?? result.Status.ToString()}");

        if (!Directory.Exists(previewDir) || !Directory.EnumerateFileSystemEntries(previewDir).Any())
            throw new InvalidOperationException($"Preview build produced no files in {previewDir}.");
    }

    /// <summary>Asks msdeploy what syncing the preview onto the server would do, without changing anything.</summary>
    private async Task<MsDeployChangeSet> WhatIfAsync(
        MsDeployTarget target,
        PublishJob job,
        string previewDir,
        Action<OutputLine> onOutput,
        CancellationToken cancellationToken)
    {
        // Compare by content: the preview's timestamps are all new, so a timestamp compare would flag everything.
        var flags = new List<string> { "-whatif", "-useCheckSum" };
        if (IsTrue(job.Profile!, "SkipExtraFilesOnServer"))
            flags.Add("-enableRule:DoNotDeleteRule");
        if (IsTrue(job.Profile!, "ExcludeApp_Data"))
            flags.Add(@"-skip:objectName=dirPath,absolutePath=\\App_Data$");

        var dest = BuildMsDeployProvider("contentPath", target.ContentPath, target, job.Credentials);
        var (args, redacted) = ComposeMsDeployArgs(
            ($"-source:contentPath={Q(previewDir)}", $"-source:contentPath={Q(previewDir)}"),
            dest,
            job.AllowUntrustedCertificate,
            flags);

        var lines = new List<string>();
        void Collect(OutputLine line)
        {
            lock (lines)
                lines.Add(line.Text);
            onOutput(line);
        }

        var exit = await RunMsDeployAsync(args, redacted, Collect, cancellationToken).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"msdeploy could not compare the preview with the server (exit code {exit}).");

        lock (lines)
            return MsDeployChangeSet.Parse(lines);
    }

    /// <summary>Undoes one partial profile snapshot on its Web Deploy target.</summary>
    private async Task RestoreMsDeployChangesAsync(
        DeploymentBackup backup,
        MsDeployTarget target,
        IBackupStore store,
        PublishCredentials credentials,
        bool allowUntrustedCertificate,
        Action<OutputLine> onOutput,
        CancellationToken cancellationToken)
    {
        if (backup.SavedPaths.Count == 0)
        {
            await UndoChangesAsync(target, backup.AddedPaths, packagePath: null, credentials, allowUntrustedCertificate, onOutput, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var download = await store.DownloadAsync(backup, cancellationToken).ConfigureAwait(false);
        try
        {
            await UndoChangesAsync(target, backup.AddedPaths, download.LocalPath, credentials, allowUntrustedCertificate, onOutput, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (download.IsTemporary)
                TryDeleteFile(download.LocalPath);
        }
    }

    /// <summary>Undoes one partial script snapshot on every Web Deploy target it covers.</summary>
    private async Task RestoreScriptChangesAsync(
        DeploymentBackup backup,
        IBackupStore store,
        PublishCredentials credentials,
        bool allowUntrustedCertificate,
        Action<OutputLine> onOutput,
        CancellationToken cancellationToken)
    {
        var download = await store.DownloadAsync(backup, cancellationToken).ConfigureAwait(false);
        var work = Path.Combine(Path.GetTempPath(), $"sdr_{Guid.NewGuid().ToString("N")[..8]}");
        try
        {
            ZipFile.ExtractToDirectory(download.LocalPath, work);

            foreach (var changes in backup.Targets)
            {
                var packagePath = changes.PackageEntry is null ? null : Path.Combine(work, changes.PackageEntry);

                // The same saved copy goes back to every server that reported these changes.
                foreach (var computerName in changes.AllComputerNames)
                {
                    onOutput(OutputLine.Info($"[restore] Rolling back {computerName} …"));
                    var target = new MsDeployTarget(computerName, string.Empty, computerName);
                    await UndoChangesAsync(target, changes.AddedPaths, packagePath, credentials, allowUntrustedCertificate, onOutput, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        finally
        {
            TryDeleteDirectory(work);
            if (download.IsTemporary)
                TryDeleteFile(download.LocalPath);
        }
    }

    /// <summary>
    /// Undoes a deploy on one Web Deploy target: removes what it added, then puts back what it overwrote
    /// or deleted (from <paramref name="packagePath"/>, when anything was saved).
    /// </summary>
    private async Task UndoChangesAsync(
        MsDeployTarget target,
        IReadOnlyList<string> addedPaths,
        string? packagePath,
        PublishCredentials credentials,
        bool allowUntrustedCertificate,
        Action<OutputLine> onOutput,
        CancellationToken cancellationToken)
    {
        foreach (var path in addedPaths)
        {
            onOutput(OutputLine.Info($"[restore] Removing {path} (added by that deploy)"));
            var dest = BuildMsDeployProvider("contentPath", path, target, credentials);
            var args = new List<string> { "-verb:delete", $"-dest:{dest.Arg}" };
            var redacted = new List<string> { "-verb:delete", $"-dest:{dest.Redacted}" };
            if (allowUntrustedCertificate)
            {
                args.Add("-allowUntrusted");
                redacted.Add("-allowUntrusted");
            }

            var exit = await RunMsDeployAsync(args, string.Join(' ', redacted), onOutput, cancellationToken).ConfigureAwait(false);
            if (exit != 0)
                onOutput(OutputLine.Error($"[restore] Could not remove {path} (exit code {exit}) — it may already be gone."));
        }

        if (packagePath is null)
            return;

        // The package carries each saved path, so "auto" puts every file back where it came from.
        // DoNotDeleteRule: saved directories must not wipe files that aren't in the snapshot.
        // useCheckSum: a same-size file changed within the timestamp tolerance must still be put back.
        var autoDest = BuildMsDeployProvider("auto", null, target, credentials);
        var (syncArgs, syncRedacted) = ComposeMsDeployArgs(
            ($"-source:package={Q(packagePath)}", $"-source:package={Q(packagePath)}"),
            autoDest,
            allowUntrustedCertificate,
            extraFlags: ["-enableRule:DoNotDeleteRule", "-useCheckSum"]);

        var syncExit = await RunMsDeployAsync(syncArgs, syncRedacted, onOutput, cancellationToken).ConfigureAwait(false);
        if (syncExit != 0)
            throw new InvalidOperationException($"msdeploy restore failed on {target.ComputerName} (exit code {syncExit}).");
    }

    // ---- Script -WhatIf (only what the script will change) ----------------

    /// <summary>
    /// Runs the script with <c>-WhatIf</c>, reads what it would change on each Web Deploy target (see
    /// <see cref="ScriptWhatIfReport"/>), and saves only the files it would overwrite or delete from each —
    /// one inner package per target inside the snapshot zip.
    /// </summary>
    private async Task<Capture?> BackUpScriptChangesAsync(
        PublishJob job,
        string packagePath,
        Action<OutputLine> onOutput,
        CancellationToken cancellationToken)
    {
        var script = job.Script!;
        onOutput(OutputLine.Info("[backup] Running the script with -WhatIf to find the server files it will change …"));

        var whatIfJob = new PublishJob
        {
            Project = job.Project,
            Script = script.WithArguments($"{script.Arguments} -WhatIf".Trim()),
            Engine = PublishEngineKind.Script,
            Configuration = job.Configuration,
            Credentials = job.Credentials,
            AllowUntrustedCertificate = job.AllowUntrustedCertificate,
        };

        var lines = new List<string>();
        void Collect(OutputLine line)
        {
            lock (lines)
                lines.Add(line.Text);
            onOutput(line);
        }

        var result = await engineFactory!.Get(PublishEngineKind.Script).PublishAsync(whatIfJob, Collect, cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
            throw new InvalidOperationException($"The script's -WhatIf run failed: {result.ErrorMessage ?? result.Status.ToString()}");

        IReadOnlyList<ScriptWhatIfTarget> targets;
        lock (lines)
            targets = ScriptWhatIfReport.Parse(lines);

        if (targets.All(t => t.Changes.IsEmpty))
        {
            onOutput(OutputLine.Info("[backup] The script won't change any files on its targets — no snapshot needed."));
            return null;
        }

        var work = Path.Combine(Path.GetTempPath(), $"sdw_{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(work);
        try
        {
            // The same application on several servers (e.g. behind a load balancer) reports the same changes on
            // each: save those files once, from the first server, and restore that copy to all of them. Servers
            // whose changes differ have drifted apart, so they keep their own copy.
            var groups = targets
                .Where(t => !t.Changes.IsEmpty)
                .GroupBy(t => ChangeSignature(t.Changes))
                .ToList();

            var recorded = new List<BackupTargetChanges>();
            for (var i = 0; i < groups.Count; i++)
            {
                var (computerName, changes) = groups[i].First();
                var replicas = groups[i].Skip(1).Select(t => t.ComputerName).ToList();

                var toSave = changes.ToSave;
                onOutput(OutputLine.Info(
                    $"[backup] {computerName}: will update {changes.Updated.Count}, delete {changes.Deleted.Count} and add " +
                    $"{changes.Added.Count} item(s) — saving the {toSave.Count} it will overwrite or delete."));
                if (replicas.Count > 0)
                {
                    onOutput(OutputLine.Info(
                        $"[backup] {string.Join(", ", replicas)} report{(replicas.Count == 1 ? "s" : "")} exactly the same changes " +
                        "(same application) — restoring will apply this one copy there too."));
                }

                string? entry = null;
                if (toSave.Count > 0)
                {
                    entry = $"target{i}.zip";
                    var manifestPath = Path.Combine(work, $"target{i}.xml");
                    WriteManifest(manifestPath, toSave);

                    var target = new MsDeployTarget(computerName, string.Empty, computerName);
                    var source = BuildMsDeployProvider("manifest", manifestPath, target, job.Credentials);
                    var dest = $"-dest:package={Q(Path.Combine(work, entry))}";
                    var (args, redacted) = ComposeMsDeployArgs(source, (dest, dest), job.AllowUntrustedCertificate);

                    var exit = await RunMsDeployAsync(args, redacted, onOutput, cancellationToken).ConfigureAwait(false);
                    if (exit != 0)
                        throw new InvalidOperationException($"msdeploy backup failed on {computerName} (exit code {exit}).");
                }

                recorded.Add(new BackupTargetChanges
                {
                    ComputerName = computerName,
                    ReplicaComputerNames = replicas,
                    SavedPaths = toSave,
                    AddedPaths = changes.Added,
                    PackageEntry = entry,
                });
            }

            // One snapshot zip holding each target's package (already compressed, so stored as-is).
            using (var zip = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                foreach (var changes in recorded.Where(c => c.PackageEntry is not null))
                    zip.CreateEntryFromFile(Path.Combine(work, changes.PackageEntry!), changes.PackageEntry!, CompressionLevel.NoCompression);
            }

            return new Capture(BackupKind.ScriptPartial, [], []) { Targets = recorded };
        }
        finally
        {
            TryDeleteDirectory(work);
        }
    }

    /// <summary>A msdeploy manifest listing each path to pull, as a <c>contentPath</c> provider.</summary>
    private static void WriteManifest(string manifestPath, IEnumerable<string> paths)
    {
        var manifest = new XElement("sitemanifest",
            paths.Select(p => new XElement("contentPath", new XAttribute("path", p))));
        new XDocument(manifest).Save(manifestPath);
    }

    /// <summary>Runs a command on the server (e.g. <c>net stop MyService</c>) through Web Deploy's runCommand provider.</summary>
    private async Task<int> RunRemoteCommandAsync(
        string command,
        MsDeployTarget target,
        PublishCredentials credentials,
        bool allowUntrustedCertificate,
        Action<OutputLine> onOutput,
        CancellationToken cancellationToken)
    {
        onOutput(OutputLine.Info($"[restore] Running on the server: {command}"));

        // Exit code 2 counts as success so "net stop/start" on a service that's already stopped/started is fine.
        var source = $"-source:runCommand={Q(command.Trim())},waitInterval=10000,waitAttempts=12,successReturnCodes=0x0;0x2";
        var dest = BuildMsDeployProvider("auto", null, target, credentials);
        var (args, redacted) = ComposeMsDeployArgs((source, source), dest, allowUntrustedCertificate);

        return await RunMsDeployAsync(args, redacted, onOutput, cancellationToken).ConfigureAwait(false);
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

        // msdeploy reads its raw command line and rejects an argument quoted as a whole
        // ("Unrecognized argument"), so values are quoted inside each argument (see Q) and the
        // arguments are passed through as-is.
        var result = await processRunner
            .RunRawAsync(msdeploy, string.Join(' ', args), workingDirectory: null, onOutput, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return result.ExitCode;
    }

    /// <summary>Quotes a msdeploy provider value that contains a space or comma, e.g. <c>contentPath="Default Web Site/app"</c>.</summary>
    private static string Q(string value) =>
        value.IndexOfAny([' ', ',']) >= 0 ? $"\"{value}\"" : value;

    /// <summary>
    /// Builds a single <c>-source:</c>/<c>-dest:</c> provider argument plus a redacted copy. A null
    /// <paramref name="providerValue"/> gives a bare provider such as <c>auto</c>.
    /// </summary>
    private static (string Arg, string Redacted) BuildMsDeployProvider(
        string providerKey,
        string? providerValue,
        MsDeployTarget target,
        PublishCredentials credentials)
    {
        // Provider settings are one comma-separated argument; the password lives inside it.
        var common = new List<string>
        {
            providerValue is null ? providerKey : $"{providerKey}={Q(providerValue)}",
            $"computerName={Q(target.ComputerName)}",
        };

        var hasUser = !string.IsNullOrEmpty(credentials.UserName);
        common.Add($"authType={(hasUser ? "Basic" : "NTLM")}");
        if (hasUser)
            common.Add($"userName={Q(credentials.UserName!)}");

        var real = new List<string>(common);
        var safe = new List<string>(common);
        if (!string.IsNullOrEmpty(credentials.Password))
        {
            real.Add($"password={Q(credentials.Password)}");
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

    /// <summary>A script whose Web Deploy targets are only known from its own <c>-WhatIf</c> report.</summary>
    private sealed record ReportedTargets() : DeploymentTarget("the targets the script's -WhatIf run reports");

    private static DeploymentTarget? ResolveTarget(BackupOwner owner, out string? reason)
    {
        if (owner.Script is { } script)
            return ResolveScriptTarget(script, owner.ProjectDirectory, out reason);

        var profile = owner.Profile!;
        var target = ResolveProfileTarget(profile, owner.ProjectDirectory);
        reason = target is null
            ? $"Backup is only supported for MSDeploy and FileSystem profiles (this one is '{profile.WebPublishMethod ?? "unknown"}')."
            : null;
        return target;
    }

    private static DeploymentTarget? ResolveProfileTarget(PublishProfile profile, string projectDirectory)
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

    private static DeploymentTarget? ResolveScriptTarget(ScriptTarget script, string projectDirectory, out string? reason)
    {
        reason = null;
        switch (script.BackupKind)
        {
            case ScriptBackupKind.WebDeploy:
                if (string.IsNullOrWhiteSpace(script.BackupServerUrl) || string.IsNullOrWhiteSpace(script.BackupPath))
                {
                    reason = "Set the Web Deploy server and remote path in the script's backup settings.";
                    return null;
                }

                var url = script.BackupServerUrl.Trim();
                var path = script.BackupPath.Trim();
                return new MsDeployTarget(NormalizeComputerName(url, path), path, $"{url} → {path}");

            case ScriptBackupKind.WhatIf:
                return new ReportedTargets();

            case ScriptBackupKind.Folder:
                if (string.IsNullOrWhiteSpace(script.BackupPath))
                {
                    reason = "Set the folder in the script's backup settings.";
                    return null;
                }

                var raw = script.BackupPath.Trim();
                return new FileSystemTarget(Path.IsPathRooted(raw) ? raw : Path.GetFullPath(Path.Combine(projectDirectory, raw)));

            default:
                reason = "No backup target is configured for this script.";
                return null;
        }
    }

    /// <summary>
    /// WMSvc endpoints need the site scoped via <c>?site=</c>; add it when missing. Not for a physical
    /// path (e.g. <c>D:\Apps\Service</c>), which isn't inside a site.
    /// </summary>
    private static string NormalizeComputerName(string serviceUrl, string contentPath)
    {
        var isPhysicalPath = Path.IsPathRooted(contentPath) || contentPath.Contains(':');
        if (!isPhysicalPath &&
            serviceUrl.Contains("msdeploy.axd", StringComparison.OrdinalIgnoreCase) &&
            !serviceUrl.Contains("site=", StringComparison.OrdinalIgnoreCase))
        {
            var siteRoot = contentPath.Split('/', '\\')[0];
            var separator = serviceUrl.Contains('?') ? '&' : '?';
            return $"{serviceUrl}{separator}site={siteRoot}";
        }

        return serviceUrl;
    }

    // ---- Helpers ----------------------------------------------------------

    /// <summary>Identifies a change list regardless of order, case or path separators.</summary>
    private static string ChangeSignature(MsDeployChangeSet changes)
    {
        static IEnumerable<string> Tagged(string tag, IEnumerable<string> paths) =>
            paths.Select(p => $"{tag} {p.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant()}");

        return string.Join('\n',
            Tagged("U", changes.Updated)
                .Concat(Tagged("D", changes.Deleted))
                .Concat(Tagged("A", changes.Added))
                .Order(StringComparer.Ordinal));
    }

    private static bool IsTrue(PublishProfile profile, string property) =>
        profile.Properties.TryGetValue(property, out var value) && bool.TryParse(value, out var flag) && flag;

    private static string KeyFor(BackupOwner owner)
    {
        // Scripts are keyed by their stable id so renaming one keeps its snapshots.
        if (owner.Script is { } script)
            return $"script_{Sanitize(script.Id)}";

        var profile = owner.Profile!;
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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }
}
