using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolutionDeployer.Core.Backup;
using SolutionDeployer.Core.Configuration;

namespace SolutionDeployer.App.ViewModels;

/// <summary>Manages the list of named S3-compatible backup destinations (add / edit / delete / test).</summary>
public partial class RemoteTargetsViewModel : ObservableObject
{
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly ICredentialStore _credentialStore;
    private readonly HashSet<string> _originalIds;

    public RemoteTargetsViewModel(SettingsStore settingsStore, AppSettings settings, ICredentialStore credentialStore)
    {
        _settingsStore = settingsStore;
        _settings = settings;
        _credentialStore = credentialStore;
        _originalIds = settings.RemoteBackupTargets.Select(t => t.Id).ToHashSet();

        foreach (var target in settings.RemoteBackupTargets)
        {
            var secret = credentialStore.IsAvailable ? credentialStore.Get(target.SecretCredentialKey) : null;
            Targets.Add(new RemoteTargetEditViewModel(target, secret));
        }

        _selected = Targets.FirstOrDefault();
        CredentialStoreAvailable = credentialStore.IsAvailable;
    }

    public ObservableCollection<RemoteTargetEditViewModel> Targets { get; } = [];

    public bool CredentialStoreAvailable { get; }

    [ObservableProperty]
    private RemoteTargetEditViewModel? _selected;

    [ObservableProperty]
    private string? _testResult;

    public event Action? CloseRequested;

    [RelayCommand]
    private void Add()
    {
        var vm = new RemoteTargetEditViewModel(new S3BackupTarget { Name = "New remote" }, secretKey: null);
        Targets.Add(vm);
        Selected = vm;
    }

    [RelayCommand]
    private void Remove()
    {
        if (Selected is null)
            return;

        var index = Targets.IndexOf(Selected);
        Targets.Remove(Selected);
        Selected = Targets.Count == 0 ? null : Targets[Math.Min(index, Targets.Count - 1)];
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        if (Selected is null)
            return;

        TestResult = "Testing…";
        try
        {
            var store = new S3BackupStore(Selected.ToTarget(), Selected.SecretKey);
            await store.TestAsync();
            TestResult = $"✔ Connected to bucket '{Selected.Bucket}'.";
        }
        catch (Exception ex)
        {
            TestResult = $"✘ {ex.Message}";
        }
    }

    [RelayCommand]
    private void Save()
    {
        _settings.RemoteBackupTargets = Targets.Select(t => t.ToTarget()).ToList();

        if (_credentialStore.IsAvailable)
        {
            foreach (var vm in Targets)
            {
                var key = $"s3-secret:{vm.Id}";
                if (string.IsNullOrEmpty(vm.SecretKey))
                    _credentialStore.Delete(key);
                else
                    _credentialStore.Set(key, vm.SecretKey);
            }

            // Drop secrets for removed targets.
            foreach (var removedId in _originalIds.Except(Targets.Select(t => t.Id)))
                _credentialStore.Delete($"s3-secret:{removedId}");
        }

        _settingsStore.Save(_settings);
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();
}
