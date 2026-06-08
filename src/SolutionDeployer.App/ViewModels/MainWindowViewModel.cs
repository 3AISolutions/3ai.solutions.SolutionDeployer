using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolutionDeployer.App.Services;
using SolutionDeployer.Core.Configuration;
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
    private readonly AppSettings _settings;

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
        IScriptEditorService scriptEditor)
    {
        _sourceLoader = sourceLoader;
        _deploymentRunner = deploymentRunner;
        _engineFactory = engineFactory;
        _settingsStore = settingsStore;
        _filePicker = filePicker;
        _updateService = updateService;
        _credentialStore = credentialStore;
        _scriptEditor = scriptEditor;
        _settings = settingsStore.Load();
        _settings.MigrateLegacy();

        _runInParallel = _settings.RunInParallel;
        _updateRepository = _settings.UpdateRepository;
        _restoreSourcesOnStartup = _settings.RestoreSourcesOnStartup;
        SyncRecent();
    }

    public ObservableCollection<SourceViewModel> Sources { get; } = [];

    public ObservableCollection<LogLine> Log { get; } = [];

    public ObservableCollection<string> RecentSolutions { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeployCommand))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeployCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusMessage = "Add a solution or project to begin.";

    [ObservableProperty]
    private bool _runInParallel;

    [ObservableProperty]
    private bool _restoreSourcesOnStartup;

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

    partial void OnRestoreSourcesOnStartupChanged(bool value)
    {
        _settings.RestoreSourcesOnStartup = value;
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
        }
    }

    [RelayCommand]
    private void RemoveSource(SourceViewModel? source)
    {
        if (source is null)
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

        project.ScriptTargets.Add(new ScriptTargetViewModel(project, result));
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
        StatusMessage = $"Updated script '{result.Name}'.";
    }

    [RelayCommand]
    private void RemoveScript(ScriptTargetViewModel? script)
    {
        if (script is null)
            return;

        var project = script.Parent;
        project.ScriptTargets.Remove(script);
        project.NotifyScriptsChanged();
        PersistScriptTargets(project);
        OnPropertyChanged(nameof(SelectedCount));
        SaveCurrentSelection();
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
                    _settings.RememberedUserNames.TryGetValue(profile.FilePath, out var rememberedUser);
                    var rememberedPassword = credentialsAvailable ? _credentialStore.Get(profile.FilePath) : null;
                    projectVm.Profiles.Add(new ProfileViewModel(
                        projectVm, profile, _settings.DefaultEngine, rememberedUser, rememberedPassword, credentialsAvailable));
                }

                foreach (var script in _settings.GetScriptTargets(project.ProjectPath))
                    projectVm.ScriptTargets.Add(new ScriptTargetViewModel(projectVm, script));

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

    [RelayCommand]
    private void SelectAll() => SetAllSelected(true);

    [RelayCommand]
    private void ClearSelection() => SetAllSelected(false);

    private void SetAllSelected(bool value)
    {
        _applyingState = true;
        foreach (var project in Sources.SelectMany(s => s.Projects))
        {
            foreach (var profile in project.Profiles)
                profile.IsSelected = value;
            foreach (var script in project.ScriptTargets)
                script.IsSelected = value;
        }
        _applyingState = false;

        OnPropertyChanged(nameof(SelectedCount));
        SaveCurrentSelection();
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

        var engineKinds = selectedProfiles.Select(p => p.Engine);
        if (selectedScripts.Count > 0)
            engineKinds = engineKinds.Append(PublishEngineKind.Script);
        foreach (var engineKind in engineKinds.Distinct())
        {
            if (!_engineFactory.Get(engineKind).IsAvailable(out var reason))
            {
                StatusMessage = $"{engineKind} engine unavailable: {reason}";
                Log.Add(LogLine.System($"ERROR: {StatusMessage}"));
                return;
            }
        }

        var jobsByTarget = new Dictionary<string, ISelectableTarget>();
        var jobs = new List<PublishJob>();

        foreach (var profileVm in selectedProfiles)
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

            profileVm.Status = PublishStatus.Pending;
            profileVm.ResultText = string.Empty;

            if (!string.IsNullOrWhiteSpace(profileVm.UserName))
                _settings.RememberedUserNames[profileVm.Profile.FilePath] = profileVm.UserName;

            PersistPassword(profileVm);
        }

        foreach (var scriptVm in selectedScripts)
        {
            var job = new PublishJob
            {
                Project = scriptVm.Parent.Project,
                Script = scriptVm.Target,
                Engine = PublishEngineKind.Script,
                Configuration = "Release",
            };
            jobs.Add(job);
            jobsByTarget[job.Id] = scriptVm;

            scriptVm.Status = PublishStatus.Pending;
            scriptVm.ResultText = string.Empty;
        }

        _settingsStore.Save(_settings);

        IsRunning = true;
        _runCts = new CancellationTokenSource();
        var startedJobs = new HashSet<string>();
        Log.Add(LogLine.System($"── Deploying {jobs.Count} target(s) ({(RunInParallel ? "parallel" : "sequential")}) ──"));
        StatusMessage = $"Deploying {jobs.Count} target(s)…";

        void OnOutput(JobOutput output) => Dispatcher.UIThread.Post(() =>
        {
            if (startedJobs.Add(output.JobId) && jobsByTarget.TryGetValue(output.JobId, out var vmStart))
                vmStart.Status = PublishStatus.Running;

            AppendLog(LogLine.From(output.JobDisplayName, output.Line));
        });

        void OnJobCompleted(PublishResult result) => Dispatcher.UIThread.Post(() =>
        {
            if (jobsByTarget.TryGetValue(result.JobId, out var vm))
            {
                vm.Status = result.Status;
                vm.ResultText = result.IsSuccess
                    ? $"OK ({result.Duration.TotalSeconds:F1}s)"
                    : result.ErrorMessage ?? result.Status.ToString();
            }
        });

        try
        {
            var options = new DeploymentRunOptions { RunInParallel = RunInParallel, MaxParallelism = 4 };
            var results = await _deploymentRunner.RunAsync(jobs, options, OnOutput, OnJobCompleted, _runCts.Token);

            var ok = results.Count(r => r.IsSuccess);
            var failed = results.Count - ok;
            StatusMessage = failed == 0
                ? $"All {ok} target(s) succeeded."
                : $"{ok} succeeded, {failed} failed.";
            Log.Add(LogLine.System($"── {StatusMessage} ──"));
        }
        catch (OperationCanceledException)
        {
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
        }
    }

    private bool CanCancel() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _runCts?.Cancel();
        StatusMessage = "Cancelling…";
    }

    [RelayCommand]
    private void ClearLog() => Log.Clear();

    private void AppendLog(LogLine line)
    {
        Log.Add(line);
        if (Log.Count > MaxLogLines)
            Log.RemoveAt(0);
    }

    /// <summary>Saves or clears a profile's password in the OS secure store based on its opt-in flag.</summary>
    private void PersistPassword(ProfileViewModel profileVm)
    {
        if (!_credentialStore.IsAvailable)
            return;

        if (profileVm.RememberPassword && !string.IsNullOrEmpty(profileVm.Password))
            _credentialStore.Set(profileVm.Profile.FilePath, profileVm.Password);
        else
            _credentialStore.Delete(profileVm.Profile.FilePath);
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
                await ApplyUpdateAsync();
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

    public async Task RunStartupUpdateCheckAsync()
    {
        if (_settings.CheckForUpdatesOnStartup &&
            !string.IsNullOrWhiteSpace(UpdateRepository) &&
            !UpdateRepository.StartsWith("OWNER/", StringComparison.OrdinalIgnoreCase))
        {
            await CheckForUpdatesAsync();
        }
    }
}
