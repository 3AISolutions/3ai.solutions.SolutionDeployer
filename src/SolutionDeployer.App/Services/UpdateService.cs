using Velopack;
using Velopack.Sources;

namespace SolutionDeployer.App.Services;

public sealed record UpdateCheckResult(bool UpdateAvailable, string? Version, string Message);

/// <summary>
/// Wraps Velopack to check for, download, and apply updates published to GitHub Releases.
/// No-ops gracefully when running outside an installed (packaged) context, e.g. during development.
/// </summary>
public sealed class UpdateService
{
    public async Task<UpdateCheckResult> CheckForUpdatesAsync(string repository, CancellationToken cancellationToken = default)
    {
        var mgr = CreateManager(repository);
        if (!mgr.IsInstalled)
            return new UpdateCheckResult(false, mgr.CurrentVersion?.ToString(), "Running from source (updates apply only to installed builds).");

        var info = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
        if (info is null)
            return new UpdateCheckResult(false, mgr.CurrentVersion?.ToString(), "You are on the latest version.");

        return new UpdateCheckResult(true, info.TargetFullRelease.Version.ToString(), $"Update available: {info.TargetFullRelease.Version}");
    }

    /// <summary>
    /// Downloads the newest release (reporting 0–100% progress) and restarts into it. Does not return
    /// when an update is applied.
    /// </summary>
    public async Task<UpdateCheckResult> DownloadAndApplyAsync(
        string repository,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var mgr = CreateManager(repository);
        if (!mgr.IsInstalled)
            return new UpdateCheckResult(false, mgr.CurrentVersion?.ToString(), "Updates apply only to installed builds.");

        var info = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
        if (info is null)
            return new UpdateCheckResult(false, mgr.CurrentVersion?.ToString(), "You are on the latest version.");

        await mgr.DownloadUpdatesAsync(info, progress is null ? null : progress.Report, cancellationToken)
            .ConfigureAwait(false);

        // Restarts the application into the new version; this call does not return.
        mgr.ApplyUpdatesAndRestart(info.TargetFullRelease);
        return new UpdateCheckResult(true, info.TargetFullRelease.Version.ToString(), "Restarting…");
    }

    private static UpdateManager CreateManager(string repository)
    {
        var url = repository.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? repository
            : $"https://github.com/{repository.Trim('/')}";
        return new UpdateManager(new GithubSource(url, null, false));
    }
}
