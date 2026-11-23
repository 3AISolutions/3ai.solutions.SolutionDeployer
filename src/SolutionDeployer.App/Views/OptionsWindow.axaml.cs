using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using SolutionDeployer.App.ViewModels;

namespace SolutionDeployer.App.Views;

/// <summary>What to open once the Options window has closed (its dialogs are owned by the main window).</summary>
public enum OptionsFollowUp
{
    None,
    BackupDestinations,
    ManageBackups,
}

/// <summary>
/// The Options dialog. Its <see cref="Avalonia.StyledElement.DataContext"/> is the <see cref="MainWindowViewModel"/>,
/// whose option properties save as they change, so there is nothing to apply on close.
/// Show it with <c>ShowDialog&lt;OptionsFollowUp&gt;</c>.
/// </summary>
public partial class OptionsWindow : Window
{
    public OptionsWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close(OptionsFollowUp.None);

    // The backup windows open over the main window, so this dialog closes first and the caller opens them.
    private void OnBackupDestinationsClick(object? sender, RoutedEventArgs e) => Close(OptionsFollowUp.BackupDestinations);

    private void OnManageBackupsClick(object? sender, RoutedEventArgs e) => Close(OptionsFollowUp.ManageBackups);

    private async void OnCopyClaudeCodeCommandClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && Clipboard is not null)
        {
            await Clipboard.SetTextAsync(vm.ClaudeCodeCommand);
            ShowCopied("Copied the Claude Code command. It contains the access token: share it only with yourself.");
        }
    }

    private async void OnCopyClaudeDesktopConfigClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && Clipboard is not null)
        {
            await Clipboard.SetTextAsync(vm.ClaudeDesktopConfig);
            ShowCopied("Copied the Claude Desktop config: merge it into claude_desktop_config.json and restart Claude.");
        }
    }

    private void ShowCopied(string message)
    {
        CopyFeedback.Text = message;
        CopyFeedback.IsVisible = true;
        if (DataContext is MainWindowViewModel vm)
            vm.StatusMessage = message;
    }
}
