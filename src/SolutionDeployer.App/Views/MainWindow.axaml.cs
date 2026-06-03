using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using SolutionDeployer.App.ViewModels;

namespace SolutionDeployer.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.Log.CollectionChanged += OnLogChanged;
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add)
            return;

        // Auto-scroll the output to the newest line.
        Dispatcher.UIThread.Post(() => this.FindControl<ScrollViewer>("LogScroller")?.ScrollToEnd(),
            DispatcherPriority.Background);
    }

    private async void OnRecentSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm &&
            sender is ComboBox { SelectedItem: string path } combo)
        {
            combo.SelectedItem = null;
            await vm.OpenRecentCommand.ExecuteAsync(path);
        }
    }
}
