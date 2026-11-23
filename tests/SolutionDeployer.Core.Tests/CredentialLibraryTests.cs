using SolutionDeployer.Core.Configuration;
using SolutionDeployer.Core.Models;

namespace SolutionDeployer.Core.Tests;

public sealed class CredentialLibraryTests : IDisposable
{
    private const string ProfileKey = @"C:\repo\Web\Properties\PublishProfiles\Prod.pubxml";
    private const string OtherProfileKey = @"C:\repo\Api\Properties\PublishProfiles\Prod.pubxml";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"sd_credlib_{Guid.NewGuid():N}");

    public CredentialLibraryTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static (AppSettings Settings, MemoryCredentialStore Store, SavedCredential Entry) WithLibraryEntry()
    {
        var settings = new AppSettings();
        var store = new MemoryCredentialStore();
        var entry = new SavedCredential { Name = "Prod IIS", UserName = "svc-deploy", Notes = "Rotates yearly" };
        TargetCredentials.SaveLibraryEntry(settings, store, entry, "library-pw");
        return (settings, store, entry);
    }

    // ---- Resolution ----------------------------------------------------------

    [Fact]
    public void Own_credentials_are_the_remembered_username_and_saved_password()
    {
        var settings = new AppSettings();
        var store = new MemoryCredentialStore();
        settings.RememberedUserNames[ProfileKey] = "deployer";
        store.Set(ProfileKey, "own-pw");

        var resolved = TargetCredentials.Resolve(settings, store, ProfileKey, defaultUserName: "from-profile");

        Assert.Equal(CredentialSource.Own, resolved.Source);
        Assert.Null(resolved.Entry);
        Assert.Equal("deployer", resolved.UserName);
        Assert.Equal("own-pw", resolved.Password);
        Assert.True(resolved.HasSavedPassword);
    }

    [Fact]
    public void Without_a_remembered_username_the_profile_username_is_offered()
    {
        var resolved = TargetCredentials.Resolve(new AppSettings(), new MemoryCredentialStore(), ProfileKey, "from-profile");

        Assert.Equal("from-profile", resolved.UserName);
        Assert.Null(resolved.Password);
        Assert.False(resolved.HasSavedPassword);
    }

    [Fact]
    public void A_target_using_a_library_entry_gets_its_username_and_password()
    {
        var (settings, store, entry) = WithLibraryEntry();
        settings.RememberedUserNames[ProfileKey] = "own-user";
        store.Set(ProfileKey, "own-pw");
        TargetCredentials.UseLibraryEntry(settings, ProfileKey, entry.Id);

        var resolved = TargetCredentials.Resolve(settings, store, ProfileKey);

        Assert.Equal(CredentialSource.Library, resolved.Source);
        Assert.Same(entry, resolved.Entry);
        Assert.Equal("svc-deploy", resolved.UserName);
        Assert.Equal("library-pw", resolved.Password);
        Assert.Equal("svc-deploy", resolved.ToPublishCredentials().UserName);
        Assert.Equal("library-pw", resolved.ToPublishCredentials().Password);
    }

    [Fact]
    public void A_reference_to_a_missing_entry_falls_back_to_the_targets_own_credentials()
    {
        var settings = new AppSettings();
        var store = new MemoryCredentialStore();
        settings.RememberedUserNames[ProfileKey] = "own-user";
        settings.CredentialAssignments[ProfileKey] = "gone";

        var resolved = TargetCredentials.Resolve(settings, store, ProfileKey);

        Assert.Equal(CredentialSource.Own, resolved.Source);
        Assert.Equal("own-user", resolved.UserName);
    }

    [Fact]
    public void Without_a_secure_store_no_password_is_resolved()
    {
        var (settings, _, entry) = WithLibraryEntry();
        settings.AssignCredential(ProfileKey, entry.Id);

        var resolved = TargetCredentials.Resolve(settings, new NullCredentialStore(), ProfileKey);

        Assert.Equal("svc-deploy", resolved.UserName);
        Assert.Null(resolved.Password);
    }

    // ---- Saving ----------------------------------------------------------------

    [Fact]
    public void Saving_own_credentials_remembers_the_password_only_when_asked()
    {
        var settings = new AppSettings();
        var store = new MemoryCredentialStore();

        TargetCredentials.SaveOwn(settings, store, ProfileKey, " deployer ", "pw", rememberPassword: true);
        Assert.Equal("deployer", settings.RememberedUserNames[ProfileKey]);
        Assert.Equal("pw", store.Get(ProfileKey));

        TargetCredentials.SaveOwn(settings, store, ProfileKey, "deployer", "pw", rememberPassword: false);
        Assert.Null(store.Get(ProfileKey));
        Assert.Equal("deployer", settings.RememberedUserNames[ProfileKey]);

        TargetCredentials.SaveOwn(settings, store, ProfileKey, "  ", null, rememberPassword: false);
        Assert.False(settings.RememberedUserNames.ContainsKey(ProfileKey));
    }

    [Fact]
    public void Switching_to_a_library_entry_and_back_keeps_the_targets_own_credentials()
    {
        var (settings, store, entry) = WithLibraryEntry();
        TargetCredentials.SaveOwn(settings, store, ProfileKey, "own-user", "own-pw", rememberPassword: true);

        TargetCredentials.UseLibraryEntry(settings, ProfileKey, entry.Id);
        Assert.Equal(entry.Id, settings.CredentialAssignments[ProfileKey]);
        Assert.Equal("own-pw", store.Get(ProfileKey));

        TargetCredentials.SaveOwn(settings, store, ProfileKey, "own-user", "own-pw", rememberPassword: true);
        Assert.False(settings.CredentialAssignments.ContainsKey(ProfileKey));
        Assert.Equal(CredentialSource.Own, TargetCredentials.Resolve(settings, store, ProfileKey).Source);
    }

    [Fact]
    public void Using_an_unknown_entry_is_refused()
    {
        Assert.Throws<ArgumentException>(() => TargetCredentials.UseLibraryEntry(new AppSettings(), ProfileKey, "nope"));
    }

    [Fact]
    public void Library_entries_can_be_renamed_and_their_password_changed_or_cleared()
    {
        var (settings, store, entry) = WithLibraryEntry();

        var renamed = new SavedCredential { Id = entry.Id, Name = "Prod IIS (new)", UserName = "svc2" };
        TargetCredentials.SaveLibraryEntry(settings, store, renamed, "new-pw");
        Assert.Equal("Prod IIS (new)", settings.Credentials.Single().Name);
        Assert.Equal("new-pw", store.Get(SavedCredential.KeyFor(entry.Id)));

        TargetCredentials.SaveLibraryEntry(settings, store, renamed, password: "");
        Assert.Null(store.Get(SavedCredential.KeyFor(entry.Id)));
    }

    [Fact]
    public void Deleting_an_entry_sends_its_targets_back_to_their_own_credentials()
    {
        var (settings, store, entry) = WithLibraryEntry();
        var other = new SavedCredential { Name = "Staging", UserName = "stage" };
        TargetCredentials.SaveLibraryEntry(settings, store, other, "stage-pw");
        settings.AssignCredential(ProfileKey, entry.Id);
        settings.AssignCredential(OtherProfileKey, entry.Id);
        settings.AssignCredential("script:abc", other.Id);
        Assert.Equal([OtherProfileKey, ProfileKey], settings.TargetsUsing(entry.Id).Order());

        var affected = TargetCredentials.DeleteLibraryEntry(settings, store, entry.Id);

        Assert.Equal([OtherProfileKey, ProfileKey], affected.Order());
        Assert.Equal("Staging", settings.Credentials.Single().Name);
        Assert.Null(store.Get(entry.PasswordKey));
        Assert.Equal(other.Id, settings.CredentialAssignments.Single().Value);
        Assert.Equal(CredentialSource.Own, TargetCredentials.Resolve(settings, store, ProfileKey).Source);
    }

    [Fact]
    public void Forgetting_a_target_removes_its_username_password_and_reference()
    {
        var (settings, store, entry) = WithLibraryEntry();
        TargetCredentials.SaveOwn(settings, store, "script:abc", "u", "p", rememberPassword: true);
        settings.AssignCredential("script:abc", entry.Id);

        TargetCredentials.Forget(settings, store, "script:abc");

        Assert.Empty(settings.CredentialAssignments);
        Assert.Empty(settings.RememberedUserNames);
        Assert.Null(store.Get("script:abc"));
        Assert.Equal("library-pw", store.Get(entry.PasswordKey)); // the shared entry stays
    }

    [Fact]
    public void Settings_keep_the_library_but_never_its_passwords()
    {
        var (settings, store, entry) = WithLibraryEntry();
        settings.AssignCredential(ProfileKey, entry.Id);
        var settingsStore = new SettingsStore(Path.Combine(_tempDir, "settings.json"));

        settingsStore.Save(settings);
        var json = File.ReadAllText(settingsStore.FilePath);
        var loaded = settingsStore.Load();

        Assert.DoesNotContain("library-pw", json);
        Assert.Equal("Prod IIS", loaded.Credentials.Single().Name);
        Assert.Equal("Rotates yearly", loaded.Credentials.Single().Notes);
        Assert.Equal(entry.Id, loaded.CredentialAssignments[ProfileKey]);
        Assert.Equal("library-pw", TargetCredentials.Resolve(loaded, store, ProfileKey).Password);
    }

    // ---- Export ----------------------------------------------------------------

    [Fact]
    public void Secret_keys_include_the_library_and_targets_that_use_it()
    {
        var (settings, _, entry) = WithLibraryEntry();
        settings.AssignCredential(ProfileKey, entry.Id);

        var keys = SettingsTransfer.SecretKeys(settings);

        Assert.Contains(entry.PasswordKey, keys);
        Assert.Contains(ProfileKey, keys); // its own password, kept for switching back
    }

    [Fact]
    public void Library_passwords_travel_in_an_encrypted_export_and_dangling_references_are_dropped()
    {
        var (settings, store, entry) = WithLibraryEntry();
        settings.AssignCredential(ProfileKey, entry.Id);
        settings.CredentialAssignments[OtherProfileKey] = "deleted-entry";
        store.Set(SavedCredential.KeyFor("deleted-entry"), "stale");
        var path = Path.Combine(_tempDir, "export.json");

        var secrets = SettingsTransfer.CollectSecrets(settings, store);
        Assert.Equal("library-pw", secrets[entry.PasswordKey]);
        SettingsTransfer.Export(settings, path, "pw", secrets);
        Assert.DoesNotContain("library-pw", File.ReadAllText(path));

        var imported = SettingsTransfer.Import(path, "pw");

        Assert.Equal("Prod IIS", imported.Settings.Credentials.Single().Name);
        Assert.Equal(entry.Id, imported.Settings.CredentialAssignments[ProfileKey]);
        Assert.False(imported.Settings.CredentialAssignments.ContainsKey(OtherProfileKey));
        Assert.Equal("library-pw", imported.Secrets[entry.PasswordKey]);
        Assert.DoesNotContain(SavedCredential.KeyFor("deleted-entry"), imported.Secrets.Keys);
    }

    [Fact]
    public void An_export_from_before_the_library_imports_with_an_empty_one()
    {
        var path = Path.Combine(_tempDir, "old.json");
        File.WriteAllText(path, """
            {
              "Format": "3ai.SolutionDeployer.Settings",
              "Version": 1,
              "ExportedUtc": "2026-01-01T00:00:00+00:00",
              "Settings": { "RememberedUserNames": { "C:\\repo\\Prod.pubxml": "deployer" } }
            }
            """);

        var imported = SettingsTransfer.Import(path, password: null);

        Assert.Empty(imported.Settings.Credentials);
        Assert.Empty(imported.Settings.CredentialAssignments);
        Assert.Equal(CredentialSource.Own,
            TargetCredentials.Resolve(imported.Settings, new MemoryCredentialStore(), @"C:\repo\Prod.pubxml").Source);
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
