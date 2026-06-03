namespace SolutionDeployer.App.Services;

/// <summary>Abstracts the native open-file dialog so the view model stays testable.</summary>
public interface IFilePickerService
{
    Task<string?> PickSolutionAsync();
}
