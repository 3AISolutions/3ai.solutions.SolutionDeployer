using Avalonia.Controls.ApplicationLifetimes;
using SolutionDeployer.App.ViewModels;
using SolutionDeployer.App.Views;
using SolutionDeployer.Core.WhatsNew;

namespace SolutionDeployer.App.Services;

public sealed class WhatsNewService(IClassicDesktopStyleApplicationLifetime lifetime, WhatsNewProvider provider)
    : IWhatsNewService
{
    private static string? CurrentVersion { get; } =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : null;

    public async Task<string?> GetNotesForCurrentVersionAsync(CancellationToken cancellationToken = default) =>
        await GetNotesForCurrentVersionAsync("3AISolutions/3ai.solutions.SolutionDeployer", cancellationToken);

    public async Task<string?> GetNotesForCurrentVersionAsync(string repository, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(CurrentVersion))
            return null;

        return await provider.GetNotesForVersionAsync(repository, CurrentVersion, cancellationToken);
    }

    public async Task<bool> ShowAsync(string? notes = null)
    {
        var owner = lifetime.MainWindow;
        if (owner is null)
            return false;

        notes ??= await GetNotesForCurrentVersionAsync();
        var vm = new WhatsNewViewModel(CurrentVersion ?? "this version", notes);
        var window = new WhatsNewWindow { DataContext = vm };
        vm.CloseRequested += () => window.Close();

        await window.ShowDialog(owner);
        return true;
    }
}