using SolutionDeployer.Core.Configuration;
using SolutionDeployer.Core.Models;

namespace SolutionDeployer.Core.Tests;

public sealed class SettingsTransferTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"sd_export_{Guid.NewGuid():N}");

    public SettingsTransferTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static AppSettings SampleSettings()
    {
        var settings = new AppSettings
        {
            RunInParallel = true,
            BackupRetention = 4,
            WindowWidth = 1200,
            WindowX = 40,
            LastSeenWhatsNewVersion = "0.7.15",
            StartWithOperatingSystem = true,
            McpServerEnabled = true,
            AutoExport = new SettingsAutoExport { Enabled = true, FilePath = @"C:\exports\settings.json" },
        };
        settings.Sources.Add(DeploymentSource.Solution(@"C:\repo\SecretProject.slnx"));
        settings.SetScriptTargets(@"C:\repo\Web\Web.csproj", [new ScriptTarget { Name = "Deploy", ScriptPath = "deploy.ps1" }]);
        settings.RememberedUserNames[@"C:\repo\Web\Properties\PublishProfiles\Prod.pubxml"] = "deployer";
        settings.SetTargetTags(@"C:\repo\Web\Properties\PublishProfiles\Prod.pubxml", ["prod", "EU"]);
        return settings;
    }

    [Fact]
    public void Plain_export_round_trips_and_leaves_out_machine_settings()
    {
        var path = Path.Combine(_tempDir, "export.json");
        SettingsTransfer.Export(SampleSettings(), path, password: null);

        Assert.False(SettingsTransfer.ReadInfo(path).IsEncrypted);
        var imported = SettingsTransfer.Import(path, password: null).Settings;

        Assert.True(imported.RunInParallel);
        Assert.Equal(4, imported.BackupRetention);
        Assert.Equal(@"C:\repo\SecretProject.slnx", Assert.Single(imported.Sources).Path);
        Assert.Equal("Deploy", Assert.Single(imported.GetScriptTargets(@"C:\repo\Web\Web.csproj")).Name);
        Assert.Equal("deployer", imported.RememberedUserNames.Values.Single());
        Assert.Equal(["prod", "EU"], imported.GetTargetTags(@"C:\repo\Web\Properties\PublishProfiles\Prod.pubxml"));

        Assert.Null(imported.WindowWidth);
        Assert.Null(imported.WindowX);
        Assert.Null(imported.LastSeenWhatsNewVersion);
        Assert.False(imported.StartWithOperatingSystem);
        Assert.False(imported.McpServerEnabled);
        Assert.False(imported.AutoExport.Enabled);
        Assert.Null(imported.AutoExport.FilePath);
    }

    [Fact]
    public void Encrypted_export_hides_the_settings_and_needs_the_password()
    {
        var path = Path.Combine(_tempDir, "export.json");
        SettingsTransfer.Export(SampleSettings(), path, "correct horse");

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("SecretProject", text);
        Assert.DoesNotContain("deployer", text);
        Assert.True(SettingsTransfer.ReadInfo(path).IsEncrypted);

        Assert.Throws<SettingsPasswordException>(() => SettingsTransfer.Import(path, password: null));
        Assert.Throws<SettingsPasswordException>(() => SettingsTransfer.Import(path, "wrong"));

        var imported = SettingsTransfer.Import(path, "correct horse").Settings;
        Assert.Equal(@"C:\repo\SecretProject.slnx", Assert.Single(imported.Sources).Path);
    }

    [Fact]
    public void Tampered_ciphertext_is_rejected()
    {
        var path = Path.Combine(_tempDir, "export.json");
        SettingsTransfer.Export(SampleSettings(), path, "pw");

        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        var cipher = Convert.FromBase64String((string)json["Encrypted"]!["CipherText"]!);
        cipher[0] ^= 0xFF;
        json["Encrypted"]!["CipherText"] = Convert.ToBase64String(cipher);
        File.WriteAllText(path, json.ToJsonString());

        Assert.Throws<SettingsPasswordException>(() => SettingsTransfer.Import(path, "pw"));
    }

    [Fact]
    public void A_file_that_is_not_an_export_is_refused()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        new SettingsStore(path).Save(SampleSettings()); // the raw settings file, not an export

        Assert.Throws<InvalidDataException>(() => SettingsTransfer.ReadInfo(path));

        File.WriteAllText(path, "not json");
        Assert.Throws<InvalidDataException>(() => SettingsTransfer.Import(path, null));
    }

    [Fact]
    public void Encrypted_export_carries_target_tags()
    {
        var path = Path.Combine(_tempDir, "tags.sdsettings");
        SettingsTransfer.Export(SampleSettings(), path, password: "pa55word");

        var imported = SettingsTransfer.Import(path, "pa55word").Settings;

        Assert.Equal(["prod", "EU"], imported.TargetTags.Values.Single());
    }

    [Fact]
    public void Target_tags_are_trimmed_and_deduplicated_ignoring_case()
    {
        var settings = new AppSettings();

        settings.SetTargetTags("a.pubxml", [" Prod ", "prod", "", null, "EU", "eu ", "Web"]);
        settings.SetTargetTags("script:1", ["web", "Linux"]);

        Assert.Equal(["Prod", "EU", "Web"], settings.GetTargetTags("a.pubxml"));
        Assert.Equal(["EU", "Linux", "Prod", "Web"], settings.AllTargetTags());

        settings.SetTargetTags("a.pubxml", ["  "]);
        Assert.False(settings.TargetTags.ContainsKey("a.pubxml"));
        Assert.Empty(settings.GetTargetTags("a.pubxml"));
    }

    [Fact]
    public void Restore_keeps_this_machines_own_settings()
    {
        var current = new AppSettings
        {
            WindowWidth = 900,
            McpServerEnabled = true,
            McpServerPort = 50000,
            StartWithOperatingSystem = true,
            AutoExport = new SettingsAutoExport { Enabled = true, FilePath = @"D:\mine.json", Encrypt = true },
        };
        var restored = new AppSettings { RunInParallel = true };

        SettingsTransfer.KeepLocal(restored, current);

        Assert.True(restored.RunInParallel);
        Assert.Equal(900, restored.WindowWidth);
        Assert.True(restored.McpServerEnabled);
        Assert.Equal(50000, restored.McpServerPort);
        Assert.True(restored.StartWithOperatingSystem);
        Assert.Equal(@"D:\mine.json", restored.AutoExport.FilePath);
        Assert.True(restored.AutoExport.Encrypt);
    }

    [Fact]
    public void Saving_settings_exports_them_automatically_but_only_when_they_changed()
    {
        var store = new SettingsStore(Path.Combine(_tempDir, "settings.json"));
        using var exporter = new SettingsAutoExporter(store, new MemoryCredentialStore(), TimeSpan.FromHours(1));
        var exportPath = Path.Combine(_tempDir, "sub", "auto.json");
        var settings = new AppSettings { AutoExport = new SettingsAutoExport { Enabled = true, FilePath = exportPath } };

        store.Save(settings);
        exporter.Flush();
        Assert.True(File.Exists(exportPath));
        Assert.True(exporter.LastStatus!.Succeeded);

        // Only the window moved: nothing to export.
        var firstStatus = exporter.LastStatus;
        settings.WindowX = 123;
        store.Save(settings);
        exporter.Flush();
        Assert.Same(firstStatus, exporter.LastStatus);

        settings.RunInParallel = true;
        store.Save(settings);
        exporter.Flush();
        Assert.NotSame(firstStatus, exporter.LastStatus);
        Assert.True(SettingsTransfer.Import(exportPath, null).Settings.RunInParallel);
    }

    [Fact]
    public void Automatic_export_is_off_until_enabled()
    {
        var store = new SettingsStore(Path.Combine(_tempDir, "settings.json"));
        using var exporter = new SettingsAutoExporter(store, new MemoryCredentialStore(), TimeSpan.FromHours(1));
        var exportPath = Path.Combine(_tempDir, "auto.json");

        store.Save(new AppSettings { AutoExport = new SettingsAutoExport { Enabled = false, FilePath = exportPath } });
        exporter.Flush();

        Assert.False(File.Exists(exportPath));
        Assert.Null(exporter.LastStatus);
    }

    [Fact]
    public void Encrypted_automatic_export_uses_the_saved_password_and_never_falls_back_to_plain()
    {
        var store = new SettingsStore(Path.Combine(_tempDir, "settings.json"));
        var credentials = new MemoryCredentialStore();
        using var exporter = new SettingsAutoExporter(store, credentials, TimeSpan.FromHours(1));
        var exportPath = Path.Combine(_tempDir, "auto.json");
        var settings = SampleSettings();
        settings.AutoExport = new SettingsAutoExport { Enabled = true, FilePath = exportPath, Encrypt = true };

        var status = exporter.ExportNow(settings);
        Assert.False(status!.Succeeded);
        Assert.False(File.Exists(exportPath));

        credentials.Set(SettingsTransfer.AutoExportPasswordKey, "s3cret");
        status = exporter.ExportNow(settings);
        Assert.True(status!.Succeeded);
        Assert.True(SettingsTransfer.ReadInfo(exportPath).IsEncrypted);
        Assert.Single(SettingsTransfer.Import(exportPath, "s3cret").Settings.Sources);
    }

    [Fact]
    public void Disposing_writes_an_export_still_waiting()
    {
        var store = new SettingsStore(Path.Combine(_tempDir, "settings.json"));
        var exporter = new SettingsAutoExporter(store, new MemoryCredentialStore(), TimeSpan.FromHours(1));
        var exportPath = Path.Combine(_tempDir, "auto.json");

        store.Save(new AppSettings { AutoExport = new SettingsAutoExport { Enabled = true, FilePath = exportPath } });
        Assert.False(File.Exists(exportPath));

        exporter.Dispose();
        Assert.True(File.Exists(exportPath));
    }

    private const string ProfileKey = @"C:\repo\Web\Properties\PublishProfiles\Prod.pubxml";

    /// <summary>A store holding a profile password, a script password, an S3 secret and two local-only secrets.</summary>
    private static (AppSettings Settings, MemoryCredentialStore Store) SettingsWithSavedPasswords()
    {
        var settings = SampleSettings();
        var s3 = new S3BackupTarget { Name = "Offsite", Bucket = "b" };
        settings.RemoteBackupTargets.Add(s3);
        var script = settings.GetScriptTargets(@"C:\repo\Web\Web.csproj").Single();

        var store = new MemoryCredentialStore();
        store.Set(ProfileKey, "profile-pw");
        store.Set(script.CredentialKey, "script-pw");
        store.Set(s3.SecretCredentialKey, "s3-secret");
        store.Set("mcp-token", "local token");
        store.Set(SettingsTransfer.AutoExportPasswordKey, "export-pw");
        return (settings, store);
    }

    [Fact]
    public void Saved_passwords_travel_only_inside_an_encrypted_export()
    {
        var (settings, store) = SettingsWithSavedPasswords();
        var secrets = SettingsTransfer.CollectSecrets(settings, store);

        // This machine's own secrets are never collected.
        Assert.Equal(3, secrets.Count);
        Assert.DoesNotContain("mcp-token", secrets.Keys);
        Assert.DoesNotContain(SettingsTransfer.AutoExportPasswordKey, secrets.Keys);

        var path = Path.Combine(_tempDir, "export.json");
        Assert.Throws<ArgumentException>(() => SettingsTransfer.Export(settings, path, password: null, secrets));
        Assert.False(File.Exists(path));

        SettingsTransfer.Export(settings, path, "pw", secrets);
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("profile-pw", text);
        Assert.DoesNotContain("s3-secret", text);

        var imported = SettingsTransfer.Import(path, "pw");
        Assert.Equal("profile-pw", imported.Secrets[ProfileKey]);
        Assert.Equal(3, imported.Secrets.Count);
    }

    [Fact]
    public void Import_drops_secrets_for_keys_the_settings_do_not_use()
    {
        var (settings, store) = SettingsWithSavedPasswords();
        var planted = new Dictionary<string, string>(SettingsTransfer.CollectSecrets(settings, store))
        {
            ["mcp-token"] = "attacker token",
            [SettingsTransfer.AutoExportPasswordKey] = "x",
        };
        var path = Path.Combine(_tempDir, "export.json");
        SettingsTransfer.Export(settings, path, "pw", planted);

        var imported = SettingsTransfer.Import(path, "pw");

        Assert.Equal(3, imported.Secrets.Count);
        Assert.DoesNotContain("mcp-token", imported.Secrets.Keys);
        Assert.DoesNotContain(SettingsTransfer.AutoExportPasswordKey, imported.Secrets.Keys);
    }

    [Fact]
    public void Encrypted_automatic_export_includes_saved_passwords_and_notices_when_one_changes()
    {
        var (settings, store) = SettingsWithSavedPasswords();
        var settingsStore = new SettingsStore(Path.Combine(_tempDir, "settings.json"));
        using var exporter = new SettingsAutoExporter(settingsStore, store, TimeSpan.FromHours(1));
        var exportPath = Path.Combine(_tempDir, "auto.json");
        settings.AutoExport = new SettingsAutoExport { Enabled = true, FilePath = exportPath, Encrypt = true };

        settingsStore.Save(settings);
        exporter.Flush();
        Assert.Equal("profile-pw", SettingsTransfer.Import(exportPath, "export-pw").Secrets[ProfileKey]);

        // A deploy saves a new password, then the settings: the export follows even though the settings didn't change.
        store.Set(ProfileKey, "new-pw");
        settingsStore.Save(settings);
        exporter.Flush();
        Assert.Equal("new-pw", SettingsTransfer.Import(exportPath, "export-pw").Secrets[ProfileKey]);

        settings.AutoExport.IncludeSecrets = false;
        exporter.ExportNow(settings);
        Assert.Empty(SettingsTransfer.Import(exportPath, "export-pw").Secrets);
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
