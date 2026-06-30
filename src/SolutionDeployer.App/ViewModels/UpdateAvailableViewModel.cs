using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SolutionDeployer.App.ViewModels;

/// <summary>Backs the "update available" dialog: shows the new version + release notes, and asks to update.</summary>
public partial class UpdateAvailableViewModel(string? version, string? notes) : ObservableObject
{
    public string Heading => $"Version {version} is available";

    public string Notes => string.IsNullOrWhiteSpace(notes) ? "No release notes were provided for this version." : notes!;

    /// <summary>True when the user chose to update now.</summary>
    public bool Accepted { get; private set; }

    public event Action? CloseRequested;

    [RelayCommand]
    private void UpdateNow()
    {
        Accepted = true;
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Later()
    {
        Accepted = false;
        CloseRequested?.Invoke();
    }
}
