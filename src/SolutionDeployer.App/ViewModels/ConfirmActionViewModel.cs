using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SolutionDeployer.App.ViewModels;

/// <summary>Backs a generic yes/no confirmation for a destructive action.</summary>
public partial class ConfirmActionViewModel(string heading, string message, string confirmLabel) : ObservableObject
{
    public string Heading { get; } = heading;

    public string Message { get; } = message;

    public string ConfirmLabel { get; } = confirmLabel;

    public bool Confirmed { get; private set; }

    public event Action? CloseRequested;

    [RelayCommand]
    private void Confirm()
    {
        Confirmed = true;
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        Confirmed = false;
        CloseRequested?.Invoke();
    }
}
