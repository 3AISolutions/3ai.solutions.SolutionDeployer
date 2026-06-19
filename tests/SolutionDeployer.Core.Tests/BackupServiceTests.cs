using SolutionDeployer.Core.Backup;
using SolutionDeployer.Core.Models;
using SolutionDeployer.Core.Publishing;

namespace SolutionDeployer.Core.Tests;

public sealed class BackupServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _backupRoot;
    private readonly BackupService _service;

    public BackupServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sd_backup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _backupRoot = Path.Combine(_tempDir, "Backups");
        _service = new BackupService(new ProcessRunner(), new MsDeployLocator(), _backupRoot, retention: 2);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private PublishProfile FileSystemProfile(string destination) => new()
    {
        Name = "Prod",
        FilePath = Path.Combine(_tempDir, "Prod.pubxml"),
        Format = PublishProfileFormat.PubXml,
        WebPublishMethod = "FileSystem",
        Properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["WebPublishMethod"] = "FileSystem",
            ["publishUrl"] = destination,
        },
    };

    private PublishJob JobFor(PublishProfile profile) => new()
    {
        Project = new DeploymentProject { Name = "App", ProjectPath = Path.Combine(_tempDir, "App.csproj") },
        Profile = profile,
        Engine = PublishEngineKind.Dotnet,
    };

    [Fact]
    public void CanBackUp_true_for_filesystem_with_destination()
    {
        var profile = FileSystemProfile(Path.Combine(_tempDir, "dest"));
        Assert.True(_service.CanBackUp(profile, _tempDir, out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void CanBackUp_false_for_filesystem_without_destination()
    {
        var profile = new PublishProfile
        {
            Name = "Prod",
            FilePath = Path.Combine(_tempDir, "Prod.pubxml"),
            Format = PublishProfileFormat.PubXml,
            WebPublishMethod = "FileSystem",
        };
        Assert.False(_service.CanBackUp(profile, _tempDir, out var reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void CanBackUp_false_for_unsupported_method()
    {
        var profile = new PublishProfile
        {
            Name = "Ftp",
            FilePath = Path.Combine(_tempDir, "Ftp.pubxml"),
            Format = PublishProfileFormat.PubXml,
            WebPublishMethod = "FTP",
            Properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["WebPublishMethod"] = "FTP" },
        };
        Assert.False(_service.CanBackUp(profile, _tempDir, out var reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public async Task FileSystem_backup_and_restore_round_trip()
    {
        var dest = Path.Combine(_tempDir, "dest");
        Directory.CreateDirectory(Path.Combine(dest, "sub"));
        await File.WriteAllTextAsync(Path.Combine(dest, "index.html"), "v1");
        await File.WriteAllTextAsync(Path.Combine(dest, "sub", "app.js"), "console.log(1)");

        var profile = FileSystemProfile(dest);

        var backup = await _service.BackUpAsync(JobFor(profile), _ => { });
        Assert.NotNull(backup);
        Assert.Equal(BackupKind.FileSystem, backup!.Kind);
        Assert.True(File.Exists(backup.PackagePath));

        // The snapshot is listed for the profile.
        Assert.Single(_service.List(profile, _tempDir));

        // Mutate the deployment: change a file, add a stray one, drop the subfolder.
        await File.WriteAllTextAsync(Path.Combine(dest, "index.html"), "v2-broken");
        await File.WriteAllTextAsync(Path.Combine(dest, "stray.txt"), "remove me");
        Directory.Delete(Path.Combine(dest, "sub"), recursive: true);

        await _service.RestoreAsync(backup, profile, _tempDir, PublishCredentials.None, true, _ => { });

        Assert.Equal("v1", await File.ReadAllTextAsync(Path.Combine(dest, "index.html")));
        Assert.Equal("console.log(1)", await File.ReadAllTextAsync(Path.Combine(dest, "sub", "app.js")));
        Assert.False(File.Exists(Path.Combine(dest, "stray.txt"))); // exact restore removes files added since
    }

    [Fact]
    public async Task BackUp_returns_null_when_nothing_deployed_yet()
    {
        var profile = FileSystemProfile(Path.Combine(_tempDir, "never-deployed"));
        var backup = await _service.BackUpAsync(JobFor(profile), _ => { });
        Assert.Null(backup);
        Assert.Empty(_service.List(profile, _tempDir));
    }

    [Fact]
    public async Task Retention_prunes_oldest_snapshots()
    {
        var dest = Path.Combine(_tempDir, "dest");
        Directory.CreateDirectory(dest);
        await File.WriteAllTextAsync(Path.Combine(dest, "f.txt"), "x");
        var profile = FileSystemProfile(dest);
        var job = JobFor(profile);

        for (var i = 0; i < 4; i++)
            Assert.NotNull(await _service.BackUpAsync(job, _ => { }));

        // retention is 2.
        Assert.Equal(2, _service.List(profile, _tempDir).Count);
    }

    [Fact]
    public async Task Delete_removes_snapshot()
    {
        var dest = Path.Combine(_tempDir, "dest");
        Directory.CreateDirectory(dest);
        await File.WriteAllTextAsync(Path.Combine(dest, "f.txt"), "x");
        var profile = FileSystemProfile(dest);

        var backup = await _service.BackUpAsync(JobFor(profile), _ => { });
        Assert.NotNull(backup);
        Assert.True(_service.Delete(backup!));
        Assert.Empty(_service.List(profile, _tempDir));
    }
}
