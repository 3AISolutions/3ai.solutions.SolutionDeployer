using SolutionDeployer.Core.Models;

namespace SolutionDeployer.Core.Backup;

/// <summary>What a set of snapshots belongs to: a publish profile or a script target.</summary>
public sealed class BackupOwner
{
    private BackupOwner(string name, string projectDirectory, PublishProfile? profile, ScriptTarget? script)
    {
        Name = name;
        ProjectDirectory = projectDirectory;
        Profile = profile;
        Script = script;
    }

    public string Name { get; }

    public string ProjectDirectory { get; }

    public PublishProfile? Profile { get; }

    public ScriptTarget? Script { get; }

    /// <summary>Key under which the owner's backup destination is stored in settings.</summary>
    public string SettingsKey => Profile?.FilePath ?? Script!.CredentialKey;

    public static BackupOwner ForProfile(PublishProfile profile, string projectDirectory) =>
        new(profile.Name, projectDirectory, profile, null);

    public static BackupOwner ForScript(ScriptTarget script, string projectDirectory) =>
        new(script.Name, projectDirectory, null, script);

    /// <summary>The owner of a job's target, or null for a job with neither a profile nor a script.</summary>
    public static BackupOwner? ForJob(PublishJob job) => job switch
    {
        { Profile: { } profile } => ForProfile(profile, job.Project.ProjectDirectory),
        { Script: { } script } => ForScript(script, job.Project.ProjectDirectory),
        _ => null,
    };
}
