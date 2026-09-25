using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolutionDeployer.App.Services;
using SolutionDeployer.Core.Models;
using SolutionDeployer.Core.Publishing;

namespace SolutionDeployer.App.ViewModels;

public partial class EnvVarRow : ObservableObject
{
    [ObservableProperty]
    private string _key = string.Empty;

    [ObservableProperty]
    private string _value = string.Empty;
}

/// <summary>A choice in the script editor's backup picker.</summary>
public sealed record BackupKindOption(ScriptBackupKind Kind, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Backs the add/edit-script modal. Produces a <see cref="ScriptTarget"/> on save.</summary>
public partial class ScriptEditorViewModel : ObservableObject
{
    private readonly string _projectDirectory;
    private readonly IFilePickerService _filePicker;
    private readonly string _id;

    public ScriptEditorViewModel(ScriptTarget draft, string projectDirectory, IFilePickerService filePicker, bool isNew)
    {
        _projectDirectory = projectDirectory;
        _filePicker = filePicker;
        _id = draft.Id;
        IsNew = isNew;

        _name = draft.Name;
        _scriptPath = draft.ScriptPath;
        _arguments = draft.Arguments ?? string.Empty;
        _workingDirectory = draft.WorkingDirectory ?? string.Empty;
        _requiresCredentials = draft.RequiresCredentials;
        _userNameVariable = draft.UserNameVariable;
        _passwordVariable = draft.PasswordVariable;
        _selectedBackupKind = BackupKinds.First(k => k.Kind == draft.BackupKind);
        _backupServerUrl = draft.BackupServerUrl ?? string.Empty;
        _backupPath = draft.BackupPath ?? string.Empty;
        _preRestoreCommand = draft.PreRestoreCommand ?? string.Empty;
        _postRestoreCommand = draft.PostRestoreCommand ?? string.Empty;
        foreach (var (k, v) in draft.Environment)
            EnvVars.Add(new EnvVarRow { Key = k, Value = v });
    }

    public bool IsNew { get; }

    public string Title => IsNew ? "Add script" : "Edit script";

    public ObservableCollection<EnvVarRow> EnvVars { get; } = [];

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _scriptPath;

    [ObservableProperty]
    private string _arguments;

    [ObservableProperty]
    private string _workingDirectory;

    [ObservableProperty]
    private bool _requiresCredentials;

    [ObservableProperty]
    private string _userNameVariable;

    [ObservableProperty]
    private string _passwordVariable;

    public static IReadOnlyList<BackupKindOption> BackupKinds { get; } =
    [
        new(ScriptBackupKind.None, "No backup"),
        new(ScriptBackupKind.WebDeploy, "Server folder (Web Deploy)"),
        new(ScriptBackupKind.Folder, "Local / network folder"),
        new(ScriptBackupKind.WhatIf, "Only what the script will change (-WhatIf)"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWebDeployBackup), nameof(HasBackup), nameof(IsWhatIfBackup),
        nameof(NeedsBackupFolder), nameof(UsesServer), nameof(BackupDescription))]
    private BackupKindOption _selectedBackupKind;

    public bool IsWebDeployBackup => SelectedBackupKind.Kind == ScriptBackupKind.WebDeploy;

    public bool IsWhatIfBackup => SelectedBackupKind.Kind == ScriptBackupKind.WhatIf;

    public bool HasBackup => SelectedBackupKind.Kind != ScriptBackupKind.None;

    /// <summary>The folder field applies to a server folder or a local folder; a -WhatIf script reports its own.</summary>
    public bool NeedsBackupFolder => SelectedBackupKind.Kind is ScriptBackupKind.WebDeploy or ScriptBackupKind.Folder;

    /// <summary>Backups that go through Web Deploy: they use the script's credentials and can run restore commands.</summary>
    public bool UsesServer => SelectedBackupKind.Kind is ScriptBackupKind.WebDeploy or ScriptBackupKind.WhatIf;

    public string BackupDescription => SelectedBackupKind.Kind switch
    {
        ScriptBackupKind.WhatIf =>
            "Before the real run the script is run with -WhatIf and reports, per server, the files it would change " +
            "(\"SD-WHATIF-TARGET:\" lines followed by msdeploy's change list). Only those files are saved; restore rolls the deploy back.",
        _ => "With \"Backup before publish\" on, the whole folder is snapshotted before the script runs, and can be restored from the script's row.",
    };

    [ObservableProperty]
    private string _backupServerUrl;

    [ObservableProperty]
    private string _backupPath;

    [ObservableProperty]
    private string _preRestoreCommand;

    [ObservableProperty]
    private string _postRestoreCommand;

    [ObservableProperty]
    private string? _error;

    /// <summary>Set when the user saves; null if cancelled.</summary>
    public ScriptTarget? Result { get; private set; }

    /// <summary>Raised to ask the hosting window to close.</summary>
    public event Action? CloseRequested;

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var path = await _filePicker.PickScriptAsync(_projectDirectory);
        if (string.IsNullOrEmpty(path))
            return;

        ScriptPath = ScriptTarget.MakeStorablePath(path, _projectDirectory);
        if (string.IsNullOrWhiteSpace(Name))
            Name = Path.GetFileNameWithoutExtension(path);
    }

    [RelayCommand]
    private void AddEnvVar() => EnvVars.Add(new EnvVarRow());

    [RelayCommand]
    private void RemoveEnvVar(EnvVarRow? row)
    {
        if (row is not null)
            EnvVars.Remove(row);
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            Error = "Enter a name.";
            return;
        }
        if (string.IsNullOrWhiteSpace(ScriptPath))
        {
            Error = "Choose a script file.";
            return;
        }
        if (IsWhatIfBackup && !ScriptPath.Trim().EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            Error = "-WhatIf backups need a PowerShell (.ps1) script with a -WhatIf switch.";
            return;
        }
        if (IsWebDeployBackup && (string.IsNullOrWhiteSpace(BackupServerUrl) || string.IsNullOrWhiteSpace(BackupPath)))
        {
            Error = "Enter the Web Deploy server URL and the folder to back up.";
            return;
        }
        if (SelectedBackupKind.Kind == ScriptBackupKind.Folder && string.IsNullOrWhiteSpace(BackupPath))
        {
            Error = "Enter the folder to back up.";
            return;
        }
        if (!ScriptInterpreters.IsSupported(ScriptPath))
        {
            Error = $"Unsupported script type. Supported: {string.Join(", ", ScriptInterpreters.SupportedExtensions)}.";
            return;
        }

        Result = new ScriptTarget
        {
            Id = _id,
            Name = Name.Trim(),
            ScriptPath = ScriptPath.Trim(),
            Arguments = string.IsNullOrWhiteSpace(Arguments) ? null : Arguments.Trim(),
            WorkingDirectory = string.IsNullOrWhiteSpace(WorkingDirectory) ? null : WorkingDirectory.Trim(),
            RequiresCredentials = RequiresCredentials,
            UserNameVariable = string.IsNullOrWhiteSpace(UserNameVariable) ? ScriptTarget.DefaultUserNameVariable : UserNameVariable.Trim(),
            PasswordVariable = string.IsNullOrWhiteSpace(PasswordVariable) ? ScriptTarget.DefaultPasswordVariable : PasswordVariable.Trim(),
            BackupKind = SelectedBackupKind.Kind,
            BackupServerUrl = IsWebDeployBackup ? BackupServerUrl.Trim() : null,
            BackupPath = NeedsBackupFolder ? BackupPath.Trim() : null,
            PreRestoreCommand = UsesServer && !string.IsNullOrWhiteSpace(PreRestoreCommand) ? PreRestoreCommand.Trim() : null,
            PostRestoreCommand = UsesServer && !string.IsNullOrWhiteSpace(PostRestoreCommand) ? PostRestoreCommand.Trim() : null,
            Environment = EnvVars
                .Where(e => !string.IsNullOrWhiteSpace(e.Key))
                .ToDictionary(e => e.Key.Trim(), e => e.Value),
        };
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        CloseRequested?.Invoke();
    }
}
