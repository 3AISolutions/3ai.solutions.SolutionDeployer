using System.Xml.Linq;

namespace SolutionDeployer.Core.Projects;

/// <summary>Inspects a project file's format.</summary>
public static class ProjectFormat
{
    // The "Web Application" project type in <ProjectTypeGuids>.
    private const string WebApplicationProjectType = "349c5851-65df-11da-9384-00065b846f21";

    /// <summary>
    /// True for a classic (non-SDK) ASP.NET Web Application project. For these, <c>/t:Publish</c> is the
    /// ClickOnce target and silently skips the project ("Skipping unpublishable project"); web publishing
    /// only runs through <c>/p:DeployOnBuild=true</c>.
    /// </summary>
    /// <summary>
    /// True when only full <c>msbuild.exe</c> can build the project: it's a classic web project, or it (or
    /// anything it references) is a classic, non-SDK project. <c>dotnet</c> can't resolve a classic
    /// project's NuGet <c>PackageReference</c>s, so compiling one fails (e.g. BC30002 "Type
    /// 'ConfigurationRoot' is not defined") — and only succeeds when an earlier msbuild.exe build happened
    /// to leave its output up to date, which makes the failure look random.
    /// </summary>
    public static bool RequiresMsBuild(string projectPath, out string? classicProject)
    {
        classicProject = null;
        if (IsClassicWebProject(projectPath))
        {
            classicProject = projectPath;
            return true;
        }

        classicProject = ProjectGraph.BuildClosure(projectPath).FirstOrDefault(IsClassicProject);
        return classicProject is not null;
    }

    /// <summary>A non-SDK (old-style) project file.</summary>
    public static bool IsClassicProject(string projectPath)
    {
        try
        {
            var root = XDocument.Load(projectPath).Root;
            return root is not null &&
                   root.Attribute("Sdk") is null &&
                   !root.Elements().Any(e => e.Name.LocalName == "Sdk") &&
                   !root.Elements().Any(e => e.Name.LocalName == "Import" && e.Attribute("Sdk") is not null);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsClassicWebProject(string projectPath)
    {
        try
        {
            var root = XDocument.Load(projectPath).Root;
            if (root is null || root.Attribute("Sdk") is not null)
                return false;

            return root.Descendants().Any(e =>
                (e.Name.LocalName == "ProjectTypeGuids" &&
                 e.Value.Contains(WebApplicationProjectType, StringComparison.OrdinalIgnoreCase)) ||
                (e.Name.LocalName == "Import" &&
                 (e.Attribute("Project")?.Value.Contains("Microsoft.WebApplication.targets", StringComparison.OrdinalIgnoreCase) ?? false) &&
                 e.Attribute("Condition")?.Value.Trim() != "false"));
        }
        catch
        {
            return false;
        }
    }
}
