using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using SolutionDeployer.App.ViewModels;
using SolutionDeployer.App.Views;
using SolutionDeployer.Core.Backup;
using SolutionDeployer.Core.Configuration;

namespace SolutionDeployer.App.Services;

public sealed class BackupManagerService(
    IClassicDesktopStyleApplicationLifetime lifetime,
    IBackupCleanupService cleanup,
    SettingsStore settingsStore) : IBackupManagerService
{
    public async Task<bool> ShowAsync(AppSettings settings, IReadOnlyCollection<BackupOwner> knownOwners)
    {
        var owner = lifetime.MainWindow;
        if (owner is null)
            return false;

        var window = new BackupManagerWindow();
        // Confirmations are owned by this window, so they always open on top of it.
        var vm = new BackupManagerViewModel(cleanup, settingsStore, settings, knownOwners,
            (heading, message, label) => ConfirmAsync(window, heading, message, label));
        window.DataContext = vm;
        vm.CloseRequested += () => window.Close();
        window.Opened += (_, _) => _ = vm.RefreshAsync();

        await window.ShowDialog(owner);
        return vm.DeletedAny;
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
