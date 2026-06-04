namespace SolutionDeployer.App.Services;

/// <summary>Abstracts the native open-file dialogs so the view model stays testable.</summary>
public interface IFilePickerService
{
    Task<string?> PickSolutionAsync();

    Task<string?> PickProjectAsync();

    /// <summary>Picks a deployment script, opening at <paramref name="startDirectory"/> when given.</summary>
    Task<string?> PickScriptAsync(string? startDirectory = null);
}
