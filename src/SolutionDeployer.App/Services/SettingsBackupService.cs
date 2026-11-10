using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using SolutionDeployer.App.ViewModels;
using SolutionDeployer.App.Views;
using SolutionDeployer.Core.Configuration;

namespace SolutionDeployer.App.Services;

public sealed class SettingsBackupService(
    IClassicDesktopStyleApplicationLifetime lifetime,
    SettingsStore settingsStore,
    ICredentialStore credentialStore,
    SettingsAutoExporter autoExporter) : ISettingsBackupService
{
    private static readonly FilePickerFileType ExportFileType = new("Settings export") { Patterns = ["*.json"] };

    public async Task<RestoredSettings?> ShowAsync(AppSettings settings, bool canRestore)
    {
        var owner = lifetime.MainWindow;
        if (owner is null)
            return null;

        var window = new SettingsBackupWindow();
        var vm = new SettingsBackupViewModel(
            settingsStore, settings, credentialStore, autoExporter,
            pickSavePath: start => PickSaveAsync(window, start),
            pickOpenPath: start => PickOpenAsync(window, start),
            canRestore);
        window.DataContext = vm;
        vm.CloseRequested += () => window.Close();

        await window.ShowDialog(owner);
        return vm.Restored is { } restored ? new RestoredSettings(restored, vm.RestoredFrom!) : null;
    }

    private static async Task<string?> PickSaveAsync(TopLevel window, string? start)
    {
        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export settings to",
            SuggestedFileName = Path.GetFileName(start) is { Length: > 0 } name ? name : SettingsTransfer.DefaultFileName,
            SuggestedStartLocation = await FolderOfAsync(window, start),
            DefaultExtension = "json",
            FileTypeChoices = [ExportFileType],
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    private static async Task<string?> PickOpenAsync(TopLevel window, string? start)
    {
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Restore settings from",
            AllowMultiple = false,
            SuggestedStartLocation = await FolderOfAsync(window, start),
            FileTypeFilter = [ExportFileType, FilePickerFileTypes.All],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>The existing folder of <paramref name="path"/> (a file or a folder), if any.</summary>
    private static async Task<IStorageFolder?> FolderOfAsync(TopLevel window, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        return !string.IsNullOrEmpty(folder) && Directory.Exists(folder)
            ? await window.StorageProvider.TryGetFolderFromPathAsync(folder)
            : null;
    }
}
