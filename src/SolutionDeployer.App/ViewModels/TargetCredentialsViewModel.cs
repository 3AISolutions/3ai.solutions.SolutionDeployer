using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolutionDeployer.App.Services;
using SolutionDeployer.Core.Configuration;
using SolutionDeployer.Core.Models;
using SolutionDeployer.Core.Publishing;

namespace SolutionDeployer.App.ViewModels;

/// <summary>Runs a credential test with the given credentials, reporting output lines as it goes.</summary>
public delegate Task<CredentialTestResult> CredentialTest(
    PublishCredentials credentials, Action<OutputLine> onOutput, CancellationToken cancellationToken);

/// <summary>
/// Backs the per-target Credentials dialog: shows how the target connects, lets the user pick a library entry or
/// enter the target's own username/password, tests them, and saves them at once (no deploy needed).
/// </summary>
public partial class TargetCredentialsViewModel : ObservableObject
{
    private const int MaxOutputLines = 200;

    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly ICredentialStore _credentialStore;
    private readonly CredentialTest? _test;
    private CancellationTokenSource? _testCts;

    public TargetCredentialsViewModel(
        AppSettings settings,
        SettingsStore settingsStore,
        ICredentialStore credentialStore,
        CredentialsDialogRequest request,
        CredentialTest? test,
        string? testUnavailableReason)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _credentialStore = credentialStore;
        _test = test;
        Request = request;
        TestUnavailableReason = testUnavailableReason;
        CredentialStoreAvailable = credentialStore.IsAvailable;
        TestOutput.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTestOutput));

        ReloadLibrary(request.Target.CredentialEntry?.Id);
        var target = request.Target;
        _useLibrary = target.CredentialEntry is not null && SelectedEntry is not null;
        _userName = target.CredentialEntry is null ? target.UserName : OwnUserName();
        _password = target.CredentialEntry is null ? target.Password : OwnPassword() ?? string.Empty;
        _rememberPassword = CredentialStoreAvailable &&
                            (target.CredentialEntry is null ? target.RememberPassword || string.IsNullOrEmpty(target.Password)
                                                            : true);
    }

    public CredentialsDialogRequest Request { get; }

    public string Title => $"Credentials · {Request.Target.Name}";

    public string Heading => Request.Target.Name;

    public string Kind => Request.Kind;

    public IReadOnlyList<ConnectionDetail> Connection => Request.Connection;

    public string? Prompt => Request.Prompt;

    public bool HasPrompt => !string.IsNullOrEmpty(Prompt);

    public bool CredentialStoreAvailable { get; }

    public ObservableCollection<SavedCredential> Library { get; } = [];

    public bool HasLibrary => Library.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseOwn))]
    private bool _useLibrary;

    public bool UseOwn
    {
        get => !UseLibrary;
        set => UseLibrary = !value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedEntryInfo))]
    private SavedCredential? _selectedEntry;

    /// <summary>The chosen entry's username and whether its password is saved.</summary>
    public string SelectedEntryInfo => SelectedEntry is not { } entry
        ? "Choose a saved credential."
        : $"Signs in as {(string.IsNullOrWhiteSpace(entry.UserName) ? "(no username)" : entry.UserName)}" +
          (LibraryPassword(entry) is null ? ". No password is saved for it: set one in the library." : ", password saved.") +
          (string.IsNullOrWhiteSpace(entry.Notes) ? string.Empty : $"\n{entry.Notes}");

    [ObservableProperty]
    private string _userName;

    [ObservableProperty]
    private string _password;

    [ObservableProperty]
    private bool _rememberPassword;

    // ---- Test ----

    public bool CanTest => _test is not null;

    /// <summary>Why there's no Test (e.g. a script without a test command); null when it can test.</summary>
    public string? TestUnavailableReason { get; }

    public bool HasTestUnavailableReason => !CanTest && !string.IsNullOrEmpty(TestUnavailableReason);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    private bool _isTesting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestResult))]
    private string? _testResult;

    [ObservableProperty]
    private bool _testSucceeded;

    public bool HasTestResult => !string.IsNullOrEmpty(TestResult);

    public ObservableCollection<string> TestOutput { get; } = [];

    public bool HasTestOutput => TestOutput.Count > 0;

    [ObservableProperty]
    private string? _error;

    /// <summary>Opens the library manager; returns true when it changed. Set by the dialog service.</summary>
    public Func<Task<bool>>? ManageLibraryHandler { get; set; }

    public bool Saved { get; private set; }

    public bool LibraryChanged { get; private set; }

    public event Action? CloseRequested;

    private bool CanRunTest() => !IsTesting;

    [RelayCommand(CanExecute = nameof(CanRunTest))]
    private async Task TestAsync()
    {
        if (_test is null)
            return;

        if (UseLibrary && SelectedEntry is null)
        {
            Error = "Choose a saved credential to test.";
            return;
        }

        Error = null;
        TestOutput.Clear();
        TestResult = "Testing…";
        TestSucceeded = false;
        IsTesting = true;
        _testCts = new CancellationTokenSource();
        try
        {
            var result = await _test(CurrentCredentials(), line => Dispatcher.UIThread.Post(() => AddOutput(line.Text)),
                _testCts.Token);
            TestSucceeded = result.Success;
            TestResult = (result.Success ? "✔ " : "✘ ") + result.Message;
        }
        catch (OperationCanceledException)
        {
            TestResult = "Test cancelled.";
        }
        catch (Exception ex)
        {
            TestResult = $"✘ {ex.Message}";
        }
        finally
        {
            IsTesting = false;
            _testCts.Dispose();
            _testCts = null;
        }
    }

    private void AddOutput(string text)
    {
        TestOutput.Add(text);
        if (TestOutput.Count > MaxOutputLines)
            TestOutput.RemoveAt(0);
    }

    [RelayCommand]
    private async Task ManageLibraryAsync()
    {
        if (ManageLibraryHandler is null || !await ManageLibraryHandler())
            return;

        LibraryChanged = true;
        ReloadLibrary(SelectedEntry?.Id);
        if (UseLibrary && SelectedEntry is null)
            UseLibrary = false; // the entry was deleted
        OnPropertyChanged(nameof(SelectedEntryInfo));
    }

    [RelayCommand]
    private void Save()
    {
        var key = Request.Target.CredentialKey;
        if (UseLibrary)
        {
            if (SelectedEntry is null)
            {
                Error = "Choose a saved credential, or use this target's own username and password.";
                return;
            }

            TargetCredentials.UseLibraryEntry(_settings, key, SelectedEntry.Id);
            Request.Target.ApplyCredentials(TargetCredentials.Resolve(_settings, _credentialStore, key), rememberPassword: true);
        }
        else
        {
            var remember = RememberPassword && CredentialStoreAvailable;
            TargetCredentials.SaveOwn(_settings, _credentialStore, key, UserName, Password, remember);
            Request.Target.ApplyCredentials(
                new ResolvedCredentials(CredentialSource.Own, null, UserName.Trim(), Password), remember);
        }

        _settingsStore.Save(_settings);
        Saved = true;
        Close();
    }

    [RelayCommand]
    private void Cancel() => Close();

    private void Close()
    {
        _testCts?.Cancel();
        CloseRequested?.Invoke();
    }

    /// <summary>The credentials the form currently describes (for Test).</summary>
    private PublishCredentials CurrentCredentials() => UseLibrary && SelectedEntry is { } entry
        ? new PublishCredentials
        {
            UserName = string.IsNullOrWhiteSpace(entry.UserName) ? null : entry.UserName,
            Password = LibraryPassword(entry),
        }
        : new PublishCredentials
        {
            UserName = string.IsNullOrWhiteSpace(UserName) ? null : UserName.Trim(),
            Password = string.IsNullOrEmpty(Password) ? null : Password,
        };

    private void ReloadLibrary(string? selectId)
    {
        Library.Clear();
        foreach (var entry in _settings.Credentials.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            Library.Add(entry);
        SelectedEntry = Library.FirstOrDefault(c => c.Id == selectId) ?? (selectId is null ? Library.FirstOrDefault() : null);
        OnPropertyChanged(nameof(HasLibrary));
    }

    private string? LibraryPassword(SavedCredential entry) =>
        _credentialStore.IsAvailable && _credentialStore.Get(entry.PasswordKey) is { Length: > 0 } secret ? secret : null;

    /// <summary>The target's own (saved) username, shown when switching away from a library entry.</summary>
    private string OwnUserName() =>
        _settings.RememberedUserNames.TryGetValue(Request.Target.CredentialKey, out var user)
            ? user
            : Request.Target.DefaultUserName ?? string.Empty;

    private string? OwnPassword() =>
        _credentialStore.IsAvailable && _credentialStore.Get(Request.Target.CredentialKey) is { Length: > 0 } secret ? secret : null;
}
