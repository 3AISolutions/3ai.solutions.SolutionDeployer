using SolutionDeployer.Core.Backup;

namespace SolutionDeployer.App.ViewModels;

/// <summary>
/// A single restorable snapshot, one row of a target's Snapshots window. Carries its owning row so a restore
/// can reuse that row's current credentials.
/// </summary>
public sealed class BackupEntryViewModel(BackupHostViewModel parent, DeploymentBackup backup)
{
    public BackupHostViewModel Parent { get; } = parent;

    public DeploymentBackup Backup { get; } = backup;

    public string DisplayName => Backup.DisplayName;

    /// <summary>e.g. "#3": keeps snapshots taken within the same second apart.</summary>
    public string SequenceText => $"#{Backup.Sequence}";

    public string CreatedText => Backup.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public bool IsPartial => Backup.IsPartial;

    public string KindText => Backup.IsPartial ? "Partial" : "Full";

    public string KindTip => Backup.Kind switch
    {
        BackupKind.MsDeployPartial => "Only the files this deploy changed on the Web Deploy server.",
        BackupKind.ScriptPartial => "Only the files this script's deploy changed on each of its servers.",
        BackupKind.MsDeploy => "The whole site, pulled with Web Deploy.",
        _ => "The whole folder.",
    };

    public string SizeText => Backup.SizeText;

    /// <summary>What a partial snapshot holds (e.g. "4 saved, 1 added"); empty for a full one.</summary>
    public string ChangesText => Backup.ChangeText.TrimStart(' ', '·').Trim();

    /// <summary>Where the snapshot was taken from (server/site or folder), when recorded.</summary>
    public string SourceText => Backup.Target ?? string.Empty;
}
