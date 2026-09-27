using SolutionDeployer.Core.Backup;
using SolutionDeployer.Core.Models;
using SolutionDeployer.Core.Publishing;

namespace SolutionDeployer.Core.Tests;

public sealed class DirectDeployTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-direct-" + Guid.NewGuid().ToString("N"));

    public DirectDeployTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ---- DeploymentRunner ----------------------------------------------------

    [Fact]
    public async Task Prepared_build_is_deployed_instead_of_publishing_again()
    {
        var engine = new CountingEngine();
        var backup = new PreparingBackupService(deploySucceeds: true);
        var runner = new DeploymentRunner(new PublishEngineFactory([engine]), backup);

        var results = await runner.RunAsync([Job()], new DeploymentRunOptions { BackupBeforePublish = true }, _ => { }, _ => { });

        Assert.True(results.Single().IsSuccess);
        Assert.Equal(0, engine.Publishes);
        Assert.Equal(1, backup.Deploys);
        Assert.True(backup.Disposed);
        Assert.Contains(results.Single().Steps, s => s.Step == "deploy");
    }

    [Fact]
    public async Task Up_to_date_server_needs_no_publish()
    {
        var engine = new CountingEngine();
        var backup = new PreparingBackupService(deploySucceeds: true, upToDate: true);
        var runner = new DeploymentRunner(new PublishEngineFactory([engine]), backup);

        var results = await runner.RunAsync([Job()], new DeploymentRunOptions { BackupBeforePublish = true }, _ => { }, _ => { });

        Assert.True(results.Single().IsSuccess);
        Assert.Equal(0, engine.Publishes);
        Assert.Equal(0, backup.Deploys);
        Assert.True(backup.Disposed);
    }

    [Fact]
    public async Task Failed_direct_deploy_falls_back_to_the_engine()
    {
        var engine = new CountingEngine();
        var backup = new PreparingBackupService(deploySucceeds: false);
        var runner = new DeploymentRunner(new PublishEngineFactory([engine]), backup);
        var output = new List<OutputLine>();

        var results = await runner.RunAsync(
            [Job()], new DeploymentRunOptions { BackupBeforePublish = true }, o => { lock (output) output.Add(o.Line); }, _ => { });

        Assert.True(results.Single().IsSuccess);
        Assert.Equal(1, backup.Deploys);
        Assert.Equal(1, engine.Publishes);
        Assert.Contains(output, l => l.Text.Contains("publishing the usual way instead"));
        Assert.Equal(["deploy", "dotnet publish"], results.Single().Steps.Select(s => s.Step));
    }

    [Fact]
    public async Task Build_once_off_publishes_through_the_engine()
    {
        var engine = new CountingEngine();
        var backup = new PreparingBackupService(deploySucceeds: true);
        var runner = new DeploymentRunner(new PublishEngineFactory([engine]), backup);

        await runner.RunAsync(
            [Job()], new DeploymentRunOptions { BackupBeforePublish = true, DeployBackupPreview = false }, _ => { }, _ => { });

        Assert.Equal(1, engine.Publishes);
        Assert.Equal(0, backup.Deploys);
        Assert.True(backup.Disposed);
    }

    [Fact]
    public async Task Each_job_logs_and_reports_its_timing()
    {
        var runner = new DeploymentRunner(new PublishEngineFactory([new CountingEngine()]), new PreparingBackupService(true));
        var output = new List<OutputLine>();

        var results = await runner.RunAsync([Job()], new DeploymentRunOptions(), o => output.Add(o.Line), _ => { });

        var result = results.Single();
        Assert.Equal(["dotnet publish"], result.Steps.Select(s => s.Step));
        Assert.True(result.Duration >= result.Steps[0].Duration);
        Assert.Contains(output, l => l.Text.StartsWith("[timing] ") && l.Text.Contains("dotnet publish"));
    }

    // ---- DirectDeployEligibility --------------------------------------------

    [Fact]
    public void Plain_web_deploy_profile_can_be_deployed_directly()
    {
        var job = JobWithFiles(ProfileXml("<SkipExtraFilesOnServer>True</SkipExtraFilesOnServer>"));

        Assert.True(DirectDeployEligibility.IsEligible(job, out var reason), reason);
    }

    [Fact]
    public void Deploy_time_parameters_need_msbuild()
    {
        var job = JobWithFiles(ProfileXml(items:
            "<MSDeployParameterValue Include=\"DefaultConnection-Web.config Connection String\"><ParameterValue>x</ParameterValue></MSDeployParameterValue>"));

        Assert.False(DirectDeployEligibility.IsEligible(job, out var reason));
        Assert.Contains("MSDeployParameterValue", reason);
    }

    [Fact]
    public void Skip_rules_in_a_wpp_targets_file_need_msbuild()
    {
        var job = JobWithFiles(ProfileXml());
        File.WriteAllText(Path.Combine(_dir, "Web.wpp.targets"),
            "<Project><ItemGroup><MsDeploySkipRules Include=\"SkipLogs\" /></ItemGroup></Project>");

        Assert.False(DirectDeployEligibility.IsEligible(job, out var reason));
        Assert.Contains("MsDeploySkipRules", reason);
    }

    [Fact]
    public void Parameters_xml_needs_msbuild()
    {
        var job = JobWithFiles(ProfileXml());
        File.WriteAllText(Path.Combine(_dir, "Parameters.xml"), "<parameters />");

        Assert.False(DirectDeployEligibility.IsEligible(job, out _));
    }

    [Fact]
    public void Database_publishing_needs_msbuild()
    {
        var job = JobWithFiles(ProfileXml(
            "<PublishDatabaseSettings><Objects><ObjectGroup Name=\"Db\" Order=\"1\" Enabled=\"True\" /></Objects></PublishDatabaseSettings>"));

        Assert.False(DirectDeployEligibility.IsEligible(job, out var reason));
        Assert.Contains("databases", reason);
    }

    [Fact]
    public void Iis_settings_need_msbuild()
    {
        var job = JobWithFiles(ProfileXml(), new Dictionary<string, string> { ["IncludeIisSettings"] = "true" });

        Assert.False(DirectDeployEligibility.IsEligible(job, out _));
    }

    // ---- StepTimings ---------------------------------------------------------

    [Theory]
    [InlineData(0.25, "250ms")]
    [InlineData(12.34, "12.3s")]
    [InlineData(125, "2m 05s")]
    [InlineData(3780, "1h 03m")]
    public void Durations_read_naturally(double seconds, string expected) =>
        Assert.Equal(expected, StepTimings.Format(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Timing_text_lists_each_step()
    {
        var text = StepTimings.Describe(
            TimeSpan.FromSeconds(90),
            [new StepTiming("preview build", TimeSpan.FromSeconds(60)), new StepTiming("deploy", TimeSpan.FromSeconds(30))]);

        Assert.Equal("1m 30s total — preview build 1m 00s · deploy 30.0s", text);
    }

    // ---- Helpers -------------------------------------------------------------

    private static string ProfileXml(string properties = "", string items = "") =>
        "<Project><PropertyGroup><WebPublishMethod>MSDeploy</WebPublishMethod>" + properties +
        "</PropertyGroup><ItemGroup>" + items + "</ItemGroup></Project>";

    private PublishJob JobWithFiles(string profileXml, Dictionary<string, string>? properties = null)
    {
        var profilePath = Path.Combine(_dir, "Prod.pubxml");
        File.WriteAllText(profilePath, profileXml);
        var projectPath = Path.Combine(_dir, "Web.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");

        return new PublishJob
        {
            Project = new DeploymentProject { Name = "Web", ProjectPath = projectPath },
            Profile = new PublishProfile
            {
                Name = "Prod",
                FilePath = profilePath,
                Format = PublishProfileFormat.PubXml,
                WebPublishMethod = "MSDeploy",
                Properties = new Dictionary<string, string>(properties ?? [], StringComparer.OrdinalIgnoreCase),
            },
            Engine = PublishEngineKind.Dotnet,
        };
    }

    private PublishJob Job() => new()
    {
        Project = new DeploymentProject { Name = "Web", ProjectPath = Path.Combine(_dir, "Web.csproj") },
        Profile = new PublishProfile
        {
            Name = "Prod",
            FilePath = Path.Combine(_dir, "Prod.pubxml"),
            Format = PublishProfileFormat.PubXml,
            WebPublishMethod = "MSDeploy",
        },
        Engine = PublishEngineKind.Dotnet,
    };

    private sealed class CountingEngine : IPublishEngine
    {
        private int _publishes;

        public int Publishes => _publishes;

        public PublishEngineKind Kind => PublishEngineKind.Dotnet;

        public bool IsAvailable(out string? unavailableReason)
        {
            unavailableReason = null;
            return true;
        }

        public Task<PublishResult> PublishAsync(PublishJob job, Action<OutputLine> onOutput, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _publishes);
            return Task.FromResult(new PublishResult { JobId = job.Id, DisplayName = job.DisplayName, Status = PublishStatus.Succeeded });
        }
    }

    /// <summary>Backs up nothing, but hands back a prepared build like a Web Deploy backup does.</summary>
    private sealed class PreparingBackupService(bool deploySucceeds, bool upToDate = false) : IBackupService
    {
        public int Deploys { get; private set; }

        public bool Disposed { get; private set; }

        public bool CanBackUp(BackupOwner owner, out string? reason)
        {
            reason = null;
            return true;
        }

        public Task<PublishBackup> BackUpForPublishAsync(
            PublishJob job, Action<OutputLine> onOutput, StepTimings? timings = null, CancellationToken cancellationToken = default)
        {
            var deployment = new PreparedDeployment(
                upToDate,
                (_, _) =>
                {
                    Deploys++;
                    return Task.FromResult(new PublishResult
                    {
                        JobId = job.Id,
                        DisplayName = job.DisplayName,
                        Status = deploySucceeds ? PublishStatus.Succeeded : PublishStatus.Failed,
                        ErrorMessage = deploySucceeds ? null : "msdeploy exited with code -1.",
                    });
                },
                () => Disposed = true);
            return Task.FromResult(new PublishBackup(null, deployment));
        }

        public Task<DeploymentBackup?> BackUpAsync(PublishJob job, Action<OutputLine> onOutput, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DeploymentBackup>> ListAsync(BackupOwner owner, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RestoreAsync(
            DeploymentBackup backup, BackupOwner owner, PublishCredentials credentials,
            bool allowUntrustedCertificate, Action<OutputLine> onOutput, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DeploymentBackup>> GetDeletionSetAsync(DeploymentBackup backup, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(DeploymentBackup backup, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
