using SolutionDeployer.Core.Models;

namespace SolutionDeployer.Core.Backup;

/// <summary>
/// A publish that is already built: the backup's preview build of a Web Deploy profile, kept so the publish can
/// sync it straight to the server instead of building the project a second time. Dispose deletes the build.
/// </summary>
public sealed class PreparedDeployment : IDisposable
{
    private readonly Func<Action<OutputLine>, CancellationToken, Task<PublishResult>> _deploy;
    private readonly Action _cleanup;
    private int _disposed;

    internal PreparedDeployment(
        bool isUpToDate,
        Func<Action<OutputLine>, CancellationToken, Task<PublishResult>> deploy,
        Action cleanup)
    {
        IsUpToDate = isUpToDate;
        _deploy = deploy;
        _cleanup = cleanup;
    }

    /// <summary>The change check found the server already matches this build: there is nothing to deploy.</summary>
    public bool IsUpToDate { get; }

    /// <summary>Syncs the build to the server with the profile's Web Deploy rules.</summary>
    public Task<PublishResult> DeployAsync(Action<OutputLine> onOutput, CancellationToken cancellationToken = default) =>
        _deploy(onOutput, cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _cleanup();
    }
}

/// <summary>
/// What <see cref="IBackupService.BackUpForPublishAsync"/> produced: the snapshot (null when there was nothing
/// to save) and, when the job can skip its own build, the <see cref="PreparedDeployment"/> to publish instead.
/// </summary>
public sealed record PublishBackup(DeploymentBackup? Backup, PreparedDeployment? Deployment);
