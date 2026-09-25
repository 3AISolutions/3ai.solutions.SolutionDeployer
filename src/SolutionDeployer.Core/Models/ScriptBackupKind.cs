namespace SolutionDeployer.Core.Models;

/// <summary>Where a script deployment's target lives, for the pre-run snapshot.</summary>
public enum ScriptBackupKind
{
    /// <summary>No backup for this script.</summary>
    None,

    /// <summary>A folder on a remote server, pulled through Web Deploy (msdeploy.exe).</summary>
    WebDeploy,

    /// <summary>A local or UNC folder, zipped directly.</summary>
    Folder,

    /// <summary>
    /// Only what the script will change: the script is first run with <c>-WhatIf</c> and reports, per
    /// Web Deploy target, the files it would update, delete or add (see <see cref="Backup.ScriptWhatIfReport"/>).
    /// </summary>
    WhatIf,
}
