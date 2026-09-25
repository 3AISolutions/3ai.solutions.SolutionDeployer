using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolutionDeployer.Core.Backup;
using SolutionDeployer.Core.Models;

namespace SolutionDeployer.App.ViewModels;

/// <summary>A selectable backup destination (local disk or a named remote) for the per-target picker.</summary>
public sealed record BackupDestinationOption(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The snapshot state shared by every row that can be backed up and restored — a publish profile or a
/// script target: its destination picker, snapshot list and the collapsible restore row.
/// </summary>
public abstract partial class BackupHostViewModel : ObservableObject
{
    public abstract string Name { get; }

    /// <summary>What the snapshots belong to, for the backup service.</summary>
    public abstract BackupOwner BackupOwner { get; }

    /// <summary>The row's current credentials, reused for a restore.</summary>
    public abstract PublishCredentials BuildCredentials();

    /// <summary>Whether this target's deployment can be snapshotted/restored.</summary>
    [ObservableProperty]
    private bool _supportsBackup;

    /// <summary>Previously-captured snapshots for this target, newest first.</summary>
    public ObservableCollection<BackupEntryViewModel> Backups { get; } = [];

    public bool HasBackups => Backups.Count > 0;

    public int BackupCount => Backups.Count;

    /// <summary>Compact badge text shown next to the row's buttons (e.g. "📸 3").</summary>
    public string BackupBadge => $"📸 {Backups.Count}";

    /// <summary>The snapshot restore row is collapsed by default; the badge toggles it per target.</summary>
    [ObservableProperty]
    private bool _showBackups;

    /// <summary>Available backup destinations (local + named remotes) for this target's picker.</summary>
    public ObservableCollection<BackupDestinationOption> BackupDestinations { get; } = [];

    private bool _applyingDestination;

    [ObservableProperty]
    private BackupDestinationOption? _selectedDestination;

    /// <summary>Raised when the user changes the destination (not when it's set programmatically).</summary>
    public event Action<BackupHostViewModel>? BackupDestinationChanged;

    partial void OnSelectedDestinationChanged(BackupDestinationOption? value)
    {
        if (!_applyingDestination)
            BackupDestinationChanged?.Invoke(this);
    }

    public void SetDestinations(IEnumerable<BackupDestinationOption> options, string selectedId)
    {
        _applyingDestination = true;
        BackupDestinations.Clear();
        foreach (var option in options)
            BackupDestinations.Add(option);
        SelectedDestination = BackupDestinations.FirstOrDefault(d => d.Id == selectedId)
                              ?? BackupDestinations.FirstOrDefault();
        _applyingDestination = false;
    }

    /// <summary>The snapshot chosen in the restore picker.</summary>
    [ObservableProperty]
    private BackupEntryViewModel? _selectedBackup;

    [RelayCommand]
    private void ToggleBackups() => ShowBackups = !ShowBackups;

    public void SetBackups(IEnumerable<BackupEntryViewModel> entries)
    {
        Backups.Clear();
        foreach (var entry in entries)
            Backups.Add(entry);
        SelectedBackup = Backups.FirstOrDefault();
        OnPropertyChanged(nameof(HasBackups));
        OnPropertyChanged(nameof(BackupCount));
        OnPropertyChanged(nameof(BackupBadge));
    }
}
