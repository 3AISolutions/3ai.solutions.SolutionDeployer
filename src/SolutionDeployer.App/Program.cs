using Avalonia;
using SolutionDeployer.App.Services;
using Velopack;

namespace SolutionDeployer.App;

internal static class Program
{
    /// <summary>Launched at sign-in (<see cref="AutoStartService.BackgroundArgument"/>): start in the tray.</summary>
    public static bool StartInBackground { get; private set; }

    /// <summary>This copy's claim on being the only one running; listens for later launches.</summary>
    public static SingleInstance? Instance { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles Velopack install/update/uninstall hooks and may exit the process.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => AutoStartService.Remove())
            .Run();

        StartInBackground = args.Contains(AutoStartService.BackgroundArgument);

        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            // Already running (probably in the tray): show that copy, unless this launch was the sign-in one.
            if (!StartInBackground)
                SingleInstance.ActivateExisting();
            return;
        }

        Instance = instance;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
