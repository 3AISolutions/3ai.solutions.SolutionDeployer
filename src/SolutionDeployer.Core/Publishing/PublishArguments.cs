using SolutionDeployer.Core.Models;

namespace SolutionDeployer.Core.Publishing;

/// <summary>
/// Builds the shared set of MSBuild <c>/p:Key=Value</c> properties for a job, with the password
/// redacted in the display form.
/// </summary>
internal static class PublishArguments
{
    private const string RedactedPassword = "***";

    /// <summary>
    /// Returns the property arguments (e.g. <c>/p:PublishProfile=Prod</c>) plus a copy in which the
    /// password value is redacted for logging.
    /// </summary>
    public static (List<string> Args, List<string> Redacted) BuildProperties(PublishJob job)
    {
        var props = new List<(string Key, string Value, bool Secret)>
        {
            ("PublishProfile", ProfileArgument(job.Profile!), false),
        };

        if (!string.IsNullOrEmpty(job.Credentials.UserName))
            props.Add(("UserName", job.Credentials.UserName, false));

        if (!string.IsNullOrEmpty(job.Credentials.Password))
            props.Add(("Password", job.Credentials.Password, true));

        if (job.AllowUntrustedCertificate)
            props.Add(("AllowUntrustedCertificate", "true", false));

        foreach (var (key, value) in job.AdditionalProperties)
            props.Add((key, value, false));

        var args = new List<string>();
        var redacted = new List<string>();
        foreach (var (key, value, secret) in props)
        {
            args.Add($"/p:{key}={value}");
            redacted.Add($"/p:{key}={(secret ? RedactedPassword : value)}");
        }

        return (args, redacted);
    }

    /// <summary>
    /// The value passed to <c>/p:PublishProfile=</c>. For a .pubxml the profile name is enough;
    /// for an exported .PublishSettings file the full path is used.
    /// </summary>
    public static string ProfileArgument(PublishProfile profile) =>
        profile.Format == PublishProfileFormat.PublishSettings ? profile.FilePath : profile.Name;
}
