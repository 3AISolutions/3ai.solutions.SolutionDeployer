using System.Text.RegularExpressions;
using SolutionDeployer.Core.Models;

namespace SolutionDeployer.Core.Backup;

/// <summary>
/// Whether a Web Deploy publish can be replaced by syncing the backup's preview build straight to the server
/// (see <see cref="PreparedDeployment"/>). That sync applies the profile's file rules — SkipExtraFilesOnServer,
/// ExcludeApp_Data, EnableMsDeployAppOffline, MSDeployEnableWebConfigEncryptRule, EnableMSDeployBackup — but
/// not what only MSBuild's Web Deploy step does: parameters (e.g. a connection string set at deploy time),
/// custom skip/replace rules, database publishing, EF migrations or IIS settings. A profile or project that
/// uses any of those publishes through MSBuild as before.
/// </summary>
internal static partial class DirectDeployEligibility
{
    // MSBuild items that change what Web Deploy does beyond copying the built files.
    private static readonly string[] DeployTimeItems =
    [
        "MSDeployParameterValue",
        "MsDeployDeclareParameters",
        "MsDeploySkipRules",
        "MsDeployReplaceRules",
        "MsDeployAdditionalDestinationProviderSettings",
        "DestinationConnectionStrings",
        "EFMigrations",
    ];

    // Database publishing configured in a profile: <PublishDatabaseSettings> … <ObjectGroup …>.
    [GeneratedRegex(@"<PublishDatabaseSettings>.*?<ObjectGroup\b", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DatabasePublishing();

    public static bool IsEligible(PublishJob job, out string? reason)
    {
        var profile = job.Profile!;
        if (IsTrue(profile, "IncludeIisSettings"))
        {
            reason = "the profile syncs IIS settings (IncludeIisSettings)";
            return false;
        }

        if (profile.Properties.TryGetValue("ProjectParametersXMLFile", out var parametersFile) &&
            !string.IsNullOrWhiteSpace(parametersFile))
        {
            reason = "the profile declares Web Deploy parameters (ProjectParametersXMLFile)";
            return false;
        }

        var projectDir = job.Project.ProjectDirectory;
        if (File.Exists(Path.Combine(projectDir, "Parameters.xml")))
        {
            reason = "the project declares Web Deploy parameters (Parameters.xml)";
            return false;
        }

        foreach (var file in FilesThatShapeThePublish(job))
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            var item = DeployTimeItems.FirstOrDefault(name =>
                text.Contains("<" + name, StringComparison.OrdinalIgnoreCase));
            if (item is not null)
            {
                reason = $"{Path.GetFileName(file)} uses {item}";
                return false;
            }

            if (DatabasePublishing().IsMatch(text))
            {
                reason = $"{Path.GetFileName(file)} publishes databases (PublishDatabaseSettings)";
                return false;
            }
        }

        reason = null;
        return true;
    }

    /// <summary>The profile, the project, its <c>*.wpp.targets</c> and any <c>Directory.Build.*</c> above it.</summary>
    private static IEnumerable<string> FilesThatShapeThePublish(PublishJob job)
    {
        yield return job.Profile!.FilePath;
        yield return job.Project.ProjectPath;

        var projectDir = job.Project.ProjectDirectory;
        if (Directory.Exists(projectDir))
        {
            foreach (var wpp in Directory.EnumerateFiles(projectDir, "*.wpp.targets"))
                yield return wpp;
        }

        for (var dir = new DirectoryInfo(projectDir); dir is not null; dir = dir.Parent)
        {
            foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets" })
            {
                var candidate = Path.Combine(dir.FullName, name);
                if (File.Exists(candidate))
                    yield return candidate;
            }
        }
    }

    private static bool IsTrue(PublishProfile profile, string property) =>
        profile.Properties.TryGetValue(property, out var value) && bool.TryParse(value, out var flag) && flag;
}
