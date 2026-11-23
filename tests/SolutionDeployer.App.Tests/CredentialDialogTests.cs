using SolutionDeployer.App.Services;
using SolutionDeployer.App.ViewModels;
using SolutionDeployer.Core.Configuration;
using SolutionDeployer.Core.Models;

namespace SolutionDeployer.App.Tests;

public sealed class CredentialDialogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-creddlg-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsStore _settingsStore;
    private readonly MemoryCredentialStore _store = new();
    private readonly AppSettings _settings = new();

    public CredentialDialogTests()
    {
        Directory.CreateDirectory(_dir);
        _settingsStore = new SettingsStore(Path.Combine(_dir, "settings.json"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ProfileViewModel Profile()
    {
        var path = Path.Combine(_dir, "App.csproj");
        File.WriteAllText(path, """<Project Sdk="Microsoft.NET.Sdk.Web"></Project>""");
        var project = new ProjectViewModel(new DeploymentProject { Name = "App", ProjectPath = path });
        var profile = new PublishProfile
        {
            Name = "Prod",
            FilePath = Path.Combine(_dir, "Prod.pubxml"),
            Format = PublishProfileFormat.PubXml,
            WebPublishMethod = "MSDeploy",
            UserName = "from-profile",
            RequiresCredentials = true,
        };
        return new ProfileViewModel(project, profile, PublishEngineKind.Dotnet, null, null, credentialStoreAvailable: true);
    }

    private TargetCredentialsViewModel Dialog(ICredentialTarget target) =>
        new(_settings, _settingsStore, _store, new CredentialsDialogRequest(target, "Publish profile", [], null, null, null),
            test: null, testUnavailableReason: null);

    [Fact]
    public void Saving_own_credentials_persists_them_at_once_and_updates_the_row()
    {
        var profile = Profile();
        var dialog = Dialog(profile);
        Assert.True(dialog.UseOwn);
        Assert.Equal("from-profile", dialog.UserName);

        dialog.UserName = "deployer";
        dialog.Password = "pw";
        dialog.RememberPassword = true;
        dialog.SaveCommand.Execute(null);

        Assert.True(dialog.Saved);
        Assert.Equal("pw", _store.Get(profile.CredentialKey));
        Assert.Equal("deployer", _settingsStore.Load().RememberedUserNames[profile.CredentialKey]);
        Assert.Equal("deployer", profile.UserName);
        Assert.True(profile.CredentialsReady);
        Assert.Equal("deployer (password saved)", profile.CredentialsSummary);
    }

    [Fact]
    public void A_password_that_is_not_remembered_stays_on_the_row_only()
    {
        var profile = Profile();
        var dialog = Dialog(profile);

        dialog.Password = "typed";
        dialog.RememberPassword = false;
        dialog.SaveCommand.Execute(null);

        Assert.Null(_store.Get(profile.CredentialKey));
        Assert.Equal("typed", profile.Password);
        Assert.Equal("from-profile (password for this session)", profile.CredentialsSummary);
    }

    [Fact]
    public void Choosing_a_library_entry_points_the_target_at_it()
    {
        var entry = new SavedCredential { Name = "Prod IIS", UserName = "svc" };
        TargetCredentials.SaveLibraryEntry(_settings, _store, entry, "lib-pw");
        var profile = Profile();
        var dialog = Dialog(profile);

        dialog.UseLibrary = true;
        Assert.Same(entry, dialog.SelectedEntry);
        dialog.SaveCommand.Execute(null);

        Assert.Equal(entry.Id, _settingsStore.Load().CredentialAssignments[profile.CredentialKey]);
        Assert.Same(entry, profile.CredentialEntry);
        Assert.Equal("svc", profile.BuildCredentials().UserName);
        Assert.Equal("lib-pw", profile.BuildCredentials().Password);

        // Reopening shows the library choice.
        Assert.True(Dialog(profile).UseLibrary);
    }

    [Fact]
    public void Choosing_the_library_without_an_entry_is_refused()
    {
        var dialog = Dialog(Profile());

        dialog.UseLibrary = true;
        dialog.SaveCommand.Execute(null);

        Assert.False(dialog.Saved);
        Assert.NotNull(dialog.Error);
    }

    [Fact]
    public void Deleting_a_library_entry_in_use_asks_first_then_frees_its_targets()
    {
        var entry = new SavedCredential { Name = "Prod IIS", UserName = "svc" };
        TargetCredentials.SaveLibraryEntry(_settings, _store, entry, "lib-pw");
        _settings.AssignCredential("script:1", entry.Id);
        var library = new CredentialLibraryViewModel(_settings, _settingsStore, _store, key => $"Target {key}");

        Assert.Equal(["Target script:1"], library.Selected!.UsedBy);
        library.DeleteCommand.Execute(null);
        Assert.True(library.IsConfirmingDelete);
        Assert.Contains("Target script:1", library.DeleteWarning);
        Assert.Single(library.Entries);

        library.ConfirmDeleteCommand.Execute(null);
        Assert.Empty(library.Entries);
        Assert.Single(_settings.Credentials); // nothing changes until Save

        library.SaveCommand.Execute(null);
        Assert.True(library.Changed);
        Assert.Empty(_settings.Credentials);
        Assert.Empty(_settings.CredentialAssignments);
        Assert.Null(_store.Get(entry.PasswordKey));
    }

    [Fact]
    public void Library_entries_need_distinct_names()
    {
        var library = new CredentialLibraryViewModel(_settings, _settingsStore, _store, key => key);
        library.AddCommand.Execute(null);
        library.Selected!.Password = "pw";
        library.AddCommand.Execute(null);

        library.SaveCommand.Execute(null);
        Assert.False(library.Changed);
        Assert.Contains("New credential", library.Error);

        library.Selected!.Name = "Staging";
        library.SaveCommand.Execute(null);
        Assert.True(library.Changed);
        Assert.Equal(2, _settings.Credentials.Count);
        Assert.Equal("pw", _store.Get(_settings.Credentials.Single(c => c.Name == "Staging").PasswordKey)); // the duplicate check selected the first
    }

    private sealed class MemoryCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _secrets = new();
        public bool IsAvailable => true;
        public string? Get(string key) => _secrets.GetValueOrDefault(key);
        public void Set(string key, string secret) => _secrets[key] = secret;
        public void Delete(string key) => _secrets.Remove(key);
    }
}
