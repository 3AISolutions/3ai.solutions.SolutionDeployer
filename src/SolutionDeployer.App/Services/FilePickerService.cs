using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace SolutionDeployer.App.Services;

/// <summary>Avalonia <see cref="IStorageProvider"/>-backed open-file dialogs for solutions and projects.</summary>
public sealed class FilePickerService(IClassicDesktopStyleApplicationLifetime lifetime) : IFilePickerService
{
    public Task<string?> PickSolutionAsync() => PickAsync(
        "Select a .NET solution",
        new FilePickerFileType("Solution files") { Patterns = ["*.sln", "*.slnx"] });

    public Task<string?> PickProjectAsync() => PickAsync(
        "Select a project",
        new FilePickerFileType("Project files") { Patterns = ["*.csproj", "*.fsproj", "*.vbproj"] });

    private async Task<string?> PickAsync(string title, FilePickerFileType filter)
    {
        var window = lifetime.MainWindow;
        if (window is null)
            return null;

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [filter, FilePickerFileTypes.All],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
