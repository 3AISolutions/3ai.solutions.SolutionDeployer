using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using SolutionDeployer.App.ViewModels;

namespace SolutionDeployer.App.Views;

public partial class MainWindow : Window
{
    private bool _sizeRestored;
    private bool _positionRestored;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Opened += OnOpened;
        Closing += OnClosing;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        vm.Log.CollectionChanged += OnLogChanged;

        // Restore size before the window is shown (avoids a resize flash). Position needs Screens,
        // so it waits for Opened.
        if (!_sizeRestored)
        {
            _sizeRestored = true;
            var (width, height, _, _, _) = vm.SavedWindowPlacement;
            if (width is > 200 and < 20000)
                Width = width.Value;
            if (height is > 200 and < 20000)
                Height = height.Value;
        }
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (_positionRestored || DataContext is not MainWindowViewModel vm)
            return;
        _positionRestored = true;

        var (_, _, x, y, maximized) = vm.SavedWindowPlacement;

        // Only restore the position if it lands on a currently-connected screen (avoid off-screen windows).
        if (x is { } px && y is { } py &&
            Screens.All.Any(s => s.Bounds.Contains(new PixelPoint(px, py))))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(px, py);
        }

        if (maximized)
            WindowState = WindowState.Maximized;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        if (WindowState == WindowState.Maximized)
        {
            // Keep the last normal bounds; just record that it was maximized.
            var p = vm.SavedWindowPlacement;
            vm.SaveWindowPlacement(p.Width ?? Width, p.Height ?? Height, p.X ?? Position.X, p.Y ?? Position.Y, true);
        }
        else
        {
            vm.SaveWindowPlacement(Width, Height, Position.X, Position.Y, false);
        }
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
