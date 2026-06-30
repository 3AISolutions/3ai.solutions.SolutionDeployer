using SolutionDeployer.App.Services;
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
