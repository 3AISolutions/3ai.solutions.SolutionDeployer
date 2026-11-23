using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
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
/// script target: its destination, its snapshot list (shown in the Snapshots window) and the row's badge.
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

    /// <summary>Compact badge text shown next to the row's buttons (e.g. "📸 3"); it opens the Snapshots window.</summary>
    public string BackupBadge => $"📸 {Backups.Count}";

    /// <summary>The snapshot list is being (re)read from its store.</summary>
    [ObservableProperty]
    private bool _isLoadingBackups;

    /// <summary>Why the last listing failed; null when it succeeded.</summary>
    [ObservableProperty]
    private string? _backupsError;

    private int _loadGeneration;

    /// <summary>Marks a listing as started; pass the result to <see cref="EndLoadingBackups"/>.</summary>
    public int BeginLoadingBackups()
    {
        IsLoadingBackups = true;
        return ++_loadGeneration;
    }

    /// <summary>
    /// Marks a listing as finished. False when a newer one was started since (e.g. the store changed while the
    /// old one was still being read), in which case its result is stale and must be dropped.
    /// </summary>
    public bool EndLoadingBackups(int generation)
    {
        if (generation != _loadGeneration)
            return false;
        IsLoadingBackups = false;
        return true;
    }

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

    /// <summary>The snapshot selected in the Snapshots window.</summary>
    [ObservableProperty]
    private BackupEntryViewModel? _selectedBackup;

    /// <summary>Replaces the snapshot list, keeping the selected snapshot selected when it is still there.</summary>
    public void SetBackups(IEnumerable<BackupEntryViewModel> entries)
    {
        var selectedId = SelectedBackup?.Backup.Id;
        Backups.Clear();
        foreach (var entry in entries)
            Backups.Add(entry);
        SelectedBackup = Backups.FirstOrDefault(b => b.Backup.Id == selectedId) ?? Backups.FirstOrDefault();
        OnPropertyChanged(nameof(HasBackups));
        OnPropertyChanged(nameof(BackupCount));
        OnPropertyChanged(nameof(BackupBadge));
    }
}
