using SolutionDeployer.Core.Git;

namespace SolutionDeployer.Core.Tests;

public sealed class GitHistoryTests : IDisposable
{
    private readonly string _tempDir;

    public GitHistoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sd_git_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string WriteProject(string name, params string[] referencePaths)
    {
        var dir = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(dir);
        var refs = string.Join("\n", referencePaths.Select(r => $"    <ProjectReference Include=\"{r}\" />"));
        var xml = $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n{refs}\n  </ItemGroup>\n</Project>\n";
        var path = Path.Combine(dir, $"{name}.csproj");
        File.WriteAllText(path, xml);
        return path;
    }

    [Fact]
    public void ResolveClosure_includes_root_first_then_transitive_refs()
    {
        // App -> Lib -> Core ; App -> Lib (dup), with backslash separators like MSBuild emits.
        var core = WriteProject("Core");
        WriteProject("Lib", @"..\Core\Core.csproj");
        var app = WriteProject("App", @"..\Lib\Lib.csproj", @"..\Core\Core.csproj");

        var closure = ProjectReferenceResolver.ResolveClosure(app);

        Assert.Equal(app, closure[0]); // root first
        Assert.Equal(3, closure.Count); // App, Lib, Core — Core de-duplicated
        Assert.Contains(closure, p => Path.GetFileName(p) == "Lib.csproj");
        Assert.Contains(core, closure);
    }

    [Fact]
    public void ResolveClosure_handles_missing_and_self_only()
    {
        var solo = WriteProject("Solo");
        Assert.Equal([solo], ProjectReferenceResolver.ResolveClosure(solo));

        var missing = Path.Combine(_tempDir, "Nope", "Nope.csproj");
        Assert.Equal([Path.GetFullPath(missing)], ProjectReferenceResolver.ResolveClosure(missing));
    }
}
