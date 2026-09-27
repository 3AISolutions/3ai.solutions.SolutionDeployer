using SolutionDeployer.Core.Backup;
using SolutionDeployer.Core.Models;
using SolutionDeployer.Core.Publishing;

namespace SolutionDeployer.Core.Tests;

public sealed class DeploymentRunnerTests
{
    [Fact]
    public async Task Parallel_run_never_publishes_the_same_project_concurrently()
    {
        var engine = new TrackingEngine();
        var runner = new DeploymentRunner(new PublishEngineFactory([engine]), new NoBackupService());

        var web = Project("Web");
        var api = Project("Api");
        var jobs = new[]
        {
            Job(web, "Staging"), Job(web, "Production"), Job(web, "Dev"),
            Job(api, "Staging"), Job(api, "Production"),
        };

        var results = await runner.RunAsync(
            jobs, new DeploymentRunOptions { RunInParallel = true, MaxParallelism = 4 }, _ => { }, _ => { });

        Assert.Equal(jobs.Length, results.Count);
        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(jobs.Select(j => j.Id), results.Select(r => r.JobId));
        Assert.Equal(1, engine.MaxConcurrentPerProject);
        Assert.Equal(2, engine.MaxConcurrentOverall);
    }

    [Fact]
    public async Task Script_without_a_backup_says_so_when_backups_are_on()
    {
        var engine = new TrackingEngine(PublishEngineKind.Script);
        var runner = new DeploymentRunner(new PublishEngineFactory([engine]), new NoBackupService());
        var job = new PublishJob
        {
            Project = Project("Service"),
            Script = new ScriptTarget { Name = "deploy", ScriptPath = "deploy.ps1" },
            Engine = PublishEngineKind.Script,
        };
        var output = new List<OutputLine>();

        var results = await runner.RunAsync(
            [job], new DeploymentRunOptions { BackupBeforePublish = true }, o => output.Add(o.Line), _ => { });

        Assert.True(results.Single().IsSuccess);
        Assert.Contains(output, l => l.Text.StartsWith("[backup] Skipped") && l.Text.Contains("no backup set up"));
    }

    private static DeploymentProject Project(string name) => new()
    {
        Name = name,
        ProjectPath = Path.Combine(Path.GetTempPath(), name, name + ".csproj"),
    };

    private static PublishJob Job(DeploymentProject project, string profile) => new()
    {
        Project = project,
        Profile = new PublishProfile
        {
            Name = profile,
            FilePath = Path.Combine(project.ProjectDirectory, profile + ".pubxml"),
            Format = PublishProfileFormat.PubXml,
        },
        Engine = PublishEngineKind.Dotnet,
    };

    private sealed class TrackingEngine(PublishEngineKind kind = PublishEngineKind.Dotnet) : IPublishEngine
    {
        private readonly Dictionary<string, int> _running = new();
        private int _overall;

        public int MaxConcurrentPerProject { get; private set; }
        public int MaxConcurrentOverall { get; private set; }

        public PublishEngineKind Kind => kind;

        public bool IsAvailable(out string? unavailableReason)
        {
            unavailableReason = null;
            return true;
        }

        public async Task<PublishResult> PublishAsync(
            PublishJob job, Action<OutputLine> onOutput, CancellationToken cancellationToken = default)
        {
            var key = job.Project.ProjectPath;
            lock (_running)
            {
                _running[key] = _running.GetValueOrDefault(key) + 1;
                _overall++;
                MaxConcurrentPerProject = Math.Max(MaxConcurrentPerProject, _running[key]);
                MaxConcurrentOverall = Math.Max(MaxConcurrentOverall, _overall);
            }

            await Task.Delay(50, cancellationToken);

            lock (_running)
            {
                _running[key]--;
                _overall--;
            }

            return new PublishResult { JobId = job.Id, DisplayName = job.DisplayName, Status = PublishStatus.Succeeded };
        }
    }

    private sealed class NoBackupService : IBackupService
    {
        public bool CanBackUp(BackupOwner owner, out string? reason)
        {
            reason = "not supported";
            return false;
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
