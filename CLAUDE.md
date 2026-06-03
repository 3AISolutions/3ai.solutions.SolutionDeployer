# CLAUDE.md

Guidance for working in this repository.

## What this is

Cross-platform desktop app to publish .NET solutions from their publish profiles. .NET 10.

## Architecture

- **`src/SolutionDeployer.Core`** — all logic, no UI dependency:
  - `Solutions/SolutionParser` parses `.sln`/`.slnx` via `Microsoft.VisualStudio.SolutionPersistence`.
  - `Profiles/ProfileDiscovery` scans `Properties/PublishProfiles` for `.pubxml` / `.PublishSettings`.
  - `Publishing/` — `IPublishEngine` with `DotnetPublishEngine` and `MsBuildPublishEngine`
    (the latter locates `msbuild.exe` via `MsBuildLocator`/vswhere, Windows only). `ProcessRunner`
    streams output; `DeploymentRunner` runs a batch of `PublishJob`s (sequential or parallel).
  - `Configuration/SettingsStore` persists `AppSettings` as JSON. **Never persist passwords.**
- **`src/SolutionDeployer.App`** — Avalonia MVVM (CommunityToolkit.Mvvm). DI is wired in
  `App.axaml.cs`. `Services/UpdateService` wraps Velopack. `Program.cs` calls `VelopackApp.Build().Run()`
  first — keep it first.
- **`tests/SolutionDeployer.Core.Tests`** — xUnit. Fixtures under `Fixtures/` are copied to output;
  project/solution files need **explicit** `<None Include>` (default globs exclude them).

## Conventions

- The MVVM toolkit source generators require `ImplicitUsings` (enabled in the App csproj) — without
  `System.Threading.Tasks` in scope, `[RelayCommand]` async methods fail to generate.
- Avalonia compiled bindings are on; every `DataTemplate` needs `x:DataType`.
- Secrets: passwords flow through `PublishCredentials` only, are redacted in logged command lines
  (`PublishArguments`), and are never serialized.

## Build / test / run

```bash
dotnet build 3ai.solutions.SolutionDeployer.slnx
dotnet test  3ai.solutions.SolutionDeployer.slnx
dotnet run --project src/SolutionDeployer.App
```

## Releasing

Tag `vX.Y.Z` → `.github/workflows/release.yml` builds self-contained per-OS, packs with `vpk`, and
uploads to the GitHub Release that Velopack's `GithubSource` reads.
