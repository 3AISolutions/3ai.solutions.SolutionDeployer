namespace SolutionDeployer.Core.Models;

/// <summary>
/// A fully-specified unit of deployment work: publish one project with one profile using one engine.
/// A "deploy all selected" run is just a list of these.
/// </summary>
public sealed class PublishJob
{
    public required DeploymentProject Project { get; init; }

    public required PublishProfile Profile { get; init; }

    public required PublishEngineKind Engine { get; init; }

    /// <summary>Build configuration. Falls back to the profile's, then "Release".</summary>
    public string Configuration { get; init; } = "Release";

    public PublishCredentials Credentials { get; init; } = PublishCredentials.None;

    /// <summary>Adds <c>/p:AllowUntrustedCertificate=true</c> when true.</summary>
    public bool AllowUntrustedCertificate { get; init; } = true;

    /// <summary>Extra MSBuild properties, applied as <c>/p:Key=Value</c>. Keys here override defaults.</summary>
    public IReadOnlyDictionary<string, string> AdditionalProperties { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Stable identifier for correlating output lines with a job in the UI.</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    public string DisplayName => $"{Project.Name} → {Profile.Name}";
}
