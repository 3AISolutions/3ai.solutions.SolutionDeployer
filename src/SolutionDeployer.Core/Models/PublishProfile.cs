namespace SolutionDeployer.Core.Models;

/// <summary>
/// The on-disk format a publish profile was read from.
/// </summary>
public enum PublishProfileFormat
{
    /// <summary>An MSBuild <c>.pubxml</c> file (Properties/PublishProfiles).</summary>
    PubXml,

    /// <summary>An exported <c>.PublishSettings</c> file (IIS / Azure import-export format).</summary>
    PublishSettings,
}

/// <summary>
/// A single publish profile belonging to a project. The <see cref="Name"/> is what gets
/// passed to <c>/p:PublishProfile=</c>.
/// </summary>
public sealed class PublishProfile
{
    public required string Name { get; init; }

    /// <summary>Absolute path to the profile file on disk.</summary>
    public required string FilePath { get; init; }

    public required PublishProfileFormat Format { get; init; }

    /// <summary>e.g. MSDeploy, FileSystem, FTP. Read from the profile when available.</summary>
    public string? WebPublishMethod { get; init; }

    /// <summary>MSDeploy service URL / publish URL, for display.</summary>
    public string? ServerUrl { get; init; }

    /// <summary>IIS application path / site name, for display.</summary>
    public string? SiteName { get; init; }

    /// <summary>Build configuration declared in the profile (defaults to Release if absent).</summary>
    public string? Configuration { get; init; }

    /// <summary>Username stored in the profile, if any. The password is never read from disk.</summary>
    public string? UserName { get; init; }

    /// <summary>True when the profile (MSDeploy) expects credentials at publish time.</summary>
    public bool RequiresCredentials { get; init; }

    /// <summary>Raw property bag parsed from the profile (lower-cased keys), for advanced display.</summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new Dictionary<string, string>();

    public override string ToString() => Name;
}
