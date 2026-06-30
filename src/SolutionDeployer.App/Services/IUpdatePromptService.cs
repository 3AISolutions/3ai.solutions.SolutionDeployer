namespace SolutionDeployer.App.Services;

/// <summary>Shows the "update available" dialog with release notes; returns true to update now.</summary>
public interface IUpdatePromptService
{
    Task<bool> ConfirmUpdateAsync(string? version, string? notes);
}
