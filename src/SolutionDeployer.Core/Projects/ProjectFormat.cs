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
