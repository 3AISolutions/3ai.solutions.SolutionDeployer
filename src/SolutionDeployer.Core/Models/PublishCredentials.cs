namespace SolutionDeployer.Core.Models;

/// <summary>
/// Credentials supplied for an MSDeploy / Web Deploy publish. Passwords are never persisted
/// to disk by the core library.
/// </summary>
public sealed class PublishCredentials
{
    public string? UserName { get; init; }

    public string? Password { get; init; }

    public bool HasAny => !string.IsNullOrEmpty(UserName) || !string.IsNullOrEmpty(Password);

    public static readonly PublishCredentials None = new();
}
