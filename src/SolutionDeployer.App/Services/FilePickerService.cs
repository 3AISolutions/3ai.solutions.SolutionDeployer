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

    public Task<string?> PickScriptAsync(string? startDirectory = null) => PickAsync(
        "Select a deployment script",
        new FilePickerFileType("Script files") { Patterns = ["*.ps1", "*.sh", "*.bash", "*.cmd", "*.bat"] },
        startDirectory);

    private async Task<string?> PickAsync(string title, FilePickerFileType filter, string? startDirectory = null)
    {
        var window = lifetime.MainWindow;
        if (window is null)
            return null;

        IStorageFolder? start = null;
        if (!string.IsNullOrEmpty(startDirectory) && Directory.Exists(startDirectory))
            start = await window.StorageProvider.TryGetFolderFromPathAsync(startDirectory);

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [filter, FilePickerFileTypes.All],
            SuggestedStartLocation = start,
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
