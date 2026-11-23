using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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

        // The user closed the window: keep running in the tray if they chose to. Quitting from the tray, or the
        // OS shutting down, closes it for real.
        if (vm.CloseToTray && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            Hide();
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

    private async void OnRecentMenuClick(object? sender, RoutedEventArgs e)
    {
        // Raised by File › Recent's generated items (their DataContext is the path) and bubbled to the submenu.
        if (DataContext is MainWindowViewModel vm &&
            e.Source is MenuItem { DataContext: string path } item && !ReferenceEquals(item, sender))
        {
            e.Handled = true;
            await vm.OpenRecentCommand.ExecuteAsync(path);
        }
    }

    private bool _optionsOpen;

    private async void OnOptionsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || _optionsOpen || !IsVisible)
            return;

        _optionsOpen = true;
        OptionsFollowUp followUp;
        try
        {
            followUp = await new OptionsWindow { DataContext = vm }.ShowDialog<OptionsFollowUp>(this);
        }
        finally
        {
            _optionsOpen = false;
        }

        // The backup windows are owned by this window, so they open once Options has closed.
        switch (followUp)
        {
            case OptionsFollowUp.BackupDestinations:
                await vm.ManageBackupTargetsCommand.ExecuteAsync(null);
                break;
            case OptionsFollowUp.ManageBackups when vm.ManageBackupsCommand.CanExecute(null):
                await vm.ManageBackupsCommand.ExecuteAsync(null);
                break;
        }
    }

    /// <summary>
    /// File › Exit quits for real, like the tray's Quit: a shutdown closes the window with
    /// <see cref="WindowCloseReason.ApplicationShutdown"/>, which <see cref="OnClosing"/> doesn't turn into hiding to the tray.
    /// </summary>
    private void OnExitClick(object? sender, RoutedEventArgs e)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
        else
            Close();
    }

    // ---- Row "⋯" menus (sources and scripts). Items that act through the window's view model
    // dispatch here: the button records which row the menu was opened for, and the items act on that.

    private object? _menuRow;

    private void OnRowMenuClick(object? sender, RoutedEventArgs e) =>
        _menuRow = (sender as Control)?.DataContext;

    private async void OnCopySourcePathClick(object? sender, RoutedEventArgs e)
    {
        if (_menuRow is SourceViewModel source && Clipboard is not null)
            await Clipboard.SetTextAsync(source.Path);
    }

    private async void OnOpenSourceFolderClick(object? sender, RoutedEventArgs e)
    {
        if (_menuRow is SourceViewModel source && Path.GetDirectoryName(source.Path) is { Length: > 0 } dir)
            await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(dir));
    }

    private async void OnRemoveSourceClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && _menuRow is SourceViewModel source)
            await vm.RemoveSourceCommand.ExecuteAsync(source);
    }

    private async void OnEditScriptClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && _menuRow is ScriptTargetViewModel script)
            await vm.EditScriptCommand.ExecuteAsync(script);
    }

    private async void OnRemoveScriptClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && _menuRow is ScriptTargetViewModel script)
            await vm.RemoveScriptCommand.ExecuteAsync(script);
    }

    private async void OnEditTagsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || _menuRow is not BackupHostViewModel row ||
            row is not ITaggedTarget target)
            return;

        var editor = new TagEditorViewModel(row.Name, target.Tags, vm.KnownTags);
        var window = new TagEditorWindow { DataContext = editor };
        editor.CloseRequested += window.Close;
        await window.ShowDialog(this);

        if (editor.Result is { } tags)
            vm.SetTargetTags(target, tags);
    }
}
