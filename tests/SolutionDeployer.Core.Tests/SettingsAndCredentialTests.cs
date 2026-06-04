using SolutionDeployer.Core.Configuration;
using SolutionDeployer.Core.Models;

namespace SolutionDeployer.Core.Tests;

public sealed class SettingsAndCredentialTests
{
    [Fact]
    public void Settings_round_trip_saved_selections_and_last_solution()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"sd_settings_{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(temp);
            var settings = store.Load();
            settings.LastSolutionPath = @"C:\repo\App.slnx";
            settings.AutoLoadLastSolution = false;
            settings.SavedSelections[@"C:\repo\App.slnx"] =
            [
                new SavedProfileSelection { Project = "WebApp", Profile = "Production", Engine = PublishEngineKind.MsBuild },
            ];
            store.Save(settings);

            var reloaded = new SettingsStore(temp).Load();

            Assert.Equal(@"C:\repo\App.slnx", reloaded.LastSolutionPath);
            Assert.False(reloaded.AutoLoadLastSolution);
            var sel = Assert.Single(reloaded.SavedSelections[@"C:\repo\App.slnx"]);
            Assert.Equal("WebApp", sel.Project);
            Assert.Equal("Production", sel.Profile);
            Assert.Equal(PublishEngineKind.MsBuild, sel.Engine);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void Sources_round_trip_and_dedupe()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"sd_settings_{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(temp);
            var settings = store.Load();

            Assert.True(settings.AddSource(DeploymentSource.Solution(@"C:\repo\App.slnx")));
            Assert.True(settings.AddSource(DeploymentSource.Project(@"C:\repo\Lib\Lib.csproj")));
            Assert.False(settings.AddSource(DeploymentSource.Solution(@"C:\repo\App.slnx"))); // dupe
            store.Save(settings);

            var reloaded = new SettingsStore(temp).Load();
            Assert.Equal(2, reloaded.Sources.Count);
            Assert.Contains(reloaded.Sources, s => s.Kind == SourceKind.Solution && s.Path == @"C:\repo\App.slnx");
            Assert.Contains(reloaded.Sources, s => s.Kind == SourceKind.Project && s.Path == @"C:\repo\Lib\Lib.csproj");
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void MigrateLegacy_seeds_sources_from_last_solution()
    {
        var settings = new AppSettings { LastSolutionPath = @"C:\repo\App.slnx", AutoLoadLastSolution = false };

        settings.MigrateLegacy();

        var source = Assert.Single(settings.Sources);
        Assert.Equal(SourceKind.Solution, source.Kind);
        Assert.Equal(@"C:\repo\App.slnx", source.Path);
        Assert.False(settings.RestoreSourcesOnStartup);
        Assert.Null(settings.LastSolutionPath);

        // Idempotent: running again doesn't duplicate.
        settings.MigrateLegacy();
        Assert.Single(settings.Sources);
    }

    [Fact]
    public void RemoveSource_drops_its_saved_selection()
    {
        var settings = new AppSettings();
        var source = DeploymentSource.Project(@"C:\repo\Lib\Lib.csproj");
        settings.AddSource(source);
        settings.SavedSelections[source.Path] = [new SavedProfileSelection { Project = "Lib", Profile = "Prod" }];

        settings.RemoveSource(source);

        Assert.Empty(settings.Sources);
        Assert.False(settings.SavedSelections.ContainsKey(source.Path));
    }

    [Fact]
    public void Settings_never_serialize_a_password_field()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"sd_settings_{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(temp);
            store.Save(store.Load());
            var json = File.ReadAllText(temp);
            Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void Windows_credential_store_round_trips_secret()
    {
        if (!OperatingSystem.IsWindows())
            return; // DPAPI store only applies on Windows.

        var temp = Path.Combine(Path.GetTempPath(), $"sd_creds_{Guid.NewGuid():N}.dat");
        try
        {
            var store = new WindowsDpapiCredentialStore(temp);
            const string key = @"C:\repo\WebApp\Properties\PublishProfiles\Production.pubxml";

            Assert.Null(store.Get(key));

            store.Set(key, "s3cret!");
            Assert.Equal("s3cret!", store.Get(key));

            // Plaintext must not be visible in the on-disk blob.
            Assert.DoesNotContain("s3cret!", File.ReadAllText(temp));

            // A fresh instance reads the persisted, encrypted value.
            Assert.Equal("s3cret!", new WindowsDpapiCredentialStore(temp).Get(key));

            store.Delete(key);
            Assert.Null(store.Get(key));
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
