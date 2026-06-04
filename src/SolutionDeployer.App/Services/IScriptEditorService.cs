using SolutionDeployer.Core.Models;

namespace SolutionDeployer.App.Services;

/// <summary>Shows the modal add/edit-script dialog; returns the edited target, or null if cancelled.</summary>
public interface IScriptEditorService
{
    Task<ScriptTarget?> EditAsync(ScriptTarget draft, string projectDirectory, bool isNew);
}
