namespace SolutionDeployer.Core.Configuration;

/// <summary>
/// What was deployed for a profile last time: the commit SHA of each project in its dependency
/// closure (keyed by project file path), plus when the deploy happened. Used as the baseline for the
/// next release summary.
/// </summary>
public sealed class DeployRecord
{
    public DateTimeOffset DeployedUtc { get; set; }

    /// <summary>Project file path → commit SHA at deploy time.</summary>
    public Dictionary<string, string> ProjectShas { get; set; } = new();
}
