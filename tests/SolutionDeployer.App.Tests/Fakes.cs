using SolutionDeployer.App.Services;
using SolutionDeployer.Core.Git;
using SolutionDeployer.Core.Models;

namespace SolutionDeployer.App.Tests;

/// <summary>File picker stub — the tests add sources by explicit path, not via dialogs.</summary>
public sealed class FakeFilePicker : IFilePickerService
{
    public Task<string?> PickSolutionAsync() => Task.FromResult<string?>(null);
    public Task<string?> PickProjectAsync() => Task.FromResult<string?>(null);
    public Task<string?> PickScriptAsync(string? startDirectory = null) => Task.FromResult<string?>(null);
}

/// <summary>Script editor stub that returns a preset target (or null to simulate cancel).</summary>
public sealed class FakeScriptEditor : IScriptEditorService
{
    public ScriptTarget? Next { get; set; }

    public Task<ScriptTarget?> EditAsync(ScriptTarget draft, string projectDirectory, bool isNew)
    {
        // Mirror the real editor: keep the draft's id so edits update in place.
        if (Next is not null)
            Next.Id = draft.Id;
        return Task.FromResult(Next);
    }
}

/// <summary>Deploy-confirmation stub; confirms by default so deploy tests aren't blocked.</summary>
public sealed class FakeDeployConfirmation : IDeployConfirmationService
{
    public DeployConfirmation Next { get; set; } = new(Confirmed: true, DontAskAgain: false);

    public Task<DeployConfirmation> ConfirmAsync(IReadOnlyList<string> targets, bool runInParallel) =>
        Task.FromResult(Next);
}

/// <summary>Inert git-history stub: reports unavailable so post-deploy recording is skipped.</summary>
public sealed class FakeGitHistory : IGitHistoryService
{
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<ReleaseSummary> BuildSummaryAsync(
        string deployedProjectPath, string deployedProjectName,
        IReadOnlyDictionary<string, string> previousShas, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ReleaseSummary { DeployedProjectName = deployedProjectName, Projects = [] });

    public Task<IReadOnlyDictionary<string, string>> CaptureShasAsync(
        string deployedProjectPath, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
}

/// <summary>Release-summary stub; never opens a window.</summary>
public sealed class FakeReleaseSummary : IReleaseSummaryService
{
    public Task ShowAsync(ReleaseSummary summary) => Task.CompletedTask;
}

/// <summary>Remote-targets manager stub; never opens a window.</summary>
public sealed class FakeRemoteTargets : IRemoteTargetsService
{
    public Task ShowAsync(SolutionDeployer.Core.Configuration.AppSettings settings) => Task.CompletedTask;
}

/// <summary>Update-prompt stub; declines by default so tests never trigger an update.</summary>
public sealed class FakeUpdatePrompt : IUpdatePromptService
{
    public bool Next { get; set; }

    public Task<bool> ConfirmUpdateAsync(string? version, string? notes) => Task.FromResult(Next);
}
