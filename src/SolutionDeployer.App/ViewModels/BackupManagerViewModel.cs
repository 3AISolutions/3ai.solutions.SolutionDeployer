using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolutionDeployer.Core.Backup;
using SolutionDeployer.Core.Configuration;

namespace SolutionDeployer.App.ViewModels;

/// <summary>One owner's snapshots in one destination, as a row of the backup manager.</summary>
public sealed partial class BackupGroupViewModel(BackupOwnerGroup group, string destination) : ObservableObject
{
    public BackupOwnerGroup Group { get; } = group;

    [ObservableProperty]
    private bool _isChecked;

    public string OwnerName => Group.OwnerName;

    public string ProjectName => Group.ProjectName;

    public string Destination { get; } = destination;

    /// <summary>The profile file or script id, for the tooltip.</summary>
    public string OwnerTip => Group.OwnerId ?? Group.OwnerKey;

    public bool NeedsAttention => Group.Status != BackupOwnerStatus.Active;

    public string StatusText => Group.Status switch
    {
        BackupOwnerStatus.Active => "Active",
        BackupOwnerStatus.OldDestination => "Old destination",
        BackupOwnerStatus.Orphaned => "Orphaned",
        _ => "Unknown",
    };

    public string StatusTip => Group.Status switch
    {
        BackupOwnerStatus.Active => "The profile or script exists and backs up here.",
        BackupOwnerStatus.OldDestination =>
            "The profile or script now backs up to a different destination, so these snapshots are no longer listed or pruned under it.",
        BackupOwnerStatus.Orphaned => "The publish profile file or script no longer exists.",
        _ => "Taken by an older version that did not record its owner, and not matched to any profile the app knows of. " +
             "Load its solution to identify it.",
    };

    public string CountText => Group.Snapshots.Count == 1 ? "1 snapshot" : $"{Group.Snapshots.Count} snapshots";

    public string SizeText => DeploymentBackup.FormatSize(Group.TotalBytes);

    public string RangeText => Group.Snapshots.Count == 1
        ? $"{Group.Newest.CreatedUtc.ToLocalTime():yyyy-MM-dd}"
        : $"{Group.Oldest.CreatedUtc.ToLocalTime():yyyy-MM-dd} → {Group.Newest.CreatedUtc.ToLocalTime():yyyy-MM-dd}";
}

/// <summary>
/// Lists every snapshot across all backup destinations by owner, edits the retention policy, and deletes
/// what is no longer needed — always after a confirmation that says exactly what will go.
/// </summary>
public partial class BackupManagerViewModel : ObservableObject
{
    private const int DefaultMaxAgeDays = 90;

    private readonly IBackupCleanupService _cleanup;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly IReadOnlyCollection<BackupOwner> _knownOwners;
    private readonly Func<string, string, string, Task<bool>> _confirm;
    private BackupInventory? _inventory;
    private bool _loadingPolicy;

    /// <param name="confirm">Asks (heading, message, confirm label) before anything is deleted; true to proceed.</param>
    public BackupManagerViewModel(
        IBackupCleanupService cleanup,
        SettingsStore settingsStore,
        AppSettings settings,
        IReadOnlyCollection<BackupOwner> knownOwners,
        Func<string, string, string, Task<bool>> confirm)
    {
        _cleanup = cleanup;
        _settingsStore = settingsStore;
        _settings = settings;
        _knownOwners = knownOwners;
        _confirm = confirm;

        _loadingPolicy = true;
        KeepLast = Math.Max(1, settings.BackupRetention);
        MaxAgeEnabled = settings.BackupMaxAgeDays is > 0;
        MaxAgeDays = settings.BackupMaxAgeDays is int days && days > 0 ? days : DefaultMaxAgeDays;
        _loadingPolicy = false;
    }

    public ObservableCollection<BackupGroupViewModel> Groups { get; } = [];

    /// <summary>Whether any snapshot was deleted, so the caller can refresh its per-target lists.</summary>
    public bool DeletedAny { get; private set; }

    public event Action? CloseRequested;

    // NumericUpDown binds decimal?.
    [ObservableProperty]
    private decimal? _keepLast;

    [ObservableProperty]
    private bool _maxAgeEnabled;

