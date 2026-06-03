using Microsoft.Extensions.DependencyInjection;
using SolutionDeployer.Core.Configuration;
using SolutionDeployer.Core.Profiles;
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

        services.AddSingleton<ProcessRunner>();
        services.AddSingleton<MsBuildLocator>();
        services.AddSingleton<IPublishEngine, DotnetPublishEngine>();
        services.AddSingleton<IPublishEngine, MsBuildPublishEngine>();
        services.AddSingleton<IPublishEngineFactory, PublishEngineFactory>();
        services.AddSingleton<DeploymentRunner>();

        services.AddSingleton<SettingsStore>();

        return services;
    }
}
