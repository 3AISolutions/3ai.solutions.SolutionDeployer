using SolutionDeployer.Core.Backup;
using SolutionDeployer.Core.Configuration;
using SolutionDeployer.Core.Models;
using SolutionDeployer.Core.Publishing;

namespace SolutionDeployer.Core.Tests;

public sealed class ScriptWhatIfTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-whatif-" + Guid.NewGuid().ToString("N"));

    public ScriptWhatIfTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Report_splits_changes_per_target()
    {
        var targets = ScriptWhatIfReport.Parse(
        [
            "Using MSBuild: C:\\msbuild.exe",
            "========== WhatIf profile: WS1-NWT ==========",
            "SD-WHATIF-TARGET: https://ws1:8172/msdeploy.axd?site=api.nwt-online.net",
            @"  Updating file (api.nwt-online.net\bin\App.dll).",
            @"Info: Updating file (api.nwt-online.net\bin\App.dll).",
            "Total changes: 1",
            "SD-WHATIF-TARGET: https://ws2:8172/msdeploy.axd?site=api.nwt-online.net",
            "Total changes: 0",
        ]);

        Assert.Equal(2, targets.Count);
        Assert.Equal("https://ws1:8172/msdeploy.axd?site=api.nwt-online.net", targets[0].ComputerName);
        Assert.Equal([@"api.nwt-online.net\bin\App.dll"], targets[0].Changes.Updated);
        Assert.True(targets[1].Changes.IsEmpty);
    }

    [Fact]
    public void Report_without_a_target_marker_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => ScriptWhatIfReport.Parse(["Total changes: 0"]));
    }

    [Fact]
    public void Report_without_a_total_is_rejected_so_nothing_is_skipped_silently()
    {
        Assert.Throws<InvalidOperationException>(() => ScriptWhatIfReport.Parse(
            ["SD-WHATIF-TARGET: https://ws1:8172/msdeploy.axd", "The Web Deploy step did not run."]));
    }

    private (BackupService Service, PublishJob Job) ScriptBackup(string scriptBody)
    {
        File.WriteAllText(Path.Combine(_dir, "deploy.cmd"), scriptBody);
        var runner = new ProcessRunner();
        var provider = new BackupStoreProvider(
            new SettingsStore(Path.Combine(_dir, "settings.json")), new NullCredentialStore(), Path.Combine(_dir, "Backups"));
        var engines = new PublishEngineFactory([new ScriptPublishEngine(runner)]);
        var service = new BackupService(runner, new MsDeployLocator(), provider, engineFactory: engines);

        var job = new PublishJob
        {
            Project = new DeploymentProject { Name = "App", ProjectPath = Path.Combine(_dir, "App.csproj") },
            Engine = PublishEngineKind.Script,
            Script = new ScriptTarget { Name = "deploy", ScriptPath = "deploy.cmd", BackupKind = ScriptBackupKind.WhatIf },
        };
        return (service, job);
    }

    [Fact]
    public async Task Script_that_changes_nothing_needs_no_snapshot()
    {
        if (!OperatingSystem.IsWindows())
            return; // Uses a .cmd for a dependency-free, deterministic interpreter.

        // Only answers as a WhatIf report when it's actually run with -WhatIf.
        var (service, job) = ScriptBackup(
            "@if not \"%1\"==\"-WhatIf\" exit /b 9\r\n" +
            "@echo SD-WHATIF-TARGET: https://ws1:8172/msdeploy.axd\r\n" +
            "@echo Total changes: 0\r\n");
        var output = new List<OutputLine>();

        var backup = await service.BackUpAsync(job, output.Add);

        Assert.Null(backup);
        Assert.Contains(output, o => o.Text.Contains("no snapshot needed"));
    }

    [Fact]
    public async Task Servers_reporting_the_same_changes_share_one_snapshot_entry()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // WS1 and WS2 run the same application (same change list); WS3 has drifted. Only additions, so no
        // files have to be pulled from a server.
        var (service, job) = ScriptBackup(
            "@echo SD-WHATIF-TARGET: https://ws1:8172/msdeploy.axd?site=api\r\n" +
            "@echo Info: Adding file (api\\bin\\New.dll).\r\n" +
            "@echo Total changes: 1\r\n" +
            "@echo SD-WHATIF-TARGET: https://ws2:8172/msdeploy.axd?site=api\r\n" +
            "@echo Info: Adding file (api/bin/new.dll).\r\n" +
            "@echo Total changes: 1\r\n" +
            "@echo SD-WHATIF-TARGET: https://ws3:8172/msdeploy.axd?site=api\r\n" +
            "@echo Info: Adding file (api\\bin\\Other.dll).\r\n" +
            "@echo Total changes: 1\r\n");

        var backup = await service.BackUpAsync(job, _ => { });

        Assert.NotNull(backup);
        Assert.Equal(BackupKind.ScriptPartial, backup!.Kind);
        Assert.Equal(2, backup.Targets.Count);
        Assert.Equal("https://ws1:8172/msdeploy.axd?site=api", backup.Targets[0].ComputerName);
        Assert.Equal(["https://ws2:8172/msdeploy.axd?site=api"], backup.Targets[0].ReplicaComputerNames);
        Assert.Empty(backup.Targets[1].ReplicaComputerNames);
        Assert.Contains("on 3 servers", backup.DisplayName);
    }

    [Fact]
    public async Task Script_whose_WhatIf_run_fails_fails_the_backup()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var (service, job) = ScriptBackup("@exit /b 3\r\n");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.BackUpAsync(job, _ => { }));
    }
}
