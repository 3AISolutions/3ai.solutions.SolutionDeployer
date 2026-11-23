using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolutionDeployer.App.Services;
using SolutionDeployer.Core.Backup;
using SolutionDeployer.Core.Configuration;
using SolutionDeployer.Core.Git;
using SolutionDeployer.Core.Models;
using SolutionDeployer.Core.Publishing;
using SolutionDeployer.Core.Solutions;

namespace SolutionDeployer.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly ISourceLoader _sourceLoader;
    private readonly DeploymentRunner _deploymentRunner;
    private readonly IPublishEngineFactory _engineFactory;
    private readonly SettingsStore _settingsStore;
    private readonly IFilePickerService _filePicker;
    private readonly UpdateService _updateService;
    private readonly ICredentialStore _credentialStore;
    private readonly IScriptEditorService _scriptEditor;
    private readonly IBackupService _backupService;
    private readonly IDeployConfirmationService _confirmationService;
    private readonly IGitHistoryService _gitHistory;
    private readonly IReleaseSummaryService _releaseSummary;
    private readonly IRemoteTargetsService _remoteTargets;
    private readonly IUpdatePromptService _updatePrompt;
    private readonly IWhatsNewService _whatsNew;
    private readonly IDeploySummaryService _deploySummary;
    private readonly IBackupManagerService _backupManager;
    private readonly IAutoStartService _autoStart;
    private readonly IMcpServerService _mcpServer;
    private readonly ISettingsBackupService _settingsBackup;
    private readonly ISnapshotsService _snapshots;
    private readonly ICredentialsDialogService? _credentialsDialog;
    private AppSettings _settings;

    private CancellationTokenSource? _runCts;
    private bool _applyingState;
    private const int MaxLogLines = 8000;

    public MainWindowViewModel(
        ISourceLoader sourceLoader,
        DeploymentRunner deploymentRunner,
        IPublishEngineFactory engineFactory,
        SettingsStore settingsStore,
        IFilePickerService filePicker,
        UpdateService updateService,
        ICredentialStore credentialStore,
        IScriptEditorService scriptEditor,
        IBackupService backupService,
        IDeployConfirmationService confirmationService,
        IGitHistoryService gitHistory,
        IReleaseSummaryService releaseSummary,
        IRemoteTargetsService remoteTargets,
        IUpdatePromptService updatePrompt,
        IWhatsNewService whatsNew,
        IDeploySummaryService deploySummary,
        IBackupManagerService backupManager,
        IAutoStartService autoStart,
        IMcpServerService mcpServer,
        ISettingsBackupService settingsBackup,
        ISnapshotsService snapshots,
        ICredentialsDialogService? credentialsDialog = null)
    {
        _sourceLoader = sourceLoader;
        _deploymentRunner = deploymentRunner;
        _engineFactory = engineFactory;
        _settingsStore = settingsStore;
        _filePicker = filePicker;
        _updateService = updateService;
        _credentialStore = credentialStore;
        _scriptEditor = scriptEditor;
        _backupService = backupService;
        _confirmationService = confirmationService;
        _gitHistory = gitHistory;
        _releaseSummary = releaseSummary;
        _remoteTargets = remoteTargets;
        _updatePrompt = updatePrompt;
        _whatsNew = whatsNew;
        _deploySummary = deploySummary;
        _backupManager = backupManager;
        _autoStart = autoStart;
        _mcpServer = mcpServer;
        _settingsBackup = settingsBackup;
        _snapshots = snapshots;
        _credentialsDialog = credentialsDialog;
        _settings = settingsStore.Load();
        _settings.MigrateLegacy();

        _runInParallel = _settings.RunInParallel;
        _showPaths = _settings.ShowPaths;
        _backupBeforePublish = _settings.BackupBeforePublish;
        _deployBackupPreview = _settings.DeployBackupPreview;
        _confirmBeforeDeploy = _settings.ConfirmBeforeDeploy;
        _updateRepository = _settings.UpdateRepository;
        _restoreSourcesOnStartup = _settings.RestoreSourcesOnStartup;
        _closeToTray = _settings.CloseToTray;
        _startWithOperatingSystem = _settings.StartWithOperatingSystem && autoStart.IsSupported;
        _mcpServerEnabled = _settings.McpServerEnabled;
        _mcpStatus = mcpServer.StatusText;
        mcpServer.StatusChanged += () => Dispatcher.UIThread.Post(() => McpStatus = _mcpServer.StatusText);
        Sources.CollectionChanged += (_, _) => OnTargetTagsChanged();
        SyncRecent();
    }

    public ObservableCollection<SourceViewModel> Sources { get; } = [];

    public ObservableCollection<LogLine> Log { get; } = [];

    public ObservableCollection<string> RecentSolutions { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeployCommand))]
    [NotifyCanExecuteChangedFor(nameof(ManageBackupsCommand))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeployCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(ManageBackupsCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusMessage = "Add a solution or project to begin.";

    [ObservableProperty]
    private bool _runInParallel;

    [ObservableProperty]
    private bool _backupBeforePublish;

    [ObservableProperty]
    private bool _deployBackupPreview;

    [ObservableProperty]
    private bool _confirmBeforeDeploy;

    [ObservableProperty]
    private bool _restoreSourcesOnStartup;

    /// <summary>Show source paths and target addresses in the list; remembered in settings.</summary>
    [ObservableProperty]
    private bool _showPaths;

    partial void OnShowPathsChanged(bool value)
    {
        _settings.ShowPaths = value;
        _settingsStore.Save(_settings);
    }

    [RelayCommand]
    private void TogglePaths() => ShowPaths = !ShowPaths;

    /// <summary>Free-text filter applied to the sources/projects/profiles/scripts tree.</summary>
    [ObservableProperty]
    private string _filterText = string.Empty;

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    [ObservableProperty]
    private string _updateRepository = string.Empty;

    [ObservableProperty]
    private string? _updateStatus;

    [ObservableProperty]
    private int _updateProgress;

    partial void OnRunInParallelChanged(bool value)
    {
        _settings.RunInParallel = value;
        _settingsStore.Save(_settings);
    }

    partial void OnBackupBeforePublishChanged(bool value)
    {
        _settings.BackupBeforePublish = value;
        _settingsStore.Save(_settings);
    }

    partial void OnDeployBackupPreviewChanged(bool value)
    {
        _settings.DeployBackupPreview = value;
        _settingsStore.Save(_settings);
    }

    partial void OnConfirmBeforeDeployChanged(bool value)
    {
        _settings.ConfirmBeforeDeploy = value;
        _settingsStore.Save(_settings);
    }

    partial void OnRestoreSourcesOnStartupChanged(bool value)
    {
        _settings.RestoreSourcesOnStartup = value;
        _settingsStore.Save(_settings);
    }

    // ---- Background running: tray, sign-in start, MCP --------------------

    /// <summary>Closing the window hides it to the tray instead of quitting.</summary>
    [ObservableProperty]
    private bool _closeToTray;

    /// <summary>Start the app (in the tray) at sign-in.</summary>
    [ObservableProperty]
    private bool _startWithOperatingSystem;

    /// <summary>Serve the deploy tools to Claude over MCP.</summary>
    [ObservableProperty]
    private bool _mcpServerEnabled;

    [ObservableProperty]
    private string _mcpStatus;

    public bool AutoStartSupported => _autoStart.IsSupported;

    /// <summary>Text for the "Start with …" option.</summary>
    public string AutoStartLabel =>
        OperatingSystem.IsWindows() ? "Start with Windows"
        : OperatingSystem.IsMacOS() ? "Start at login"
        : "Start when I sign in";

    public string ClaudeCodeCommand => _mcpServer.ClaudeCodeCommand;

    public string ClaudeDesktopConfig => _mcpServer.ClaudeDesktopConfig;

    partial void OnCloseToTrayChanged(bool value)
    {
        _settings.CloseToTray = value;
        _settingsStore.Save(_settings);
    }

    private bool _revertingAutoStart;

    partial void OnStartWithOperatingSystemChanged(bool value)
    {
        if (_revertingAutoStart)
            return;

        try
        {
            _autoStart.SetEnabled(value);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not {(value ? "add" : "remove")} the sign-in entry: {ex.Message}";
            Dispatcher.UIThread.Post(() => // put the checkbox back
            {
                _revertingAutoStart = true;
                StartWithOperatingSystem = !value;
                _revertingAutoStart = false;
            });
            return;
        }

        _settings.StartWithOperatingSystem = value;
        _settingsStore.Save(_settings);
    }

    partial void OnMcpServerEnabledChanged(bool value)
    {
        _settings.McpServerEnabled = value;
        _settingsStore.Save(_settings);
        _ = _mcpServer.ApplyAsync(value, _settings.McpServerPort);
    }

    [RelayCommand]
    private void RegenerateMcpToken()
    {
        _mcpServer.RegenerateToken();
        StatusMessage = "Created a new MCP access token. Reconnect Claude with the new command or config.";
    }

    /// <summary>
    /// Brings the background options in line with the settings once sources are loaded: refreshes the sign-in
    /// entry (the executable may have moved) and starts the MCP server when enabled.
    /// </summary>
    public async Task RunStartupBackgroundServicesAsync()
    {
        if (StartWithOperatingSystem)
        {
            try
            {
                _autoStart.SetEnabled(true);
            }
            catch
            {
                // The entry from last time still works unless the app moved; not worth interrupting startup.
            }
        }

        if (McpServerEnabled)
            await _mcpServer.ApplyAsync(true, _settings.McpServerPort);
    }

    /// <summary>Window placement persisted from the previous session, for the view to restore.</summary>
    public (double? Width, double? Height, int? X, int? Y, bool Maximized) SavedWindowPlacement =>
        (_settings.WindowWidth, _settings.WindowHeight, _settings.WindowX, _settings.WindowY, _settings.WindowMaximized);

    /// <summary>Persists the current window placement (called by the view as it closes).</summary>
    public void SaveWindowPlacement(double width, double height, int x, int y, bool maximized)
    {
        _settings.WindowWidth = width;
        _settings.WindowHeight = height;
        _settings.WindowX = x;
        _settings.WindowY = y;
        _settings.WindowMaximized = maximized;
        _settingsStore.Save(_settings);
    }

    /// <summary>Current app version, shown in the toolbar (e.g. "v0.5.0").</summary>
    public string AppVersion
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? string.Empty : $"v{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>Semver string of the running build (Major.Minor.Build), or null when unresolved.</summary>
    private static string? CurrentVersionString =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : null;

    public int SelectedCount =>
        Sources.SelectMany(s => s.Projects)
            .Sum(p => p.Profiles.Count(pr => pr.IsSelected) + p.ScriptTargets.Count(st => st.IsSelected));

    public bool HasSources => Sources.Count > 0;

    // ---- Adding / removing sources ---------------------------------------

    [RelayCommand]
    private async Task AddSolutionAsync()
    {
        var path = await _filePicker.PickSolutionAsync();
        if (!string.IsNullOrEmpty(path))
            await AddSourceAsync(DeploymentSource.Solution(path));
    }

    [RelayCommand]
    private async Task AddProjectAsync()
    {
        var path = await _filePicker.PickProjectAsync();
        if (!string.IsNullOrEmpty(path))
            await AddSourceAsync(DeploymentSource.Project(path));
    }

    [RelayCommand]
    private async Task OpenRecentAsync(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        var ext = Path.GetExtension(path);
        var kind = ext.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            ? SourceKind.Solution
            : SourceKind.Project;
        await AddSourceAsync(new DeploymentSource { Kind = kind, Path = Path.GetFullPath(path) });
    }

    private async Task AddSourceAsync(DeploymentSource source)
    {
        source.Path = Path.GetFullPath(source.Path);

        if (!_settings.AddSource(source))
        {
            StatusMessage = $"{source.Name} is already in the list.";
            return;
        }

        _settings.AddRecentSolution(source.Path);
        _settingsStore.Save(_settings);
        SyncRecent();

        IsLoading = true;
        try
        {
            var vm = await BuildSourceAsync(source);
            Sources.Add(vm);
            StatusMessage = SummariseSource(vm);
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(HasSources));
            if (FilterText.Length > 0)
                ApplyFilter();
        }
    }

    [RelayCommand]
    private async Task RemoveSourceAsync(SourceViewModel? source)
    {
        if (source is null)
            return;

        var kind = source.Kind == SourceKind.Solution ? "solution" : "project";
        var confirmed = await _confirmationService.ConfirmActionAsync(
            $"Remove {source.Name}?",
            $"The {kind} is removed from the list and its profile selections are forgotten. " +
            "Nothing on disk is deleted, and you can add it again later.",
            "Remove");
        if (!confirmed)
            return;

        Sources.Remove(source);
        _settings.RemoveSource(source.Source);
        _settingsStore.Save(_settings);

        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSources));
        StatusMessage = $"Removed {source.Name}.";
    }

    // ---- Script targets ---------------------------------------------------

    [RelayCommand]
    private async Task AddScriptAsync(ProjectViewModel? project)
    {
        if (project is null)
            return;

        var draft = new ScriptTarget();
        var result = await _scriptEditor.EditAsync(draft, project.ProjectDirectory, isNew: true);
        if (result is null)
            return;

        project.ScriptTargets.Add(CreateScriptViewModel(project, result));
        project.NotifyScriptsChanged();
        PersistScriptTargets(project);
        OnPropertyChanged(nameof(SelectedCount));
        StatusMessage = $"Added script '{result.Name}' to {project.Name}.";
    }

    [RelayCommand]
    private async Task EditScriptAsync(ScriptTargetViewModel? script)
    {
        if (script is null)
            return;

        var result = await _scriptEditor.EditAsync(script.Target, script.Parent.ProjectDirectory, isNew: false);
        if (result is null)
            return;

        script.Update(result);
        PersistScriptTargets(script.Parent);
        ConfigureBackup(script); // the backup target may have been added, changed or removed
        StatusMessage = $"Updated script '{result.Name}'.";
    }

    /// <summary>Builds a script row with its remembered credentials and backup state.</summary>
    private ScriptTargetViewModel CreateScriptViewModel(ProjectViewModel project, ScriptTarget script)
    {
        var scriptVm = new ScriptTargetViewModel(project, script, credentialStoreAvailable: _credentialStore.IsAvailable);
        LoadCredentials(scriptVm);
        scriptVm.BackupDestinationChanged += OnBackupDestinationChanged;
        scriptVm.Tags.Replace(_settings.GetTargetTags(scriptVm.TagKey));
        ConfigureBackup(scriptVm);
        return scriptVm;
    }

    [RelayCommand]
    private async Task RemoveScriptAsync(ScriptTargetViewModel? script)
    {
        if (script is null)
            return;

        var confirmed = await _confirmationService.ConfirmActionAsync(
            $"Remove script {script.Name}?",
            "The script deployment is removed from this project along with its saved password. " +
            "The script file itself is not deleted.",
            "Remove");
        if (!confirmed)
            return;

        var project = script.Parent;
        project.ScriptTargets.Remove(script);
        project.NotifyScriptsChanged();
        TargetCredentials.Forget(_settings, _credentialStore, script.Target.CredentialKey);
        _settings.TargetTags.Remove(script.TagKey);
        PersistScriptTargets(project);
        OnPropertyChanged(nameof(SelectedCount));
        SaveCurrentSelection();
        OnTargetTagsChanged();
        StatusMessage = $"Removed script '{script.Name}'.";
    }

    private void PersistScriptTargets(ProjectViewModel project)
    {
        _settings.SetScriptTargets(project.ProjectPath, project.ScriptTargets.Select(s => s.Target));
        _settingsStore.Save(_settings);
    }

    [RelayCommand]
    private async Task RefreshAllAsync()
    {
        if (Sources.Count == 0)
            return;

        IsLoading = true;
        var current = _settings.Sources.ToList();
        Sources.Clear();
        try
        {
            foreach (var source in current)
                Sources.Add(await BuildSourceAsync(source));
            StatusMessage = $"Refreshed {Sources.Count} source(s).";
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(HasSources));
            if (FilterText.Length > 0)
                ApplyFilter();
        }
    }

    /// <summary>Loads a source from disk and builds its view-model tree, restoring saved selections.</summary>
    private async Task<SourceViewModel> BuildSourceAsync(DeploymentSource source)
    {
        var vm = new SourceViewModel(source);
        var loaded = await _sourceLoader.LoadAsync(source);

        if (loaded.IsMissing)
            vm.Problem = "File not found on disk.";
        else if (loaded.Error is not null)
            vm.Problem = loaded.Error;

        _applyingState = true;
        try
        {
            var credentialsAvailable = _credentialStore.IsAvailable;
            foreach (var project in loaded.Projects)
            {
                var projectVm = new ProjectViewModel(project);
                projectVm.SelectionChanged += OnProjectSelectionChanged;
                foreach (var profile in project.Profiles)
                {
                    var profileVm = new ProfileViewModel(
                        projectVm, profile, _settings.DefaultEngine, null, null, credentialsAvailable);
                    LoadCredentials(profileVm);
                    profileVm.BackupDestinationChanged += OnBackupDestinationChanged;
                    profileVm.Tags.Replace(_settings.GetTargetTags(profileVm.TagKey));
                    ConfigureBackup(profileVm);
                    projectVm.Profiles.Add(profileVm);
                }

                foreach (var script in _settings.GetScriptTargets(project.ProjectPath))
                    projectVm.ScriptTargets.Add(CreateScriptViewModel(projectVm, script));

                vm.Projects.Add(projectVm);
            }

            RestoreSelection(vm);
        }
        finally
        {
            _applyingState = false;
        }

        return vm;
    }

    private static string SummariseSource(SourceViewModel vm)
    {
        if (vm.HasProblem)
            return $"{vm.Name}: {vm.Problem}";

        var profileCount = vm.Projects.Sum(p => p.Profiles.Count);
        var summary = $"{vm.Name}: {vm.Projects.Count} project(s), {profileCount} profile(s).";
        if (vm.Projects.Count == 0)
            summary += vm.Kind == SourceKind.Solution
                ? " No projects with publish profiles found."
                : " No publish profiles found (looked in Properties/PublishProfiles).";
        return summary;
    }

    private void SyncRecent()
    {
        RecentSolutions.Clear();
        foreach (var recent in _settings.RecentSolutions)
            RecentSolutions.Add(recent);
    }

    // ---- Selection --------------------------------------------------------

    private void OnProjectSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        SaveCurrentSelection();
    }

    private void RestoreSelection(SourceViewModel source)
    {
        if (!_settings.SavedSelections.TryGetValue(source.Path, out var saved) || saved.Count == 0)
            return;

        foreach (var sel in saved)
        {
            var project = source.Projects.FirstOrDefault(p => p.Name == sel.Project);
            if (project is null)
                continue;

            if (sel.Kind == SelectionKind.Script)
            {
                var script = project.ScriptTargets.FirstOrDefault(s => s.Target.Id == sel.ScriptId);
                if (script is not null)
                    script.IsSelected = true;
            }
            else
            {
                var profile = project.Profiles.FirstOrDefault(pr => pr.Name == sel.Profile);
                if (profile is not null)
                {
                    profile.Engine = sel.Engine;
                    profile.IsSelected = true;
                }
            }
        }
    }

    private void SaveCurrentSelection()
    {
        if (_applyingState)
            return;

        foreach (var source in Sources)
        {
            var selection = new List<SavedProfileSelection>();

            foreach (var project in source.Projects)
            {
                selection.AddRange(project.Profiles
                    .Where(pr => pr.IsSelected)
                    .Select(pr => new SavedProfileSelection
                    {
                        Kind = SelectionKind.Profile,
                        Project = project.Name,
                        Profile = pr.Name,
                        Engine = pr.Engine,
                    }));

                selection.AddRange(project.ScriptTargets
                    .Where(st => st.IsSelected)
                    .Select(st => new SavedProfileSelection
                    {
                        Kind = SelectionKind.Script,
                        Project = project.Name,
                        ScriptId = st.Target.Id,
                    }));
            }

            if (selection.Count == 0)
                _settings.SavedSelections.Remove(source.Path);
            else
                _settings.SavedSelections[source.Path] = selection;
        }

        _settingsStore.Save(_settings);
    }

    /// <summary>Ticks every target the filters leave visible (hidden ones keep their state).</summary>
    [RelayCommand]
    private void SelectAll() => SetAllSelected(true, visibleOnly: true);

    [RelayCommand]
    private void ClearSelection() => SetAllSelected(false, visibleOnly: false);

    private void SetAllSelected(bool value, bool visibleOnly)
    {
        _applyingState = true;
        foreach (var project in Sources.SelectMany(s => s.Projects))
        {
            foreach (var profile in project.Profiles.Where(p => !visibleOnly || p.IsVisible))
                profile.IsSelected = value;
            foreach (var script in project.ScriptTargets.Where(s => !visibleOnly || s.IsVisible))
                script.IsSelected = value;
        }
        _applyingState = false;

        OnPropertyChanged(nameof(SelectedCount));
        SaveCurrentSelection();
    }

    // ---- Filtering --------------------------------------------------------

    /// <summary>
    /// Applies <see cref="FilterText"/> and the tag filter across the tree: a target stays visible if its text
    /// (or a relevant ancestor's) matches AND it has every ticked tag. Matching parents expand so results are visible.
    /// </summary>
    private void ApplyFilter()
    {
        var filter = FilterText?.Trim() ?? string.Empty;
        var noText = filter.Length == 0;
        var tags = SelectedFilterTags;
        var noTags = tags.Count == 0;
        var noFilter = noText && noTags;
        bool Match(string? text) => text is not null && text.Contains(filter, StringComparison.OrdinalIgnoreCase);
        bool HasTags(ITaggedTarget target) => tags.All(target.Tags.ContainsTag);

        foreach (var source in Sources)
        {
            var sourceMatch = Match(source.Name) || Match(source.Path);
            var anyProjectVisible = false;

            foreach (var project in source.Projects)
            {
                var projectMatch = sourceMatch || Match(project.Name);
                var anyChildVisible = false;

                foreach (var profile in project.Profiles)
                {
                    profile.IsVisible = (noText || projectMatch ||
                        Match(profile.Name) || Match(profile.Target) || Match(profile.Method)) && HasTags(profile);
                    anyChildVisible |= profile.IsVisible;
                }

                foreach (var script in project.ScriptTargets)
                {
                    script.IsVisible = (noText || projectMatch || Match(script.Name) || Match(script.ScriptPath)) &&
                                       HasTags(script);
                    anyChildVisible |= script.IsVisible;
                }

                // Under a tag filter a project only shows for its matching targets.
                project.IsVisible = noFilter || (projectMatch && noTags) || anyChildVisible;
                if (!noFilter && project.IsVisible)
                    project.IsExpanded = true;
                anyProjectVisible |= project.IsVisible;
            }

            source.IsVisible = noFilter || (sourceMatch && noTags) || anyProjectVisible;
            if (!noFilter && source.IsVisible)
                source.IsExpanded = true;
        }
    }

    // ---- Tags -------------------------------------------------------------

    private readonly HashSet<string> _filterTags = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The list's tag filter: every tag on a loaded target (plus any still ticked), sorted.</summary>
    public ObservableCollection<TagFilterOption> TagFilterOptions { get; } = [];

    public bool HasTagFilterOptions => TagFilterOptions.Count > 0;

    public bool IsTagFilterActive => _filterTags.Count > 0;

    /// <summary>The tag filter button's caption, e.g. "Tags" or "Tags: prod, eu".</summary>
    public string TagFilterLabel => _filterTags.Count == 0
        ? "Tags"
        : "Tags: " + string.Join(", ", TagFilterOptions.Where(o => o.IsSelected).Select(o => o.Tag));

    /// <summary>Every tag in use on any target, for the tag editor's suggestions.</summary>
    public IReadOnlyList<string> KnownTags => _settings.AllTargetTags();

    private IReadOnlyList<string> SelectedFilterTags => _filterTags.ToList();

    private IEnumerable<ITaggedTarget> TaggedTargets => Sources.SelectMany(s => s.Projects)
        .SelectMany(p => p.Profiles.Cast<ITaggedTarget>().Concat(p.ScriptTargets));

    /// <summary>Replaces a target's tags (normalised), saves them and re-applies the filter.</summary>
    public void SetTargetTags(ITaggedTarget target, IEnumerable<string> tags)
    {
        _settings.SetTargetTags(target.TagKey, tags);
        _settingsStore.Save(_settings);

        // A project added both on its own and through a solution has two rows for the same target.
        var saved = _settings.GetTargetTags(target.TagKey);
        foreach (var row in TaggedTargets.Where(t => t.TagKey == target.TagKey).Append(target).Distinct())
            row.Tags.Replace(saved);
        OnTargetTagsChanged();
    }

    [RelayCommand]
    private void ClearTagFilter()
    {
        _filterTags.Clear();
        foreach (var option in TagFilterOptions)
            option.IsSelected = false;
        OnTagFilterChanged();
    }

    /// <summary>Rebuilds the tag filter's options after rows or their tags changed, keeping what is ticked.</summary>
    private void OnTargetTagsChanged()
    {
        var tags = TaggedTargets.SelectMany(t => t.Tags).Concat(_filterTags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!tags.SequenceEqual(TagFilterOptions.Select(o => o.Tag)))
        {
            foreach (var option in TagFilterOptions)
                option.PropertyChanged -= OnTagFilterOptionChanged;
            TagFilterOptions.Clear();
            foreach (var tag in tags)
            {
                var option = new TagFilterOption(tag, _filterTags.Contains(tag));
                option.PropertyChanged += OnTagFilterOptionChanged;
                TagFilterOptions.Add(option);
            }
            OnPropertyChanged(nameof(HasTagFilterOptions));
            OnPropertyChanged(nameof(TagFilterLabel));
        }

        if (FilterText.Length > 0 || IsTagFilterActive)
            ApplyFilter();
    }

    private void OnTagFilterOptionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not TagFilterOption option || e.PropertyName != nameof(TagFilterOption.IsSelected))
            return;

        var changed = option.IsSelected ? _filterTags.Add(option.Tag) : _filterTags.Remove(option.Tag);
        if (changed)
            OnTagFilterChanged();
    }

    private void OnTagFilterChanged()
    {
        OnPropertyChanged(nameof(IsTagFilterActive));
        OnPropertyChanged(nameof(TagFilterLabel));
        ApplyFilter();
    }

    // ---- Deployment -------------------------------------------------------

    private bool CanDeploy() => !IsRunning && !IsLoading;

    [RelayCommand(CanExecute = nameof(CanDeploy))]
    private async Task DeployAsync()
    {
        var projects = Sources.SelectMany(s => s.Projects).ToList();
        var selectedProfiles = projects.SelectMany(p => p.Profiles).Where(pr => pr.IsSelected).ToList();
        var selectedScripts = projects.SelectMany(p => p.ScriptTargets).Where(st => st.IsSelected).ToList();

        if (selectedProfiles.Count + selectedScripts.Count == 0)
        {
            StatusMessage = "Select at least one target to deploy.";
            return;
        }

        if (FindUnavailableEngine(selectedProfiles, selectedScripts) is { } engineError)
        {
            StatusMessage = engineError;
            Log.Add(LogLine.System($"ERROR: {StatusMessage}"));
            return;
        }

        if (ConfirmBeforeDeploy)
        {
            var targets = selectedProfiles
                .Select(p => $"{p.Parent.Name} → {p.Name}  [{p.Engine}]{(string.IsNullOrEmpty(p.Target) ? "" : $"  {p.Target}")}")
                .Concat(selectedScripts.Select(s => $"{s.Parent.Name} → {s.Name}  (script)"))
                .ToList();

            var confirmation = await _confirmationService.ConfirmAsync(targets, RunInParallel);
            if (!confirmation.Confirmed)
            {
                StatusMessage = "Deployment cancelled.";
                return;
            }

            if (confirmation.DontAskAgain)
                ConfirmBeforeDeploy = false; // persisted via OnConfirmBeforeDeployChanged
        }

        // Credentials are saved from each row's Credentials dialog; ask for any password a target still lacks.
        if (!await PromptForMissingPasswordsAsync(selectedProfiles.Cast<ICredentialTarget>().Concat(selectedScripts)))
        {
            StatusMessage = "Deployment cancelled: a target has no password.";
            return;
        }

        var summary = await RunDeploymentAsync(selectedProfiles, selectedScripts, RunInParallel, BackupBeforePublish, observer: null);
        if (summary is not null)
            await _deploySummary.ShowAsync(summary);
    }

    private string? FindUnavailableEngine(IEnumerable<ProfileViewModel> profiles, IReadOnlyCollection<ScriptTargetViewModel> scripts)
    {
        var engineKinds = profiles.Select(p => p.Engine);
        if (scripts.Count > 0)
            engineKinds = engineKinds.Append(PublishEngineKind.Script);
        foreach (var engineKind in engineKinds.Distinct())
        {
            if (!_engineFactory.Get(engineKind).IsAvailable(out var reason))
                return $"{engineKind} engine unavailable: {reason}";
        }

        return null;
    }

    /// <summary>
    /// Deploys the given rows with the credentials they hold, updating their status dots and the output as it
    /// goes. Sets <see cref="IsRunning"/> before its first await, so a caller can fire and forget it. Returns the
    /// summary to show, or null when the run was cancelled or aborted.
    /// </summary>
    private async Task<DeploySummaryViewModel?> RunDeploymentAsync(
        IReadOnlyList<ProfileViewModel> profiles,
        IReadOnlyList<ScriptTargetViewModel> scripts,
        bool runInParallel,
        bool backupBeforePublish,
        IRunObserver? observer)
    {
        // Status dots show this run only: clear every row, then mark what's queued.
        var queued = profiles.Cast<BackupHostViewModel>().Concat(scripts).ToHashSet();
        foreach (var target in Sources.SelectMany(s => s.Projects)
                     .SelectMany(p => p.Profiles.Cast<ISelectableTarget>().Concat(p.ScriptTargets)))
        {
            target.Status = PublishStatus.Pending;
            target.IsQueued = queued.Contains((BackupHostViewModel)target);
            target.ResultText = string.Empty;
        }

        var jobsByTarget = new Dictionary<string, BackupHostViewModel>();
        var jobs = new List<PublishJob>();

        foreach (var profileVm in profiles)
        {
            var job = new PublishJob
            {
                Project = profileVm.Parent.Project,
                Profile = profileVm.Profile,
                Engine = profileVm.Engine,
                Configuration = string.IsNullOrWhiteSpace(profileVm.Profile.Configuration)
                    ? "Release"
                    : profileVm.Profile.Configuration!,
                Credentials = profileVm.BuildCredentials(),
            };
            jobs.Add(job);
            jobsByTarget[job.Id] = profileVm;
        }

        foreach (var scriptVm in scripts)
        {
            var job = new PublishJob
            {
                Project = scriptVm.Parent.Project,
                Script = scriptVm.Target,
                Engine = PublishEngineKind.Script,
                Configuration = "Release",
                Credentials = scriptVm.BuildCredentials(),
            };
            jobs.Add(job);
            jobsByTarget[job.Id] = scriptVm;
        }

        IsRunning = true;
        _runCts = new CancellationTokenSource();
        var startedJobs = new HashSet<string>();
        DeploySummaryViewModel? summary = null;
        var outcome = PublishStatus.Failed;
        var heading = $"── Deploying {jobs.Count} target(s) ({(runInParallel ? "parallel" : "sequential")}) ──";
        Log.Add(LogLine.System(heading));
        observer?.Output(heading);
        StatusMessage = $"Deploying {jobs.Count} target(s)…";

        void OnOutput(JobOutput output)
        {
            observer?.Output($"[{output.JobDisplayName}] {output.Line.Text}");
            Dispatcher.UIThread.Post(() =>
            {
                if (startedJobs.Add(output.JobId) && jobsByTarget.TryGetValue(output.JobId, out var vmStart))
                {
                    ((ISelectableTarget)vmStart).Status = PublishStatus.Running;
                    observer?.TargetChanged(vmStart, PublishStatus.Running, null);
                }

                AppendLog(LogLine.From(output.JobDisplayName, output.Line));
            });
        }

        void OnJobCompleted(PublishResult result) => Dispatcher.UIThread.Post(() =>
        {
            if (jobsByTarget.TryGetValue(result.JobId, out var vm))
            {
                var row = (ISelectableTarget)vm;
                row.Status = result.Status;
                row.ResultText = result.IsSuccess
                    ? $"OK ({StepTimings.Format(result.Duration)})"
                    : result.ErrorMessage ?? result.Status.ToString();
                observer?.TargetChanged(vm, result.Status, result.IsSuccess ? result.TimingText : row.ResultText);
            }
        });

        try
        {
            var options = new DeploymentRunOptions
            {
                RunInParallel = runInParallel,
                MaxParallelism = 4,
                BackupBeforePublish = backupBeforePublish,
                DeployBackupPreview = DeployBackupPreview,
            };
            var runStarted = Stopwatch.GetTimestamp();
            var results = await _deploymentRunner.RunAsync(jobs, options, OnOutput, OnJobCompleted, _runCts.Token);
            var runTime = StepTimings.Format(Stopwatch.GetElapsedTime(runStarted));
            summary = DeploySummaryViewModel.From(jobs, results);

            var ok = results.Count(r => r.IsSuccess);
            var failed = results.Count - ok;
            outcome = failed == 0 ? PublishStatus.Succeeded : PublishStatus.Failed;
            StatusMessage = failed == 0
                ? $"All {ok} target(s) succeeded in {runTime}."
                : $"{ok} succeeded, {failed} failed ({runTime}).";
            Log.Add(LogLine.System($"── {StatusMessage} ──"));

            // A backup taken during the run produces a new snapshot — refresh the restore lists.
            if (backupBeforePublish)
                foreach (var host in queued)
                    _ = LoadBackupsAsync(host);

            // Record/log the git history for profiles that deployed successfully (informative only).
            var succeeded = results
                .Where(r => r.IsSuccess && jobsByTarget.TryGetValue(r.JobId, out var t) && t is ProfileViewModel)
                .Select(r => (ProfileViewModel)jobsByTarget[r.JobId])
                .ToList();
            await RecordReleaseHistoryAsync(succeeded);
        }
        catch (OperationCanceledException)
        {
            outcome = PublishStatus.Cancelled;
            StatusMessage = "Deployment cancelled.";
            Log.Add(LogLine.System("── Cancelled ──"));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Deployment error: {ex.Message}";
            Log.Add(LogLine.System($"ERROR: {ex.Message}"));
        }
        finally
        {
            IsRunning = false;
            _runCts?.Dispose();
            _runCts = null;

            // Anything that never reported a result (cancelled or aborted run) shouldn't stay "waiting".
            // Real results are posted to the dispatcher and land after this, so they still win.
            foreach (var target in jobsByTarget.Values.Cast<ISelectableTarget>()
                         .Where(t => t.Status is PublishStatus.Pending or PublishStatus.Running))
                target.Status = PublishStatus.Cancelled;
        }

        // Posted so it lands after the per-target results already queued on the dispatcher.
        if (observer is not null)
        {
            var message = StatusMessage;
            Dispatcher.UIThread.Post(() => observer.Completed(outcome, message));
        }

        return summary;
    }

    private bool CanCancel() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _runCts?.Cancel();
        StatusMessage = "Cancelling…";
    }

    // ---- Backup / restore -------------------------------------------------

    /// <summary>
    /// Works out whether a profile/script row can be backed up and, if so, fills its destination picker
    /// and snapshot list. Re-run after a script's backup settings change.
    /// </summary>
    private void ConfigureBackup(BackupHostViewModel host)
    {
        host.SupportsBackup = _backupService.CanBackUp(host.BackupOwner, out _);
        if (!host.SupportsBackup)
        {
            host.SetBackups([]);
            return;
        }

        host.SetDestinations(BuildDestinationOptions(), _settings.GetBackupTargetId(host.BackupOwner.SettingsKey));
        _ = LoadBackupsAsync(host); // fire-and-forget so a remote listing never blocks load
    }

    /// <summary>
    /// Repopulates a row's snapshot list from its configured destination. Best-effort: a remote (S3)
    /// listing may fail or be slow, so callers fire-and-forget and errors surface in the Snapshots window.
    /// </summary>
    private async Task LoadBackupsAsync(BackupHostViewModel host)
    {
        if (!host.SupportsBackup)
            return;

        var generation = host.BeginLoadingBackups();
        try
        {
            var backups = await _backupService.ListAsync(host.BackupOwner);
            if (!host.EndLoadingBackups(generation))
                return; // the store changed while listing; the newer listing fills the list
            host.BackupsError = null;
            host.SetBackups(backups.Select(b => new BackupEntryViewModel(host, b)));
        }
        catch (Exception ex)
        {
            if (!host.EndLoadingBackups(generation))
                return;
            host.BackupsError = $"Could not list snapshots: {ex.Message}";
            host.SetBackups([]);
            StatusMessage = $"Could not list snapshots for {host.Name}: {ex.Message}";
        }
    }

    /// <summary>Re-reads a row's snapshot list (the Snapshots window's Refresh).</summary>
    public Task ReloadBackupsAsync(BackupHostViewModel host) => LoadBackupsAsync(host);

    /// <summary>Snapshots can be restored or deleted: no deploy or restore is running and sources are loaded.</summary>
    public bool CanChangeSnapshots => CanDeploy();

    /// <summary>
    /// Opens a row's Snapshots window. Restoring from it closes the window, then runs here like any restore, so the
    /// log and the row's status dot follow it.
    /// </summary>
    [RelayCommand]
    private async Task ShowSnapshotsAsync(BackupHostViewModel? host)
    {
        if (host is null || !host.SupportsBackup)
            return;

        var snapshots = new SnapshotsViewModel(host, this);
        try
        {
            await _snapshots.ShowAsync(snapshots);
        }
        finally
        {
            snapshots.Detach();
        }

        if (snapshots.RestoreRequested is not { } entry)
            return;

        // Something (e.g. an MCP deploy) may have started while the window was open.
        if (!CanDeploy())
        {
            StatusMessage = "Not restored: a deploy or restore started meanwhile. Try again once it has finished.";
            return;
        }

        await RunRestoreAsync(entry.Parent, entry.Backup, observer: null);
    }

    private List<BackupDestinationOption> BuildDestinationOptions()
    {
        var options = new List<BackupDestinationOption> { new(S3BackupTarget.LocalId, "Local disk") };
        options.AddRange(_settings.RemoteBackupTargets.Select(t => new BackupDestinationOption(t.Id, t.ToString())));
        return options;
    }

    private void OnBackupDestinationChanged(BackupHostViewModel host)
    {
        var id = host.SelectedDestination?.Id ?? S3BackupTarget.LocalId;
        _settings.SetBackupTargetId(host.BackupOwner.SettingsKey, id);
        _settingsStore.Save(_settings);
        StatusMessage = $"{host.Name} backups → {host.SelectedDestination?.Name ?? "Local disk"}.";
        _ = LoadBackupsAsync(host);
    }

    private IEnumerable<BackupHostViewModel> AllBackupHosts() =>
        Sources.SelectMany(s => s.Projects)
            .SelectMany(p => p.Profiles.Cast<BackupHostViewModel>().Concat(p.ScriptTargets));

    /// <summary>Re-populates every row's destination picker after the remote list changes.</summary>
    private void RefreshAllBackupDestinations()
    {
        var options = BuildDestinationOptions();
        foreach (var host in AllBackupHosts().Where(h => h.SupportsBackup))
            host.SetDestinations(options, _settings.GetBackupTargetId(host.BackupOwner.SettingsKey));
    }

    [RelayCommand]
    private async Task ManageBackupTargetsAsync()
    {
        await _remoteTargets.ShowAsync(_settings);
        // The user may have added/removed remotes — refresh the per-row pickers.
        RefreshAllBackupDestinations();
    }

    [RelayCommand(CanExecute = nameof(CanDeploy))]
    private async Task ManageBackupsAsync()
    {
        var hosts = AllBackupHosts().ToList();
        if (!await _backupManager.ShowAsync(_settings, hosts.Select(h => h.BackupOwner).ToList()))
            return;

        foreach (var host in hosts.Where(h => h.SupportsBackup))
            _ = LoadBackupsAsync(host);
    }

    // ---- Settings export / restore ----------------------------------------

    /// <summary>File (beside settings.json) that holds the settings replaced by the latest restore.</summary>
    public const string BeforeRestoreFileName = "settings.before-restore.json";

    [RelayCommand]
    private async Task ExportRestoreSettingsAsync()
    {
        var restored = await _settingsBackup.ShowAsync(_settings, canRestore: !IsRunning);
        if (restored is null)
            return;

        // A deploy (from Claude, say) may have started while the window was open.
        if (IsRunning)
        {
            StatusMessage = "Settings not restored: a deploy is running. Restore once it has finished.";
            return;
        }

        var secrets = restored.Import.Secrets;
        var restoredSecrets = await ApplyRestoredSettingsAsync(restored.Import.Settings, secrets);
        var passwords = secrets.Count == 0 ? string.Empty
            : restoredSecrets == secrets.Count ? $", {restoredSecrets} saved password(s)"
            : ". Its saved passwords were skipped: no secure credential store on this computer";
        StatusMessage = $"Restored settings from {Path.GetFileName(restored.FilePath)}: {Sources.Count} source(s){passwords}. " +
                        $"The previous settings are in {BeforeRestoreFileName}.";
    }

    /// <summary>
    /// Replaces the settings with <paramref name="restored"/> (keeping the ones that belong to this machine), then
    /// reloads every source from the new list. The replaced settings are exported to
    /// <see cref="BeforeRestoreFileName"/> first, so the restore can itself be undone (that file holds no passwords).
    /// </summary>
    /// <param name="secrets">Saved passwords from the export, written to the credential store (replacing any there).</param>
    /// <returns>How many of <paramref name="secrets"/> were saved.</returns>
    public async Task<int> ApplyRestoredSettingsAsync(
        AppSettings restored, IReadOnlyDictionary<string, string>? secrets = null)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_settingsStore.FilePath))!;
            SettingsTransfer.Export(_settings, Path.Combine(dir, BeforeRestoreFileName), password: null);
        }
        catch
        {
            // Best-effort: the restore was asked for, and the user can still export first themselves.
        }

        SettingsTransfer.KeepLocal(restored, _settings);
        restored.MigrateLegacy();
        _settings = restored;

        // Before the save and the source reload, so the profiles load with their passwords and the automatic
        // export (if any) picks them up.
        var savedSecrets = 0;
        if (secrets is { Count: > 0 } && _credentialStore.IsAvailable)
        {
            foreach (var (key, secret) in secrets)
            {
                _credentialStore.Set(key, secret);
                savedSecrets++;
            }
        }

        _settingsStore.Save(_settings);

        RunInParallel = _settings.RunInParallel;
        BackupBeforePublish = _settings.BackupBeforePublish;
        DeployBackupPreview = _settings.DeployBackupPreview;
        ConfirmBeforeDeploy = _settings.ConfirmBeforeDeploy;
        RestoreSourcesOnStartup = _settings.RestoreSourcesOnStartup;
        ShowPaths = _settings.ShowPaths;
        CloseToTray = _settings.CloseToTray;
        UpdateRepository = _settings.UpdateRepository;
        SyncRecent();

        IsLoading = true;
        Sources.Clear();
        try
        {
            foreach (var source in _settings.Sources.ToList())
                Sources.Add(await BuildSourceAsync(source));
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(HasSources));
            if (FilterText.Length > 0)
                ApplyFilter();
        }

        return savedSecrets;
    }

    /// <summary>Restores a row's snapshot. Sets <see cref="IsRunning"/> before its first await.</summary>
    private async Task RunRestoreAsync(BackupHostViewModel host, DeploymentBackup backup, IRunObserver? observer)
    {
        IsRunning = true;
        _runCts = new CancellationTokenSource();
        var heading = $"── Restoring {host.Name} from {backup.DisplayName} ──";
        Log.Add(LogLine.System(heading));
        observer?.Output(heading);
        observer?.TargetChanged(host, PublishStatus.Running, null);
        StatusMessage = $"Restoring {host.Name}…";
        var outcome = PublishStatus.Failed;

        void Sink(OutputLine line)
        {
            observer?.Output($"[{host.Name}] {line.Text}");
            Dispatcher.UIThread.Post(() => AppendLog(LogLine.From(host.Name, line)));
        }

        try
        {
            await _backupService.RestoreAsync(
                backup,
                host.BackupOwner,
                host.BuildCredentials(),
                allowUntrustedCertificate: true,
                Sink,
                _runCts.Token);

            outcome = PublishStatus.Succeeded;
            StatusMessage = $"Restored {host.Name}.";
            Log.Add(LogLine.System($"── Restored {host.Name} ──"));
        }
        catch (OperationCanceledException)
        {
            outcome = PublishStatus.Cancelled;
            StatusMessage = "Restore cancelled.";
            Log.Add(LogLine.System("── Restore cancelled ──"));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Restore failed: {ex.Message}";
            Log.Add(LogLine.System($"ERROR: {ex.Message}"));
        }
        finally
        {
            IsRunning = false;
            _runCts?.Dispose();
            _runCts = null;
        }

        if (observer is not null)
        {
            var message = StatusMessage;
            Dispatcher.UIThread.Post(() =>
            {
                observer.TargetChanged(host, outcome, message);
                observer.Completed(outcome, message);
            });
        }
    }

    // ---- Remote control (MCP) ---------------------------------------------
    // Called on the UI thread by the MCP bridge. Unlike the window's commands these never open a dialog: the
    // caller has already confirmed with its user, and nobody may be looking at the window.

    /// <summary>Why a remote deploy or restore can't start right now, or null when it can.</summary>
    public string? RemoteBusyReason =>
        IsRunning ? "A deployment or restore is already running in the app. Wait for it to finish (see get_run)."
        : IsLoading ? "The app is still loading its sources. Try again in a moment."
        : null;

    /// <summary>
    /// Starts deploying the given rows with their saved credentials, without the confirmation or summary windows.
    /// Returns why it couldn't start, or null once it has (the observer then hears about the run).
    /// </summary>
    public string? StartRemoteDeploy(
        IReadOnlyList<BackupHostViewModel> targets, bool? runInParallel, bool? backupBeforePublish, IRunObserver observer)
    {
        if (RemoteBusyReason is { } busy)
            return busy;

        var profiles = targets.OfType<ProfileViewModel>().ToList();
        var scripts = targets.OfType<ScriptTargetViewModel>().ToList();

        var missingPasswords = profiles.Where(p => !p.CredentialsReady).Select(p => p.Name)
            .Concat(scripts.Where(s => !s.CredentialsReady).Select(s => s.Name))
            .ToList();
        if (missingPasswords.Count > 0)
            return $"No saved password for {string.Join(", ", missingPasswords)}. Set it in the app (the row's ⋯ menu → " +
                   "Credentials…) and tick 'Remember', or pick a saved credential (passwords are never sent over MCP).";

        if (scripts.FirstOrDefault(s => s.HasProblem) is { } broken)
            return $"Script '{broken.Name}' can't run: {broken.Problem}";

        if (FindUnavailableEngine(profiles, scripts) is { } engineError)
            return engineError;

        _ = RunDeploymentAsync(profiles, scripts,
            runInParallel ?? RunInParallel, backupBeforePublish ?? BackupBeforePublish, observer);
        return null;
    }

    /// <summary>Starts restoring a snapshot of a row. Returns why it couldn't start, or null once it has.</summary>
    public string? StartRemoteRestore(BackupHostViewModel host, DeploymentBackup backup, IRunObserver observer)
    {
        if (RemoteBusyReason is { } busy)
            return busy;

        _ = RunRestoreAsync(host, backup, observer);
        return null;
    }

    /// <summary>A row's snapshots straight from its store, newest first.</summary>
    public Task<IReadOnlyList<DeploymentBackup>> ListBackupsAsync(BackupHostViewModel host) =>
        _backupService.ListAsync(host.BackupOwner);

    /// <summary>What a profile's project gained in git since it was last deployed, as plain text.</summary>
    public async Task<string> BuildReleaseSummaryTextAsync(ProfileViewModel profile)
    {
        if (!await _gitHistory.IsAvailableAsync())
            return "git is not available on this machine, so no release summary can be built.";

        var previous = _settings.DeployHistory.TryGetValue(profile.Profile.FilePath, out var record)
            ? record.ProjectShas
            : new Dictionary<string, string>();
        var summary = await _gitHistory.BuildSummaryAsync(profile.Parent.Project.ProjectPath, profile.Parent.Name, previous);
        return summary.ToPlainText();
    }

    /// <summary>
    /// Deletes a snapshot from the Snapshots window, together with the older partial snapshots that need it (after
    /// <paramref name="confirm"/> agrees, when there are any). Returns what happened, for the window.
    /// </summary>
    public async Task<string> DeleteBackupAsync(BackupEntryViewModel entry, Func<string, string, string, Task<bool>> confirm)
    {
        var host = entry.Parent;
        if (!CanDeploy())
            return "Not deleted: a deploy or restore is running. Try again once it has finished.";

        // Older partial snapshots can't be restored without this one, so they have to go with it.
        IReadOnlyList<DeploymentBackup> deletionSet;
        try
        {
            deletionSet = await _backupService.GetDeletionSetAsync(entry.Backup);
        }
        catch (Exception ex)
        {
            return StatusMessage = $"Could not delete that snapshot: {ex.Message}";
        }

        if (deletionSet.Count > 1)
        {
            var older = deletionSet.Where(b => b.Id != entry.Backup.Id).Select(b => $"#{b.Sequence}");
            var confirmed = await confirm(
                "Delete snapshots",
                $"Snapshot #{entry.Backup.Sequence} of {host.Name} only records what its deploy changed, and every older " +
                $"snapshot of this kind needs it to be restored. Deleting it also deletes {string.Join(", ", older)}.",
                $"Delete {deletionSet.Count} snapshots");
            if (!confirmed)
                return "Nothing deleted.";
        }

        if (await _backupService.DeleteAsync(entry.Backup))
        {
            foreach (var deleted in deletionSet)
                Log.Add(LogLine.System($"Deleted snapshot {deleted.DisplayName} for {host.Name}."));
            StatusMessage = deletionSet.Count == 1
                ? $"Deleted snapshot for {host.Name}."
                : $"Deleted {deletionSet.Count} snapshots for {host.Name}.";
        }
        else
        {
            StatusMessage = "Could not delete that snapshot.";
        }

        var outcome = StatusMessage;
        await LoadBackupsAsync(host);
        return outcome;
    }

    // ---- Release summary (git history) ------------------------------------

    /// <summary>Opens the release-summary window for a profile's deployed project + dependencies.</summary>
    [RelayCommand]
    private async Task ReleaseSummaryAsync(ProfileViewModel? profile)
    {
        if (profile is null)
            return;

        StatusMessage = $"Building release summary for {profile.Parent.Name}…";
        try
        {
            var previous = _settings.DeployHistory.TryGetValue(profile.Profile.FilePath, out var record)
                ? record.ProjectShas
                : new Dictionary<string, string>();

            var summary = await _gitHistory.BuildSummaryAsync(
                profile.Parent.Project.ProjectPath, profile.Parent.Name, previous);

            await _releaseSummary.ShowAsync(summary);
            StatusMessage = "Ready.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not build release summary: {ex.Message}";
        }
    }

    /// <summary>
    /// After a successful deploy, logs what changed since the last deploy and records the current
    /// commit SHAs as the new baseline. Best-effort and informative only — never blocks a deploy.
    /// </summary>
    private async Task RecordReleaseHistoryAsync(IReadOnlyList<ProfileViewModel> deployed)
    {
        if (deployed.Count == 0 || !await _gitHistory.IsAvailableAsync())
            return;

        foreach (var profileVm in deployed)
        {
            try
            {
                var key = profileVm.Profile.FilePath;
                var previous = _settings.DeployHistory.TryGetValue(key, out var record)
                    ? record.ProjectShas
                    : new Dictionary<string, string>();

                var summary = await _gitHistory.BuildSummaryAsync(
                    profileVm.Parent.Project.ProjectPath, profileVm.Parent.Name, previous);

                foreach (var line in summary.ToPlainText().Split('\n'))
                    AppendLog(LogLine.From(profileVm.Name, OutputLine.Info(line.TrimEnd('\r'))));

                var current = summary.Projects
                    .Where(p => p.CurrentSha is not null)
                    .ToDictionary(p => p.ProjectPath, p => p.CurrentSha!);
                if (current.Count > 0)
                    _settings.DeployHistory[key] = new DeployRecord { DeployedUtc = DateTimeOffset.UtcNow, ProjectShas = current };
            }
            catch
            {
                // Informative feature only — never disrupt the deploy result.
            }
        }

        _settingsStore.Save(_settings);
    }

    [RelayCommand]
    private void ClearLog() => Log.Clear();

    private void AppendLog(LogLine line)
    {
        Log.Add(line);
        if (Log.Count > MaxLogLines)
            Log.RemoveAt(0);
    }

    // ---- Credentials ------------------------------------------------------

    /// <summary>Gives a row the credentials its settings point at (a library entry or its own).</summary>
    private void LoadCredentials(ICredentialTarget target)
    {
        var resolved = TargetCredentials.Resolve(_settings, _credentialStore, target.CredentialKey, target.DefaultUserName);
        target.ApplyCredentials(resolved, rememberPassword: resolved.HasSavedPassword);
    }

    /// <summary>
    /// Re-reads every row's credentials after the library changed. A row with its own password typed for this
    /// session only (not saved) keeps it.
    /// </summary>
    private void RefreshAllCredentials()
    {
        foreach (var target in AllCredentialTargets())
        {
            var sessionPassword = target.CredentialEntry is null && !target.RememberPassword ? target.Password : null;
            var resolved = TargetCredentials.Resolve(_settings, _credentialStore, target.CredentialKey, target.DefaultUserName);
            if (resolved is { Source: CredentialSource.Own, HasSavedPassword: false } && !string.IsNullOrEmpty(sessionPassword))
                target.ApplyCredentials(resolved with { Password = sessionPassword }, rememberPassword: false);
            else
                target.ApplyCredentials(resolved, rememberPassword: resolved.HasSavedPassword);
        }
    }

    private IEnumerable<ICredentialTarget> AllCredentialTargets() =>
        Sources.SelectMany(s => s.Projects)
            .SelectMany(p => p.Profiles.Cast<ICredentialTarget>().Concat(p.ScriptTargets));

    /// <summary>A target's display name from its credential key, for the library's "used by" lists.</summary>
    private string DescribeCredentialTarget(string key)
    {
        switch (AllCredentialTargets().FirstOrDefault(t => t.CredentialKey == key))
        {
            case ProfileViewModel profile:
                return $"{profile.Parent.Name} → {profile.Name}";
            case ScriptTargetViewModel script:
                return $"{script.Parent.Name} → {script.Name}";
        }

        if (_settings.ScriptTargets.Values.SelectMany(list => list).FirstOrDefault(t => t.CredentialKey == key) is { } saved)
            return $"{saved.Name} (script)";

        return Path.IsPathRooted(key) ? Path.GetFileNameWithoutExtension(key) : key;
    }

    /// <summary>Opens a row's Credentials dialog (⋯ → Credentials…).</summary>
    [RelayCommand]
    private Task EditCredentialsAsync(ICredentialTarget? target) =>
        target is null ? Task.CompletedTask : EditCredentialsCoreAsync(target, prompt: null);

    private async Task<bool> EditCredentialsCoreAsync(ICredentialTarget target, string? prompt)
    {
        if (_credentialsDialog is null)
            return false;

        var request = target switch
        {
            ProfileViewModel p => new CredentialsDialogRequest(
                p, $"Publish profile · {p.Parent.Name}", DescribeConnection(p), p.Profile, null, null, prompt),
            ScriptTargetViewModel s => new CredentialsDialogRequest(
                s, $"Script · {s.Parent.Name}", DescribeConnection(s), null, s.Target, s.Parent.ProjectDirectory, prompt),
            _ => throw new ArgumentException("Unknown target.", nameof(target)),
        };

        var result = await _credentialsDialog.EditAsync(_settings, request, DescribeCredentialTarget);
        if (result.LibraryChanged)
            RefreshAllCredentials();
        if (result.Saved)
            StatusMessage = $"Saved the credentials for {target.Name}.";
        return result.Saved;
    }

    /// <summary>Opens the credential library manager.</summary>
    [RelayCommand]
    private async Task ManageCredentialsAsync()
    {
        if (_credentialsDialog is not null && await _credentialsDialog.ShowLibraryAsync(_settings, DescribeCredentialTarget))
            RefreshAllCredentials();
    }

    /// <summary>
    /// Opens the Credentials dialog for each target that needs a password it doesn't have. False when the user
    /// cancels one, or no dialog can be shown.
    /// </summary>
    private async Task<bool> PromptForMissingPasswordsAsync(IEnumerable<ICredentialTarget> targets)
    {
        foreach (var target in targets.Where(t => !t.CredentialsReady).ToList())
        {
            var saved = await EditCredentialsCoreAsync(target,
                $"{target.Name} needs a password to deploy. Enter it (tick Remember to save it), or choose a saved credential.");
            if (!saved || !target.CredentialsReady)
                return false;
        }

        return true;
    }

    private static IReadOnlyList<ConnectionDetail> DescribeConnection(ProfileViewModel profile)
    {
        var p = profile.Profile;
        var details = new List<ConnectionDetail>
        {
            new("Profile", Path.GetFileName(p.FilePath)),
            new("Publish method", p.WebPublishMethod ?? "—"),
        };
        if (!string.IsNullOrWhiteSpace(p.ServerUrl))
            details.Add(new("Server URL", p.ServerUrl));
        if (!string.IsNullOrWhiteSpace(p.SiteName))
            details.Add(new("Site", p.SiteName));
        details.Add(new("User in profile", string.IsNullOrWhiteSpace(p.UserName) ? "(none)" : p.UserName));
        return details;
    }

    private static IReadOnlyList<ConnectionDetail> DescribeConnection(ScriptTargetViewModel script)
    {
        var t = script.Target;
        var details = new List<ConnectionDetail>
        {
            new("Script", t.ScriptPath),
            new("Username variable",
                string.IsNullOrWhiteSpace(t.UserNameVariable) ? ScriptTarget.DefaultUserNameVariable : t.UserNameVariable),
            new("Password variable",
                string.IsNullOrWhiteSpace(t.PasswordVariable) ? ScriptTarget.DefaultPasswordVariable : t.PasswordVariable),
        };
        if (t.BackupKind == ScriptBackupKind.WebDeploy && !string.IsNullOrWhiteSpace(t.BackupServerUrl))
            details.Add(new("Web Deploy server", t.BackupServerUrl));
        details.Add(new("Test command", string.IsNullOrWhiteSpace(t.TestCommand) ? "(none)" : t.TestCommand));
        return details;
    }

    // ---- Startup ----------------------------------------------------------

    /// <summary>Reloads the persisted sources (and their saved selections) when enabled.</summary>
    public async Task RunStartupLoadAsync()
    {
        if (!RestoreSourcesOnStartup || _settings.Sources.Count == 0)
            return;

        IsLoading = true;
        try
        {
            foreach (var source in _settings.Sources.ToList())
                Sources.Add(await BuildSourceAsync(source));

            StatusMessage = $"Restored {Sources.Count} source(s).";
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(HasSources));
            if (FilterText.Length > 0)
                ApplyFilter();
        }
    }

    // ---- Updates ----------------------------------------------------------

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        UpdateStatus = "Checking for updates…";
        _settings.UpdateRepository = UpdateRepository;
        _settingsStore.Save(_settings);
        try
        {
            var result = await _updateService.CheckForUpdatesAsync(UpdateRepository);
            UpdateStatus = result.Message;
            if (result.UpdateAvailable)
            {
                // Show what's new and let the user decide, rather than updating silently.
                if (await _updatePrompt.ConfirmUpdateAsync(result.Version, result.Notes))
                    await ApplyUpdateAsync();
                else
                    UpdateStatus = $"Update to {result.Version} postponed.";
            }
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Update check failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ApplyUpdateAsync()
    {
        UpdateStatus = "Downloading update…";
        try
        {
            var progress = new Progress<int>(p => Dispatcher.UIThread.Post(() => UpdateProgress = p));
            var result = await _updateService.DownloadAndApplyAsync(UpdateRepository, progress);
            UpdateStatus = result.Message;
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Update failed: {ex.Message}";
        }
    }

    /// <summary>Reopens the "what's new" dialog on demand (toolbar button).</summary>
    [RelayCommand]
    private async Task ShowWhatsNewAsync()
    {
        try
        {
            await _whatsNew.ShowAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not load release notes: {ex.Message}";
        }
        finally
        {
            // Mark the running version as seen even when opened manually, so a later startup won't
            // re-show it automatically.
            var current = CurrentVersionString;
            if (!string.IsNullOrEmpty(current) &&
                !string.Equals(_settings.LastSeenWhatsNewVersion, current, StringComparison.Ordinal))
            {
                _settings.LastSeenWhatsNewVersion = current;
                _settingsStore.Save(_settings);
            }
        }
    }

    public async Task RunStartupUpdateCheckAsync()
    {
        if (_settings.CheckForUpdatesOnStartup &&
            !string.IsNullOrWhiteSpace(UpdateRepository) &&
            !UpdateRepository.StartsWith("OWNER/", StringComparison.OrdinalIgnoreCase))
        {
            await CheckForUpdatesAsync();
        }
    }

    /// <summary>
    /// On first launch of a new version, pops up the "what's new" changelog (the release notes
    /// published to GitHub for the running tag). Subsequent launches of the same version stay quiet.
    /// Best-effort: if notes can't be fetched, the version is still marked seen so we don't retry
    /// every launch.
    /// </summary>
    public async Task RunStartupWhatsNewCheckAsync()
    {
        var current = CurrentVersionString;
        if (string.IsNullOrEmpty(current))
            return;

        if (string.Equals(_settings.LastSeenWhatsNewVersion, current, StringComparison.Ordinal))
            return;

        try
        {
            await _whatsNew.ShowAsync();
        }
        catch
        {
            // Informative only — never block startup.
        }
        finally
        {
            _settings.LastSeenWhatsNewVersion = current;
            _settingsStore.Save(_settings);
        }
    }
}
