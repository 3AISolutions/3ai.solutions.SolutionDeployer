using SolutionDeployer.Core.Configuration;
using SolutionDeployer.Core.Models;
using SolutionDeployer.Core.Publishing;

namespace SolutionDeployer.Core.Tests;

public sealed class ScriptDeploymentTests
{
    [Theory]
    [InlineData("a b c", new[] { "a", "b", "c" })]
    [InlineData("-Server \"my host\" -Name app", new[] { "-Server", "my host", "-Name", "app" })]
    [InlineData("'single quoted' plain", new[] { "single quoted", "plain" })]
    [InlineData("   ", new string[0])]
    public void Tokenize_splits_quote_aware(string input, string[] expected)
    {
        Assert.Equal(expected, CommandLine.Tokenize(input));
    }

    [Fact]
    public void Tokenize_null_is_empty()
    {
        Assert.Empty(CommandLine.Tokenize(null));
    }

    [Theory]
    [InlineData("deploy.ps1")]
    [InlineData("deploy.sh")]
    [InlineData("deploy.bash")]
    [InlineData("deploy.cmd")]
    [InlineData("deploy.bat")]
    public void Supported_extensions_are_recognised(string file)
    {
        Assert.True(ScriptInterpreters.IsSupported(file));
    }

    [Fact]
    public void Unsupported_extension_is_rejected_with_reason()
    {
        Assert.False(ScriptInterpreters.IsSupported("deploy.py"));
        Assert.False(ScriptInterpreters.TryResolve("deploy.py", out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void ScriptTarget_resolves_relative_path_against_project_dir()
    {
        var projectDir = OperatingSystem.IsWindows() ? @"C:\repo\App" : "/repo/App";
        var target = new ScriptTarget { ScriptPath = "scripts/deploy.sh" };

        var resolved = target.ResolveScriptPath(projectDir);

        Assert.True(Path.IsPathRooted(resolved));
        Assert.EndsWith("deploy.sh", resolved);
        Assert.Equal(projectDir, target.ResolveWorkingDirectory(projectDir));
    }

    [Fact]
    public void MakeStorablePath_prefers_relative_under_project()
    {
        var projectDir = OperatingSystem.IsWindows() ? @"C:\repo\App" : "/repo/App";
        var inside = Path.Combine(projectDir, "scripts", "deploy.sh");
        var outside = OperatingSystem.IsWindows() ? @"D:\other\deploy.sh" : "/other/deploy.sh";

        Assert.Equal(Path.Combine("scripts", "deploy.sh"), ScriptTarget.MakeStorablePath(inside, projectDir));
        Assert.Equal(Path.GetFullPath(outside), ScriptTarget.MakeStorablePath(outside, projectDir));
    }

    [Fact]
    public async Task Script_engine_runs_script_passes_context_and_reports_exit_code()
    {
        if (!OperatingSystem.IsWindows())
            return; // Uses a .cmd for a dependency-free, deterministic interpreter.

        var dir = Path.Combine(Path.GetTempPath(), $"sd_script_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "ok.cmd"),
                "@echo project=%SD_PROJECT_NAME% cfg=%SD_CONFIGURATION% extra=%EXTRA%\r\n@exit /b 0\r\n");
            await File.WriteAllTextAsync(Path.Combine(dir, "fail.cmd"), "@exit /b 3\r\n");

            var project = new DeploymentProject { Name = "App", ProjectPath = Path.Combine(dir, "App.csproj") };
            var engine = new ScriptPublishEngine(new ProcessRunner());
            var output = new List<OutputLine>();

            var okJob = new PublishJob
            {
                Project = project,
                Engine = PublishEngineKind.Script,
                Configuration = "Release",
                Script = new ScriptTarget
                {
                    Name = "ok",
                    ScriptPath = "ok.cmd",
                    Environment = new Dictionary<string, string> { ["EXTRA"] = "hi" },
                },
            };

            var ok = await engine.PublishAsync(okJob, output.Add);

            Assert.Equal(PublishStatus.Succeeded, ok.Status);
            Assert.Equal(0, ok.ExitCode);
            var text = string.Join("\n", output.Select(o => o.Text));
            Assert.Contains("project=App", text);
            Assert.Contains("cfg=Release", text);
            Assert.Contains("extra=hi", text);

            var failJob = new PublishJob
            {
                Project = project,
                Engine = PublishEngineKind.Script,
                Script = new ScriptTarget { Name = "fail", ScriptPath = "fail.cmd" },
            };

            var fail = await engine.PublishAsync(failJob, _ => { });
            Assert.Equal(PublishStatus.Failed, fail.Status);
            Assert.Equal(3, fail.ExitCode);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Script_engine_fails_clearly_when_file_missing()
    {
        var project = new DeploymentProject
        {
            Name = "App",
            ProjectPath = Path.Combine(Path.GetTempPath(), "nope", "App.csproj"),
        };
        var engine = new ScriptPublishEngine(new ProcessRunner());
        var job = new PublishJob
        {
            Project = project,
            Engine = PublishEngineKind.Script,
            Script = new ScriptTarget { Name = "x", ScriptPath = "missing.ps1" },
        };

        var result = await engine.PublishAsync(job, _ => { });

        Assert.Equal(PublishStatus.Failed, result.Status);
        Assert.Contains("not found", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Settings_round_trip_script_targets_and_script_selection()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"sd_settings_{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(temp);
            var settings = store.Load();
            var projectPath = OperatingSystem.IsWindows() ? @"C:\repo\App\App.csproj" : "/repo/App/App.csproj";

            settings.SetScriptTargets(projectPath,
            [
                new ScriptTarget
                {
                    Id = "script-1",
                    Name = "Deploy linux",
                    ScriptPath = "deploy_linux.ps1",
                    Arguments = "-Server host -ServiceName app.service",
                    Environment = new Dictionary<string, string> { ["FOO"] = "bar" },
                },
            ]);
            settings.SavedSelections["src"] =
            [
                new SavedProfileSelection { Kind = SelectionKind.Script, Project = "App", ScriptId = "script-1" },
            ];
            store.Save(settings);

            var reloaded = new SettingsStore(temp).Load();
            var script = Assert.Single(reloaded.GetScriptTargets(projectPath));
            Assert.Equal("Deploy linux", script.Name);
            Assert.Equal("-Server host -ServiceName app.service", script.Arguments);
            Assert.Equal("bar", script.Environment["FOO"]);

            var sel = Assert.Single(reloaded.SavedSelections["src"]);
            Assert.Equal(SelectionKind.Script, sel.Kind);
            Assert.Equal("script-1", sel.ScriptId);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
