using System.Xml.Linq;
using SolutionDeployer.Core.Models;

namespace SolutionDeployer.Core.Profiles;

/// <summary>
/// Scans <c>Properties/PublishProfiles</c> (and the project root) for <c>.pubxml</c> and
/// <c>.PublishSettings</c> files and parses their metadata. Passwords are never read.
/// </summary>
public sealed class ProfileDiscovery : IProfileDiscovery
{
    public IReadOnlyList<PublishProfile> DiscoverProfiles(string projectFilePath)
    {
        var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectFilePath));
        if (projectDir is null || !Directory.Exists(projectDir))
            return [];

        var searchDirs = new[]
        {
            Path.Combine(projectDir, "Properties", "PublishProfiles"),
            Path.Combine(projectDir, "PublishProfiles"),
        };

        var profiles = new List<PublishProfile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in searchDirs)
        {
            if (!Directory.Exists(dir))
                continue;

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var ext = Path.GetExtension(file);

                // *.pubxml.user holds encrypted secrets — skip it.
                if (file.EndsWith(".pubxml.user", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (ext.Equals(".pubxml", StringComparison.OrdinalIgnoreCase))
                {
                    if (seen.Add(file))
                        profiles.Add(ParsePubXml(file));
                }
                else if (ext.Equals(".PublishSettings", StringComparison.OrdinalIgnoreCase) ||
                         ext.Equals(".publishsettings", StringComparison.OrdinalIgnoreCase))
                {
                    if (seen.Add(file))
                        profiles.AddRange(ParsePublishSettings(file));
                }
            }
        }

        return profiles
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static PublishProfile ParsePubXml(string file)
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var doc = XDocument.Load(file);
            // Ignore namespaces so both legacy (MSBuild xmlns) and modern pubxml parse.
            foreach (var pg in doc.Descendants().Where(e => e.Name.LocalName == "PropertyGroup"))
            {
                foreach (var prop in pg.Elements())
                {
                    var key = prop.Name.LocalName;
                    if (!props.ContainsKey(key))
                        props[key] = prop.Value.Trim();
                }
            }
        }
        catch
        {
            // Malformed profile — still surface it by name so the user can see/fix it.
        }

        var method = props.GetValueOrDefault("WebPublishMethod");
        return new PublishProfile
        {
            Name = Path.GetFileNameWithoutExtension(file),
            FilePath = file,
            Format = PublishProfileFormat.PubXml,
            WebPublishMethod = method,
            ServerUrl = props.GetValueOrDefault("MSDeployServiceURL") ?? props.GetValueOrDefault("SiteUrlToLaunchAfterPublish"),
            SiteName = props.GetValueOrDefault("DeployIisAppPath"),
            Configuration = props.GetValueOrDefault("LastUsedBuildConfiguration"),
            UserName = props.GetValueOrDefault("UserName"),
            RequiresCredentials = string.Equals(method, "MSDeploy", StringComparison.OrdinalIgnoreCase),
            Properties = props,
        };
    }

    private static IEnumerable<PublishProfile> ParsePublishSettings(string file)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(file);
        }
        catch
        {
            yield break;
        }

        foreach (var pp in doc.Descendants().Where(e => e.Name.LocalName == "publishProfile"))
        {
            var props = pp.Attributes()
                .ToDictionary(a => a.Name.LocalName, a => a.Value, StringComparer.OrdinalIgnoreCase);

            var method = props.GetValueOrDefault("publishMethod");
            var profileName = props.GetValueOrDefault("profileName")
                              ?? Path.GetFileNameWithoutExtension(file);

            yield return new PublishProfile
            {
                Name = profileName,
                FilePath = file,
                Format = PublishProfileFormat.PublishSettings,
                WebPublishMethod = method,
                ServerUrl = props.GetValueOrDefault("publishUrl") ?? props.GetValueOrDefault("destinationAppUrl"),
                SiteName = props.GetValueOrDefault("msdeploySite"),
                UserName = props.GetValueOrDefault("userName"),
                RequiresCredentials = !string.Equals(method, "FileSystem", StringComparison.OrdinalIgnoreCase),
                Properties = props,
            };
        }
    }
}
