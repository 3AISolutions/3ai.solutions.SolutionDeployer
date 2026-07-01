using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SolutionDeployer.App.ViewModels;

/// <summary>
/// Backs the "what's new" dialog: shows the release notes for the running version. Read-only, with a
/// single Close action. Displayed on first launch of a new version and on demand from the toolbar.
/// </summary>
public partial class WhatsNewViewModel(string version, string? notes) : ObservableObject
{
    public string Heading => $"What's new in {version}";

    public string Notes => string.IsNullOrWhiteSpace(notes)
        ? "No release notes are available for this version yet."
        : notes!;

    public event Action? CloseRequested;

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();
}