using SolutionDeployer.App.ViewModels;

namespace SolutionDeployer.App.Services;

/// <summary>Shows the modal post-deploy summary (per-project success/failure).</summary>
public interface IDeploySummaryService
{
    Task ShowAsync(DeploySummaryViewModel summary);
}
