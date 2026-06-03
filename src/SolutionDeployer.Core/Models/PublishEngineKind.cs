namespace SolutionDeployer.Core.Models;

/// <summary>
/// Which build tool drives the publish.
/// </summary>
public enum PublishEngineKind
{
    /// <summary>Invoke <c>dotnet publish</c> (cross-platform, requires the .NET SDK).</summary>
    Dotnet,

    /// <summary>Invoke <c>msbuild.exe /t:Publish</c> (Windows-only, located via vswhere; needed for Web Deploy / full-framework).</summary>
    MsBuild,
}
