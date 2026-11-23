using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolutionDeployer.Core.Configuration;

namespace SolutionDeployer.App.ViewModels;

/// <summary>Editable form state for one credential library entry.</summary>
public partial class CredentialEntryEditViewModel : ObservableObject
{
    public CredentialEntryEditViewModel(SavedCredential entry, string? password, IReadOnlyList<string> usedBy)
    {
        Id = entry.Id;
        _name = entry.Name;
        _userName = entry.UserName;
        _notes = entry.Notes ?? string.Empty;
        _password = password ?? string.Empty;
        UsedBy = usedBy;
    }

    public string Id { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string _userName;

    [ObservableProperty]
    private string _password;

    [ObservableProperty]
    private string _notes;

    /// <summary>The targets that use this entry (display names).</summary>
    public IReadOnlyList<string> UsedBy { get; }

    public string UsageText => UsedBy.Count switch
    {
        0 => "Not used by any target yet. Pick it in a target's Credentials dialog.",
        1 => $"Used by {UsedBy[0]}.",
        _ => $"Used by {UsedBy.Count} targets: {string.Join(", ", UsedBy)}.",
    };

    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? (string.IsNullOrWhiteSpace(UserName) ? "(new credential)" : UserName)
        : Name;

    public SavedCredential ToEntry() => new()
    {
        Id = Id,
        Name = Name.Trim(),
        UserName = UserName.Trim(),
        Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim(),
    };
}

/// <summary>
/// The credential library manager: add, rename, edit and delete shared credentials. Changes apply on Save; deleting
/// an entry that targets use asks first, and those targets go back to their own credentials.
/// </summary>
public partial class CredentialLibraryViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly ICredentialStore _credentialStore;
    private readonly HashSet<string> _deletedIds = [];

    public CredentialLibraryViewModel(
        AppSettings settings, SettingsStore settingsStore, ICredentialStore credentialStore,
        Func<string, string> describeTarget)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _credentialStore = credentialStore;
        CredentialStoreAvailable = credentialStore.IsAvailable;

        foreach (var entry in settings.Credentials.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var password = credentialStore.IsAvailable ? credentialStore.Get(entry.PasswordKey) : null;
            var usedBy = settings.TargetsUsing(entry.Id).Select(describeTarget)
                .Order(StringComparer.CurrentCultureIgnoreCase).ToList();
            Entries.Add(new CredentialEntryEditViewModel(entry, password, usedBy));
        }

        _selected = Entries.FirstOrDefault();
    }

    public ObservableCollection<CredentialEntryEditViewModel> Entries { get; } = [];

    public bool CredentialStoreAvailable { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private CredentialEntryEditViewModel? _selected;

    public bool HasSelection => Selected is not null;

    /// <summary>Set while a delete of an entry in use waits for confirmation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmingDelete))]
    private string? _deleteWarning;

    public bool IsConfirmingDelete => DeleteWarning is not null;

    [ObservableProperty]
    private string? _error;

    /// <summary>True once the user saved changes.</summary>
    public bool Changed { get; private set; }

    public event Action? CloseRequested;

    partial void OnSelectedChanged(CredentialEntryEditViewModel? value) => DeleteWarning = null;

    [RelayCommand]
    private void Add()
    {
        var vm = new CredentialEntryEditViewModel(new SavedCredential { Name = "New credential" }, null, []);
        Entries.Add(vm);
        Selected = vm;
    }

    private bool CanDelete() => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        if (Selected is null)
            return;

        if (Selected.UsedBy.Count == 0)
        {
            RemoveSelected();
            return;
        }

        DeleteWarning = $"'{Selected.DisplayName}' is used by {string.Join(", ", Selected.UsedBy)}. " +
                        "Deleting it switches them back to their own username and password (which may not be set).";
    }

    [RelayCommand]
    private void ConfirmDelete() => RemoveSelected();

    [RelayCommand]
    private void KeepEntry() => DeleteWarning = null;

    private void RemoveSelected()
    {
        if (Selected is null)
            return;

        _deletedIds.Add(Selected.Id);
        var index = Entries.IndexOf(Selected);
        Entries.Remove(Selected);
        Selected = Entries.Count == 0 ? null : Entries[Math.Min(index, Entries.Count - 1)];
        DeleteWarning = null;
    }

    [RelayCommand]
    private void Save()
    {
        if (Entries.FirstOrDefault(e => string.IsNullOrWhiteSpace(e.Name)) is { } unnamed)
        {
            Selected = unnamed;
            Error = "Give every credential a name.";
            return;
        }

        if (Entries.GroupBy(e => e.Name.Trim(), StringComparer.CurrentCultureIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } dup)
        {
            Selected = dup.First();
            Error = $"Two credentials are named '{dup.Key}'. Give each a different name.";
            return;
        }

        foreach (var id in _deletedIds)
            TargetCredentials.DeleteLibraryEntry(_settings, _credentialStore, id);

        foreach (var vm in Entries)
            TargetCredentials.SaveLibraryEntry(_settings, _credentialStore, vm.ToEntry(), vm.Password);

        _settingsStore.Save(_settings);
        Changed = true;
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();
}
