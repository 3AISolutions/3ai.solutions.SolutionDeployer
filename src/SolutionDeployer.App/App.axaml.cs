using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using SolutionDeployer.App.Services;
using SolutionDeployer.App.ViewModels;
using SolutionDeployer.App.Views;
using SolutionDeployer.Core;
using SolutionDeployer.Core.WhatsNew;

namespace SolutionDeployer.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new ServiceCollection();
            services.AddSolutionDeployerCore();
            services.AddSingleton<IClassicDesktopStyleApplicationLifetime>(desktop);
            services.AddSingleton<IFilePickerService, FilePickerService>();
            services.AddSingleton<IScriptEditorService, ScriptEditorService>();
            services.AddSingleton<IDeployConfirmationService, DeployConfirmationService>();
            services.AddSingleton<IReleaseSummaryService, ReleaseSummaryService>();
            services.AddSingleton<IRemoteTargetsService, RemoteTargetsService>();
            services.AddSingleton<IUpdatePromptService, UpdatePromptService>();
            services.AddSingleton<WhatsNewProvider>();
            services.AddSingleton<IWhatsNewService, WhatsNewService>();
            services.AddSingleton<UpdateService>();
            services.AddSingleton<MainWindowViewModel>();

            var provider = services.BuildServiceProvider();
            var viewModel = provider.GetRequiredService<MainWindowViewModel>();

            desktop.MainWindow = new MainWindow { DataContext = viewModel };

            // Fire-and-forget startup work: reopen the last solution, then check for updates.
            // Continuations resume on the UI thread (Avalonia sync context), so VM/UI access is safe.
            _ = StartupAsync(viewModel);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task StartupAsync(MainWindowViewModel viewModel)
    {
        await viewModel.RunStartupLoadAsync();
        await viewModel.RunStartupWhatsNewCheckAsync();
        await viewModel.RunStartupUpdateCheckAsync();
    }
}
