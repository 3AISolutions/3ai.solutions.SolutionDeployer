using SolutionDeployer.Core.Models;

namespace SolutionDeployer.App.ViewModels;

/// <summary>
/// Follows a deploy or restore started from outside the window (over MCP). Called from build threads as well
/// as the UI thread.
/// </summary>
public interface IRunObserver
{
    void Output(string line);

    void TargetChanged(BackupHostViewModel target, PublishStatus status, string? result);

    /// <summary>The run ended: <see cref="PublishStatus.Succeeded"/>, <see cref="PublishStatus.Failed"/> or <see cref="PublishStatus.Cancelled"/>.</summary>
    void Completed(PublishStatus status, string message);
}
