namespace SolutionDeployer.App.Services;

/// <summary>Outcome of the pre-deploy confirmation dialog.</summary>
public readonly record struct DeployConfirmation(bool Confirmed, bool DontAskAgain);

/// <summary>Shows a modal confirmation listing what is about to deploy.</summary>
public interface IDeployConfirmationService
{
    Task<DeployConfirmation> ConfirmAsync(IReadOnlyList<string> targets, bool runInParallel);

    /// <summary>Asks the user to confirm a destructive action (e.g. removing a source); true to proceed.</summary>
    Task<bool> ConfirmActionAsync(string heading, string message, string confirmLabel);
}
