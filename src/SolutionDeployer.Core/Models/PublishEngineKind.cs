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

    /// <summary>Run a user-supplied script (.ps1/.sh/.bash/.cmd/.bat) as the deployment.</summary>
    Script,
}
