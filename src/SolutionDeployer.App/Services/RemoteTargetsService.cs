using Avalonia.Controls.ApplicationLifetimes;
using SolutionDeployer.App.ViewModels;
using SolutionDeployer.App.Views;
using SolutionDeployer.Core.Configuration;

namespace SolutionDeployer.App.Services;

public sealed class RemoteTargetsService(
    IClassicDesktopStyleApplicationLifetime lifetime,
    SettingsStore settingsStore,
    ICredentialStore credentialStore) : IRemoteTargetsService
{
    public async Task ShowAsync(AppSettings settings)
    {
        var owner = lifetime.MainWindow;
        if (owner is null)
            return;

        var vm = new RemoteTargetsViewModel(settingsStore, settings, credentialStore);
        var window = new RemoteTargetsWindow { DataContext = vm };
        vm.CloseRequested += () => window.Close();

        await window.ShowDialog(owner);
    }
}
