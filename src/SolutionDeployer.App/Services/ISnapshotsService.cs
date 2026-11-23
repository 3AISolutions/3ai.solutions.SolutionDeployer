using SolutionDeployer.App.ViewModels;

namespace SolutionDeployer.App.Services;

/// <summary>Shows a target's modal Snapshots window (its store, snapshots, restore and delete).</summary>
public interface ISnapshotsService
{
    /// <summary>Returns once the window has closed; <see cref="SnapshotsViewModel.RestoreRequested"/> says whether to restore.</summary>
    Task ShowAsync(SnapshotsViewModel snapshots);
}
