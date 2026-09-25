using Avalonia.Controls.ApplicationLifetimes;
using SolutionDeployer.App.ViewModels;
using SolutionDeployer.App.Views;

namespace SolutionDeployer.App.Services;

public sealed class DeploySummaryService(IClassicDesktopStyleApplicationLifetime lifetime) : IDeploySummaryService
{
    public async Task ShowAsync(DeploySummaryViewModel summary)
    {
        var owner = lifetime.MainWindow;
        if (owner is null)
            return;

        var window = new DeploySummaryWindow { DataContext = summary };
        summary.CloseRequested += () => window.Close();

        await window.ShowDialog(owner);
    }
}
