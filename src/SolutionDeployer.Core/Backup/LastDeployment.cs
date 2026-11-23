using System.Text.Json;
using System.Text.Json.Serialization;

namespace SolutionDeployer.Core.Backup;

/// <summary>
/// When a target was last deployed successfully, and by whom. Kept beside the target's snapshots in its backup
/// destination (<see cref="FileName"/> in the owner's folder), so everyone sharing a bucket sees the latest deploy,
/// whoever made it, and whether or not it took a snapshot.
/// </summary>
public sealed class LastDeployment
{
    /// <summary>Not a <c>.json</c> name, so snapshot listings (which read every manifest) pass over it.</summary>
    public const string FileName = "last-deployment.info";

    public required DateTimeOffset DeployedUtc { get; init; }

    /// <summary>The OS user who deployed.</summary>
    public required string User { get; init; }

    public required string Machine { get; init; }

    /// <summary>"andre on BUILD-PC".</summary>
    [JsonIgnore]
    public string By => $"{User} on {Machine}";

    public static LastDeployment Now() => new()
    {
        DeployedUtc = DateTimeOffset.UtcNow,
        User = Environment.UserName,
        Machine = Environment.MachineName,
    };

    /// <summary>Who is deploying from this machine, as snapshots record it.</summary>
    public static string CurrentDeployer => $"{Environment.UserName} on {Environment.MachineName}";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string Serialize() => JsonSerializer.Serialize(this, Options);

    public static LastDeployment? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<LastDeployment>(json);
        }
        catch
        {
            return null;
        }
    }
}
