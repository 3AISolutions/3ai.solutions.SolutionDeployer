using Microsoft.Extensions.DependencyInjection;
using SolutionDeployer.Core.Backup;
using SolutionDeployer.Core.Configuration;
using SolutionDeployer.Core.Git;
using SolutionDeployer.Core.Profiles;
using SolutionDeployer.Core.Projects;
using SolutionDeployer.Core.Publishing;
using SolutionDeployer.Core.Solutions;

namespace SolutionDeployer.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers all core services (parsing, discovery, publish engines, orchestration).</summary>
    public static IServiceCollection AddSolutionDeployerCore(this IServiceCollection services)
    {
        services.AddSingleton<IProfileDiscovery, ProfileDiscovery>();
        services.AddSingleton<ISolutionParser, SolutionParser>();
        services.AddSingleton<IProjectLoader, ProjectLoader>();
        services.AddSingleton<ISourceLoader, SourceLoader>();

        services.AddSingleton<ProcessRunner>();
        services.AddSingleton<MsBuildLocator>();
        services.AddSingleton<IPublishEngine, DotnetPublishEngine>();
        services.AddSingleton<IPublishEngine, MsBuildPublishEngine>();
        services.AddSingleton<IPublishEngine, ScriptPublishEngine>();
        services.AddSingleton<IPublishEngineFactory, PublishEngineFactory>();
        services.AddSingleton<DeploymentRunner>();

        services.AddSingleton<MsDeployLocator>();
        services.AddSingleton<IBackupStoreProvider>(sp => new BackupStoreProvider(
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<ICredentialStore>()));
        services.AddSingleton<IBackupService>(sp => new BackupService(
            sp.GetRequiredService<ProcessRunner>(),
            sp.GetRequiredService<MsDeployLocator>(),
            sp.GetRequiredService<IBackupStoreProvider>(),
            retention: sp.GetRequiredService<SettingsStore>().Load().BackupRetention));

        services.AddSingleton<IGitHistoryService, GitHistoryService>();

        services.AddSingleton<SettingsStore>();
        services.AddSingleton<ICredentialStore>(_ => CredentialStoreFactory.Create());

        return services;
    }
}
