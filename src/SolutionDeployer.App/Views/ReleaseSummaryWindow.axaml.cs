using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using SolutionDeployer.App.ViewModels;

namespace SolutionDeployer.App.Views;

public partial class ReleaseSummaryWindow : Window
{
    public ReleaseSummaryWindow()
    {
        InitializeComponent();
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ReleaseSummaryViewModel vm && Clipboard is not null)
            await Clipboard.SetTextAsync(vm.PlainText);
    }
}
