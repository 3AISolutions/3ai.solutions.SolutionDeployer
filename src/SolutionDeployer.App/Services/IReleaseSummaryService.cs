using SolutionDeployer.Core.Git;

namespace SolutionDeployer.App.Services;

/// <summary>Shows the release-summary window for a built <see cref="ReleaseSummary"/>.</summary>
public interface IReleaseSummaryService
{
    Task ShowAsync(ReleaseSummary summary);
}
