using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using SolutionDeployer.App.Services;
using SolutionDeployer.App.ViewModels;
using SolutionDeployer.App.Views;
using SolutionDeployer.Core;
using SolutionDeployer.Core.WhatsNew;
using SolutionDeployer.Mcp;

namespace SolutionDeployer.App;

public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainWindow? _window;
    private MainWindowViewModel? _viewModel;
    private IMcpServerService? _mcpServer;
    private bool _startupPromptsPending;

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
            services.AddSingleton<IDeploySummaryService, DeploySummaryService>();
            services.AddSingleton<IReleaseSummaryService, ReleaseSummaryService>();
            services.AddSingleton<IRemoteTargetsService, RemoteTargetsService>();
            services.AddSingleton<IBackupManagerService, BackupManagerService>();
            services.AddSingleton<IUpdatePromptService, UpdatePromptService>();
            services.AddSingleton<WhatsNewProvider>();
            services.AddSingleton<IWhatsNewService, WhatsNewService>();
            services.AddSingleton<UpdateService>();
            services.AddSingleton<IAutoStartService, AutoStartService>();
            services.AddSingleton<RunRegistry>();
            services.AddSingleton<IDeployerHost, McpDeployerBridge>();
            // Resolved on first start: the bridge needs the view model, which needs this service.
            services.AddSingleton<Func<IDeployerHost>>(sp => sp.GetRequiredService<IDeployerHost>);
            services.AddSingleton<IMcpServerService, McpServerService>();
            services.AddSingleton<MainWindowViewModel>();

            var provider = services.BuildServiceProvider();
            _desktop = desktop;
            _viewModel = provider.GetRequiredService<MainWindowViewModel>();
            _mcpServer = provider.GetRequiredService<IMcpServerService>();

            // The app may outlive its window (tray), so it only ends on an explicit quit or a real close.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // Off the UI thread: the stop resumes on its context, which this handler is blocking.
            desktop.Exit += (_, _) => Task.Run(_mcpServer.StopAsync).Wait(TimeSpan.FromSeconds(3));

            _window = new MainWindow { DataContext = _viewModel };
            _window.Closed += (_, _) => desktop.Shutdown();

            // Started at sign-in: stay in the tray. Without the tray there'd be no way back to the window.
            var startHidden = Program.StartInBackground && _viewModel.CloseToTray;
            if (!startHidden)
                desktop.MainWindow = _window;

            CreateTrayIcon(_viewModel);

            if (Program.Instance is { } instance)
            {
                instance.ActivationRequested += () => Dispatcher.UIThread.Post(ShowMainWindow);
                instance.Listen();
            }

            // Fire-and-forget startup work: reopen the last solution, then check for updates.
            // Continuations resume on the UI thread (Avalonia sync context), so VM/UI access is safe.
            _ = StartupAsync(_viewModel, showPrompts: !startHidden);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartupAsync(MainWindowViewModel viewModel, bool showPrompts)
    {
        await viewModel.RunStartupLoadAsync();
        await viewModel.RunStartupBackgroundServicesAsync();

        // What's new and the update prompt are windows: in the tray they wait until the window is first shown.
        if (showPrompts)
            await RunStartupPromptsAsync(viewModel);
        else
            _startupPromptsPending = true;
    }

    private static async Task RunStartupPromptsAsync(MainWindowViewModel viewModel)
    {
        await viewModel.RunStartupWhatsNewCheckAsync();
        await viewModel.RunStartupUpdateCheckAsync();
    }

    /// <summary>Brings the window back from the tray (or from behind other windows).</summary>
    private void ShowMainWindow()
    {
        if (_desktop is null || _window is null || _viewModel is null)
            return;

        _desktop.MainWindow ??= _window;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();

        if (_startupPromptsPending)
        {
            _startupPromptsPending = false;
            _ = RunStartupPromptsAsync(_viewModel);
        }
    }

    private void Quit() => _desktop?.Shutdown();

    private void CreateTrayIcon(MainWindowViewModel viewModel)
    {
        var open = new NativeMenuItem("Open Solution Deployer");
        open.Click += (_, _) => ShowMainWindow();
        var mcpStatus = new NativeMenuItem { IsEnabled = false };
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => Quit();

        var tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://SolutionDeployer.App/Assets/app-icon.ico"))),
            ToolTipText = "3ai Solution Deployer",
            Menu = new NativeMenu { Items = { open, mcpStatus, new NativeMenuItemSeparator(), quit } },
        };
        tray.Clicked += (_, _) => ShowMainWindow();

        void Sync()
        {
            tray.IsVisible = viewModel.CloseToTray;
            mcpStatus.Header = viewModel.McpServerEnabled ? $"Claude (MCP): {viewModel.McpStatus}" : "Claude (MCP): off";
        }

        Sync();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainWindowViewModel.CloseToTray) or nameof(MainWindowViewModel.McpServerEnabled)
                or nameof(MainWindowViewModel.McpStatus))
                Sync();
        };

        TrayIcon.SetIcons(this, new TrayIcons { tray });
    }
}
