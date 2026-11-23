using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SolutionDeployer.App.ViewModels;

/// <summary>
/// One target's Snapshots window: its store, its snapshots, and Restore / Delete / Refresh on the selected one.
/// The list, selection and store live on the row (<see cref="BackupHostViewModel"/>), so they stay in step with its
/// badge. Deleting and listing go through the main window; a restore closes the window and is then run by the main
/// window's restore pipeline (<see cref="RestoreRequested"/>), so the log and status dots behave as for any run.
/// </summary>
public sealed partial class SnapshotsViewModel : ObservableObject
{
    private readonly MainWindowViewModel _owner;

    public SnapshotsViewModel(BackupHostViewModel host, MainWindowViewModel owner)
    {
        Host = host;
        _owner = owner;
        Host.PropertyChanged += OnHostChanged;
        Host.Backups.CollectionChanged += OnBackupsChanged;
        _owner.PropertyChanged += OnOwnerChanged;
    }

    public BackupHostViewModel Host { get; }

    public string Title => $"Snapshots — {Host.Name}";

    /// <summary>Asks (heading, message, confirm label) before deleting; set by the window's service so the
    /// confirmation opens on top of this window. Null means "proceed".</summary>
    public Func<string, string, string, Task<bool>>? Confirm { get; set; }

    /// <summary>The snapshot to restore once the window has closed; null when it closed without a restore.</summary>
    public BackupEntryViewModel? RestoreRequested { get; private set; }

    public event Action? CloseRequested;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand), nameof(DeleteCommand), nameof(RefreshCommand))]
    private bool _isBusy;

    /// <summary>The outcome of the last delete, shown in the footer.</summary>
    [ObservableProperty]
    private string? _status;

    /// <summary>A listing finished and found nothing (false while listing, so the list never flashes "empty").</summary>
    public bool IsEmpty => !Host.IsLoadingBackups && Host.Backups.Count == 0;

    /// <summary>What restoring the selected snapshot does; for a partial one, which newer deploys it also undoes.</summary>
    public string? RestoreNote
    {
        get
        {
            if (Host.SelectedBackup is not { } selected)
                return null;

            var sequence = selected.SequenceText;
            if (!selected.IsPartial)
                return $"Restoring {sequence} replaces the target's content with what it held before that deploy.";

            var newer = NewerPartials(selected);
            return newer.Count == 0
                ? $"Restoring {sequence} rolls back the deploy it was taken before."
                : $"Restoring {sequence} also undoes the newer deploys {string.Join(", ", newer.Select(b => b.SequenceText))}: " +
                  $"a Web Deploy rollback first restores every newer snapshot, returning the target to how it was before {sequence}'s deploy.";
        }
    }

    /// <summary>The selected restore also rolls back newer deploys, so the note is shown as a warning.</summary>
    public bool RestoreUndoesNewer => Host.SelectedBackup is { IsPartial: true } selected && NewerPartials(selected).Count > 0;

    private List<BackupEntryViewModel> NewerPartials(BackupEntryViewModel selected) =>
        Host.Backups.Where(b => b.IsPartial && b.Backup.Sequence > selected.Backup.Sequence)
            .OrderBy(b => b.Backup.Sequence)
            .ToList();

    private bool CanActOnSelection() =>
        Host.SelectedBackup is not null && !Host.IsLoadingBackups && !IsBusy && _owner.CanChangeSnapshots;

    private bool CanRefresh() => !IsBusy && !Host.IsLoadingBackups;

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private void Restore()
    {
        RestoreRequested = Host.SelectedBackup;
        CloseRequested?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private async Task DeleteAsync()
    {
        if (Host.SelectedBackup is not { } entry)
            return;

        IsBusy = true;
        try
        {
            Status = await _owner.DeleteBackupAsync(entry, Confirm ?? ((_, _, _) => Task.FromResult(true)));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public Task RefreshAsync()
    {
        Status = null;
        return _owner.ReloadBackupsAsync(Host);
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    /// <summary>Stops following the row and the main window once the window has closed.</summary>
    public void Detach()
    {
        Host.PropertyChanged -= OnHostChanged;
        Host.Backups.CollectionChanged -= OnBackupsChanged;
        _owner.PropertyChanged -= OnOwnerChanged;
    }

    private void OnHostChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(BackupHostViewModel.SelectedBackup):
                OnSelectionOrListChanged();
                break;
            case nameof(BackupHostViewModel.IsLoadingBackups):
                OnPropertyChanged(nameof(IsEmpty));
                RefreshCommands();
                break;
        }
    }

    private void OnBackupsChanged(object? sender, NotifyCollectionChangedEventArgs e) => OnSelectionOrListChanged();

    private void OnSelectionOrListChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(RestoreNote));
        OnPropertyChanged(nameof(RestoreUndoesNewer));
        RefreshCommands();
    }

    private void OnOwnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A deploy or restore may start while this window is open (e.g. over MCP).
        if (e.PropertyName is nameof(MainWindowViewModel.IsRunning) or nameof(MainWindowViewModel.IsLoading))
            RefreshCommands();
    }

    private void RefreshCommands()
    {
        RestoreCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
    }
}
