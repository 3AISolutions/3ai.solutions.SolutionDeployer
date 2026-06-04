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