    [ObservableProperty]
    private decimal? _maxAgeDays;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(CleanUpByPolicyCommand), nameof(DeleteCheckedCommand))]
    private bool _isBusy;

    /// <summary>A scan finished and found nothing (false while scanning, so the list never flashes "empty").</summary>
    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private string? _summary;

    [ObservableProperty]
    private string? _status;

    /// <summary>Destinations that could not be listed; null when all were.</summary>
    [ObservableProperty]
    private string? _errors;

    public BackupRetentionPolicy Policy => new((int)(KeepLast ?? 1), MaxAgeEnabled ? (int)(MaxAgeDays ?? DefaultMaxAgeDays) : null);

    partial void OnKeepLastChanged(decimal? value) => SavePolicy();

    partial void OnMaxAgeEnabledChanged(bool value) => SavePolicy();

    partial void OnMaxAgeDaysChanged(decimal? value) => SavePolicy();

    private void SavePolicy()
    {
        if (_loadingPolicy)
            return;

        var policy = Policy;
        _settings.BackupRetention = Math.Max(1, policy.KeepLast);
        _settings.BackupMaxAgeDays = policy.MaxAgeDays is > 0 ? policy.MaxAgeDays : null;
        _settingsStore.Save(_settings);
    }

    private bool CanAct => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAct))]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        Status = "Scanning backup destinations…";
        try
        {
            // Listing local disk is synchronous file IO; keep it off the UI thread.
            _inventory = await Task.Run(() => _cleanup.GetInventoryAsync(_knownOwners));

            Groups.Clear();
            foreach (var group in _inventory.Groups)
                Groups.Add(new BackupGroupViewModel(group, DestinationName(group)));
            IsEmpty = Groups.Count == 0;

            var snapshots = _inventory.Groups.Sum(g => g.Snapshots.Count);
            var bytes = _inventory.Groups.Sum(g => g.TotalBytes);
            var unneeded = _inventory.Groups.Count(g => g.Status != BackupOwnerStatus.Active);
            Summary = $"{snapshots} snapshot(s) · {DeploymentBackup.FormatSize(bytes)} across {_inventory.Groups.Count} target(s)" +
                      (unneeded > 0 ? $" · {unneeded} no longer in use" : string.Empty);
            Errors = _inventory.Errors.Count == 0
                ? null
                : string.Join(Environment.NewLine, _inventory.Errors.Select(e => $"Could not list {e.StoreDescription}: {e.Message}"));
            Status = null;
        }
        catch (Exception ex)
        {
            Status = $"Could not scan backups: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string DestinationName(BackupOwnerGroup group) =>
        group.StoreTargetId == S3BackupTarget.LocalId
            ? "Local disk"
            : _settings.RemoteBackupTargets.FirstOrDefault(t => t.Id == group.StoreTargetId)?.ToString() ?? group.StoreDescription;

    /// <summary>Checks every row whose owner is gone, unknown or now backs up elsewhere.</summary>
    [RelayCommand]
    private void SelectUnneeded()
    {
        foreach (var group in Groups)
            group.IsChecked = group.NeedsAttention;
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task CleanUpByPolicyAsync()
    {
        if (_inventory is null)
            return;

        var policy = Policy;
        var plan = BackupCleanupPlan.ForRetention(_inventory, policy, DateTimeOffset.UtcNow);
        if (plan.IsEmpty)
        {
            Status = "Nothing to clean up: every target is within the retention policy.";
            return;
        }

        var rule = $"keep the last {policy.KeepLast} per target" +
                   (policy.MaxAgeDays is { } days ? $" and none older than {days} days" : string.Empty);
        var message =
            $"Delete {plan.Deletions.Count} snapshot(s) ({DeploymentBackup.FormatSize(plan.BytesReclaimed)}) to {rule}?" +
            Environment.NewLine + Environment.NewLine +
            Describe(plan) + Environment.NewLine + Environment.NewLine +
            "The newest snapshot of every target is always kept.";

        await ApplyAsync(plan, "Clean up backups", message);
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task DeleteCheckedAsync()
    {
        var chosen = Groups.Where(g => g.IsChecked).Select(g => g.Group).ToList();
        if (chosen.Count == 0)
        {
            Status = "Tick the targets whose snapshots should be deleted.";
            return;
        }

        var plan = BackupCleanupPlan.ForGroups(chosen);
        var active = chosen.Count(g => g.Status == BackupOwnerStatus.Active);
        var message =
            $"Delete all {plan.Deletions.Count} snapshot(s) ({DeploymentBackup.FormatSize(plan.BytesReclaimed)}) of " +
            $"{chosen.Count} target(s)?" + Environment.NewLine + Environment.NewLine +
            Describe(plan) +
            (active > 0
                ? Environment.NewLine + Environment.NewLine +
                  $"{active} of them still in use: their deployments can no longer be rolled back."
                : string.Empty);

        await ApplyAsync(plan, "Delete snapshots", message);
    }

    private async Task ApplyAsync(BackupCleanupPlan plan, string heading, string message)
    {
        if (!await _confirm(heading, message, "Delete"))
            return;

        IsBusy = true;
        Status = $"Deleting {plan.Deletions.Count} snapshot(s)…";
        BackupCleanupResult result;
        try
        {
            result = await Task.Run(() => _cleanup.ApplyAsync(plan));
        }
        catch (Exception ex)
        {
            // Some snapshots may already be gone.
            DeletedAny = true;
            Status = $"Cleanup failed: {ex.Message}";
            return;
        }
        finally
        {
            IsBusy = false;
        }

        DeletedAny |= result.Deleted > 0;
        await RefreshAsync();
        Status = result.Failed == 0
            ? $"Deleted {result.Deleted} snapshot(s), freed {DeploymentBackup.FormatSize(result.BytesReclaimed)}."
            : $"Deleted {result.Deleted} snapshot(s); {result.Failed} could not be deleted: {result.Errors.FirstOrDefault()}";
    }

    /// <summary>A few lines naming what goes, per target.</summary>
    private static string Describe(BackupCleanupPlan plan)
    {
        const int maxLines = 6;
        var lines = plan.Deletions
            .GroupBy(d => (d.Backup.StorageTargetId, d.Backup.ProfileKey))
            .Select(g => $"• {g.First().Backup.ProjectName} / {g.First().Backup.ProfileName}: {g.Count()} ({g.First().Reason})")
            .ToList();

        return lines.Count <= maxLines
            ? string.Join(Environment.NewLine, lines)
            : string.Join(Environment.NewLine, lines.Take(maxLines - 1)) + Environment.NewLine + $"• … and {lines.Count - maxLines + 1} more";
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();
}
