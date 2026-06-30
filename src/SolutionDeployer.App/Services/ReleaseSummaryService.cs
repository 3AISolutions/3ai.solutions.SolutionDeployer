using Avalonia.Controls.ApplicationLifetimes;
using SolutionDeployer.App.ViewModels;
using SolutionDeployer.App.Views;
using SolutionDeployer.Core.Git;

namespace SolutionDeployer.App.Services;

public sealed class ReleaseSummaryService(IClassicDesktopStyleApplicationLifetime lifetime) : IReleaseSummaryService
{
    public async Task ShowAsync(ReleaseSummary summary)
    {
        var owner = lifetime.MainWindow;
        if (owner is null)
            return;

        var vm = new ReleaseSummaryViewModel(summary);
        var window = new ReleaseSummaryWindow { DataContext = vm };
        vm.CloseRequested += () => window.Close();

        await window.ShowDialog(owner);
    }
}
