using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace SolutionDeployer.App.Services;

/// <summary>Avalonia <see cref="IStorageProvider"/>-backed open-file dialog for solution files.</summary>
public sealed class FilePickerService(IClassicDesktopStyleApplicationLifetime lifetime) : IFilePickerService
{
    public async Task<string?> PickSolutionAsync()
    {
        var window = lifetime.MainWindow;
        if (window is null)
            return null;

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a .NET solution",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Solution files")
                {
                    Patterns = ["*.sln", "*.slnx"],
                },
                FilePickerFileTypes.All,
            ],
        });

        var file = files.Count > 0 ? files[0] : null;
        return file?.TryGetLocalPath();
    }
}
