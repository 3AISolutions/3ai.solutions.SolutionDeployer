using Avalonia.Controls.ApplicationLifetimes;
using SolutionDeployer.App.ViewModels;
using SolutionDeployer.App.Views;

namespace SolutionDeployer.App.Services;

public sealed class UpdatePromptService(IClassicDesktopStyleApplicationLifetime lifetime) : IUpdatePromptService
{
    public async Task<bool> ConfirmUpdateAsync(string? version, string? notes)
    {
        var owner = lifetime.MainWindow;
        if (owner is null)
            return false;

        var vm = new UpdateAvailableViewModel(version, notes);
        var window = new UpdateAvailableWindow { DataContext = vm };
        vm.CloseRequested += () => window.Close();

        await window.ShowDialog(owner);
        return vm.Accepted;
    }
}
