using System.Text.Json.Serialization;

namespace SolutionDeployer.Core.Configuration;

/// <summary>
/// A named entry in the credential library: a username that several targets can share. Its password lives in
/// the <see cref="ICredentialStore"/> under <see cref="PasswordKey"/>, never in settings.
/// </summary>
public sealed class SavedCredential
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Display name, e.g. "Prod IIS deploy user".</summary>
    public string Name { get; set; } = string.Empty;

    public string UserName { get; set; } = string.Empty;

    /// <summary>Free-form notes (where it's used, who owns it). Never a secret.</summary>
    public string? Notes { get; set; }

    /// <summary>Credential-store key of this entry's password.</summary>
    [JsonIgnore]
    public string PasswordKey => KeyFor(Id);

    public const string KeyPrefix = "credential:";

    public static string KeyFor(string id) => KeyPrefix + id;

    public override string ToString() => Name;
}
