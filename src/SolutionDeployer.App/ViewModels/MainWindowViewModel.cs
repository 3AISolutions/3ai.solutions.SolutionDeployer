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
    private readonly ISolutionParser _solutionParser;
    private readonly DeploymentRunner _deploymentRunner;
    private readonly IPublishEngineFactory _engineFactory;
    private readonly SettingsStore _settingsStore;
    private readonly IFilePickerService _filePicker;
    private readonly UpdateService _updateService;
    private readonly AppSettings _settings;

    private CancellationTokenSource? _runCts;
    private const int MaxLogLines = 8000;

    public MainWindowViewModel(
        ISolutionParser solutionParser,
        DeploymentRunner deploymentRunner,
        IPublishEngineFactory engineFactory,
        SettingsStore settingsStore,
        IFilePickerService filePicker,
        UpdateService updateService)
    {
        _solutionParser = solutionParser;
        _deploymentRunner = deploymentRunner;
        _engineFactory = engineFactory;
        _settingsStore = settingsStore;
        _filePicker = filePicker;
        _updateService = updateService;
        _settings = settingsStore.Load();

        _runInParallel = _settings.RunInParallel;
        _updateRepository = _settings.UpdateRepository;
        foreach (var recent in _settings.RecentSolutions)
            RecentSolutions.Add(recent);
    }

    public ObservableCollection<ProjectViewModel> Projects { get; } = [];

    public ObservableCollection<LogLine> Log { get; } = [];

    public ObservableCollection<string> RecentSolutions { get; } = [];

    public IReadOnlyList<PublishEngineKind> Engines { get; } =
        [PublishEngineKind.Dotnet, PublishEngineKind.MsBuild];

    [ObservableProperty]
    private string? _solutionPath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeployCommand))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeployCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusMessage = "Open a solution to begin.";

    [ObservableProperty]
    private bool _runInParallel;

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

    public int SelectedCount =>
        Projects.Sum(p => p.Profiles.Count(pr => pr.IsSelected));

    // ---- Solution loading -------------------------------------------------

    [RelayCommand]
    private async Task BrowseSolutionAsync()
    {
        var path = await _filePicker.PickSolutionAsync();
        if (!string.IsNullOrEmpty(path))
            await LoadSolutionAsync(path);
    }

    [RelayCommand]
    private async Task OpenRecentAsync(string? path)
    {
        if (!string.IsNullOrEmpty(path))
            await LoadSolutionAsync(path);
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (!string.IsNullOrEmpty(SolutionPath))
            await LoadSolutionAsync(SolutionPath);
    }

    private async Task LoadSolutionAsync(string path)
    {
        IsLoading = true;
        StatusMessage = $"Loading {Path.GetFileName(path)}…";
        Projects.Clear();

        try
        {
            var solution = await _solutionParser.ParseAsync(path);
            SolutionPath = solution.SolutionPath;

            foreach (var project in solution.Projects)
            {
                var projectVm = new ProjectViewModel(project);
                projectVm.SelectionChanged += () => OnPropertyChanged(nameof(SelectedCount));
                foreach (var profile in project.Profiles)
                {
                    _settings.RememberedUserNames.TryGetValue(profile.FilePath, out var remembered);
                    projectVm.Profiles.Add(new ProfileViewModel(projectVm, profile, _settings.DefaultEngine, remembered));
                }
                Projects.Add(projectVm);
            }

            _settings.AddRecentSolution(solution.SolutionPath);
            _settingsStore.Save(_settings);
            SyncRecent();

            var profileCount = Projects.Sum(p => p.Profiles.Count);
            StatusMessage = $"{solution.Name}: {Projects.Count} project(s), {profileCount} profile(s).";
            if (profileCount == 0)
                StatusMessage += " No publish profiles found (looked in Properties/PublishProfiles).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load solution: {ex.Message}";
            Log.Add(LogLine.System($"ERROR: {ex.Message}"));
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(SelectedCount));
        }
    }

    private void SyncRecent()
    {
        RecentSolutions.Clear();
        foreach (var recent in _settings.RecentSolutions)
            RecentSolutions.Add(recent);
    }

    // ---- Selection helpers ------------------------------------------------

    [RelayCommand]
    private void SelectAll() => SetAllSelected(true);

    [RelayCommand]
    private void ClearSelection() => SetAllSelected(false);

    private void SetAllSelected(bool value)
    {
        foreach (var project in Projects)
            foreach (var profile in project.Profiles)
                profile.IsSelected = value;
        OnPropertyChanged(nameof(SelectedCount));
    }

    // ---- Deployment -------------------------------------------------------

    private bool CanDeploy() => !IsRunning && !IsLoading;

    [RelayCommand(CanExecute = nameof(CanDeploy))]
    private async Task DeployAsync()
    {
        var selected = Projects
            .SelectMany(p => p.Profiles)
            .Where(pr => pr.IsSelected)
            .ToList();

        if (selected.Count == 0)
        {
            StatusMessage = "Select at least one profile to deploy.";
            return;
        }

        // Pre-flight: make sure every chosen engine is actually available here.
        foreach (var engineKind in selected.Select(s => s.Engine).Distinct())
        {
            if (!_engineFactory.Get(engineKind).IsAvailable(out var reason))
            {
                StatusMessage = $"{engineKind} engine unavailable: {reason}";
                Log.Add(LogLine.System($"ERROR: {StatusMessage}"));
                return;
            }
        }

        var jobsByProfile = new Dictionary<string, ProfileViewModel>();
        var jobs = new List<PublishJob>();
        foreach (var profileVm in selected)
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
            jobsByProfile[job.Id] = profileVm;

            profileVm.Status = PublishStatus.Pending;
            profileVm.ResultText = string.Empty;

            // Remember (non-secret) usernames for next time.
            if (!string.IsNullOrWhiteSpace(profileVm.UserName))
                _settings.RememberedUserNames[profileVm.Profile.FilePath] = profileVm.UserName;
        }

        _settingsStore.Save(_settings);

        IsRunning = true;
        _runCts = new CancellationTokenSource();
        var startedJobs = new HashSet<string>();
        Log.Add(LogLine.System($"── Deploying {jobs.Count} target(s) ({(RunInParallel ? "parallel" : "sequential")}) ──"));
        StatusMessage = $"Deploying {jobs.Count} target(s)…";

        void OnOutput(JobOutput output) => Dispatcher.UIThread.Post(() =>
        {
            if (startedJobs.Add(output.JobId) && jobsByProfile.TryGetValue(output.JobId, out var vmStart))
                vmStart.Status = PublishStatus.Running;

            AppendLog(LogLine.From(output.JobDisplayName, output.Line));
        });

        void OnJobCompleted(PublishResult result) => Dispatcher.UIThread.Post(() =>
        {
            if (jobsByProfile.TryGetValue(result.JobId, out var vm))
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
