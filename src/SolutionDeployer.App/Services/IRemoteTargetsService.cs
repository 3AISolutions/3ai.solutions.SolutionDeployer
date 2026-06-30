using SolutionDeployer.Core.Configuration;

namespace SolutionDeployer.App.Services;

/// <summary>Shows the modal "Backup destinations" manager for the named S3 remotes.</summary>
public interface IRemoteTargetsService
{
    Task ShowAsync(AppSettings settings);
}
