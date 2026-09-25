using Avalonia.Controls.ApplicationLifetimes;
using SolutionDeployer.App.ViewModels;
using SolutionDeployer.App.Views;

namespace SolutionDeployer.App.Services;

public sealed class DeployConfirmationService(IClassicDesktopStyleApplicationLifetime lifetime) : IDeployConfirmationService
{
    public async Task<DeployConfirmation> ConfirmAsync(IReadOnlyList<string> targets, bool runInParallel)
    {
        var owner = lifetime.MainWindow;
        if (owner is null)
            return new DeployConfirmation(Confirmed: true, DontAskAgain: false);

        var vm = new ConfirmDeployViewModel(targets, runInParallel);
        var window = new ConfirmDeployWindow { DataContext = vm };
        vm.CloseRequested += () => window.Close();

        await window.ShowDialog(owner);
        return new DeployConfirmation(vm.Confirmed, vm.DontAskAgain);
    }

    public async Task<bool> ConfirmActionAsync(string heading, string message, string confirmLabel)
    {
        var owner = lifetime.MainWindow;
        if (owner is null)
            return false;

        var vm = new ConfirmActionViewModel(heading, message, confirmLabel);
        var window = new ConfirmActionWindow { DataContext = vm };
        vm.CloseRequested += () => window.Close();

        await window.ShowDialog(owner);
        return vm.Confirmed;
    }
}
