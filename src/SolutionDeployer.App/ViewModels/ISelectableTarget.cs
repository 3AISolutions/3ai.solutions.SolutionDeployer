using SolutionDeployer.Core.Models;

namespace SolutionDeployer.App.ViewModels;

/// <summary>A deploy target the user can tick and that shows run status: a publish profile or a script.</summary>
public interface ISelectableTarget
{
    bool IsSelected { get; set; }

    PublishStatus Status { get; set; }

    /// <summary>
    /// True while the target belongs to the current (or last) run. <see cref="PublishStatus.Pending"/> is
    /// also every idle row's default, so this is what tells "waiting in the queue" apart from "not deploying".
    /// </summary>
    bool IsQueued { get; set; }

    string ResultText { get; set; }
}

/// <summary>Display text for a target's run status (shown as the status dot's tooltip).</summary>
public static class TargetStatusText
{
    public static string Describe(PublishStatus status, bool isQueued) => status switch
    {
        PublishStatus.Pending => isQueued ? "Waiting to deploy" : string.Empty,
        PublishStatus.Running => "Deploying…",
        PublishStatus.Succeeded => "Deployed successfully",
        PublishStatus.Failed => "Deploy failed",
        PublishStatus.Cancelled => "Cancelled",
        _ => string.Empty,
    };
}
