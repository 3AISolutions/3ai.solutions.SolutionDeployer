using Avalonia.Controls.ApplicationLifetimes;
using SolutionDeployer.App.ViewModels;
using SolutionDeployer.App.Views;
using SolutionDeployer.Core.Models;

namespace SolutionDeployer.App.Services;

public sealed class ScriptEditorService(
    IClassicDesktopStyleApplicationLifetime lifetime,
    IFilePickerService filePicker) : IScriptEditorService
{
    public async Task<ScriptTarget?> EditAsync(ScriptTarget draft, string projectDirectory, bool isNew)
    {
        var owner = lifetime.MainWindow;
        if (owner is null)
            return null;

        var vm = new ScriptEditorViewModel(draft, projectDirectory, filePicker, isNew);
        var window = new ScriptEditorWindow { DataContext = vm };
        vm.CloseRequested += () => window.Close();

        await window.ShowDialog(owner);
        return vm.Result;
    }
}
