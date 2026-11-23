using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using SolutionDeployer.App.ViewModels;
using SolutionDeployer.App.Views;

namespace SolutionDeployer.App.Services;

public sealed class SnapshotsService(IClassicDesktopStyleApplicationLifetime lifetime) : ISnapshotsService
{
    public async Task ShowAsync(SnapshotsViewModel snapshots)
    {
        var owner = lifetime.MainWindow;
        if (owner is null)
            return;

        var window = new SnapshotsWindow { DataContext = snapshots };
        // Confirmations are owned by this window, so they always open on top of it.
        snapshots.Confirm = (heading, message, label) => ConfirmAsync(window, heading, message, label);
        snapshots.CloseRequested += () => window.Close();
        window.Opened += (_, _) => _ = snapshots.RefreshAsync();

        await window.ShowDialog(owner);
    }

    private static async Task<bool> ConfirmAsync(Window owner, string heading, string message, string confirmLabel)
    {
        var vm = new ConfirmActionViewModel(heading, message, confirmLabel);
        var window = new ConfirmActionWindow { DataContext = vm };
        vm.CloseRequested += () => window.Close();

        await window.ShowDialog(owner);
        return vm.Confirmed;
    }
}
