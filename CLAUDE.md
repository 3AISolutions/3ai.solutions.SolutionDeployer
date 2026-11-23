# CLAUDE.md

Guidance for working in this repository.

## What this is

Cross-platform desktop app to publish .NET solutions from their publish profiles. .NET 11.

## Architecture

- **`src/SolutionDeployer.Core`** — all logic, no UI dependency:
  - `Solutions/SolutionParser` parses `.sln`/`.slnx` via `Microsoft.VisualStudio.SolutionPersistence`.
  - `Solutions/SourceLoader` resolves a `DeploymentSource` (solution **or** standalone project) into
    its projects; solutions are filtered to projects that have ≥1 profile, `Projects/ProjectLoader`
    loads a single project unfiltered.
  - `Profiles/ProfileDiscovery` scans `Properties/PublishProfiles` for `.pubxml` / `.PublishSettings`.
  - `Publishing/` — `IPublishEngine` with `DotnetPublishEngine`, `MsBuildPublishEngine`
    (the latter locates `msbuild.exe` via `MsBuildLocator`/vswhere, Windows only) and
    `ScriptPublishEngine` (runs a `ScriptTarget`: interpreter inferred by `ScriptInterpreters`, args
    split by `CommandLine`, `SD_*` context env). `ProcessRunner` streams output; `DeploymentRunner`
    runs a batch of `PublishJob`s (each a profile **or** a script; sequential, or parallel *across unrelated builds* — jobs whose
    builds touch a common project, itself or a shared `ProjectReference` (`Projects/ProjectGraph`), run one
    after another, since concurrent builds of one project collide in obj/bin). Projects that are, or
    reference, classic non-SDK projects are msbuild-only (`ProjectFormat.RequiresMsBuild`): dotnet can't
    resolve a classic project's NuGet packages.
  - `Backup/BackupService` snapshots a `BackupOwner` (profile or script) before publishing. MSDeploy
    profiles get a *partial* snapshot: a preview FileSystem publish to temp, `msdeploy -whatif
    -useCheckSum` (parsed by `MsDeployChangeSet`), then only the updated/deleted paths are pulled via a
    manifest; restore removes added paths and rolls back every newer snapshot first. Scripts get a full
    snapshot of their configured folder, or (`ScriptBackupKind.WhatIf`) are first run with `-WhatIf` and
    must print `SD-WHATIF-TARGET: <computerName>` + msdeploy change lines + `Total changes: N` per target
    (`ScriptWhatIfReport`); targets reporting identical changes are saved once and restored to all. Any other
    backup failure only warns, but a `-WhatIf` run that exits non-zero (`ScriptWhatIfFailedException`) fails the
    job without running the script for real. A target
    reported as `ssh://user@host` (absolute paths) is saved/restored with tar over `ssh`/`scp` instead of msdeploy.
  - Build once: that MSDeploy preview is kept as a `PreparedDeployment` and `DeploymentRunner` syncs it to the
    server (msdeploy, same content rules as the what-if) instead of publishing — i.e. building — again. Profiles
    using MSBuild-only Web Deploy features (parameters, skip rules, DB publishing …; `DirectDeployEligibility`)
    publish as before, and a failed sync falls back to the engine. Each job's steps are timed (`StepTimings` →
    `PublishResult.Steps`, logged as `[timing]`).
  - Because partial snapshots chain, deletion only ever removes an owner's *oldest* snapshots
    (`BackupRetention`): deleting one also deletes every older partial one. `BackupCleanupService`
    inventories all stores and classifies owners (active / old destination / orphaned / unknown) via
    `DeploymentBackup.OwnerId`, falling back to profile paths remembered in settings for older snapshots.
  - `MsBuildPublishEngine` publishes classic (non-SDK) web projects via `DeployOnBuild` through their
    solution (`/t:<SolutionTargetName>`) — `/t:Publish` is ClickOnce there and silently skips them.
  - `Configuration/SettingsStore` persists `AppSettings` as JSON. **Never persist passwords.**
  - `AppSettings.TargetTags`: user tags per deploy target, keyed like `RememberedUserNames` (profile file path or
    `ScriptTarget.CredentialKey`); `NormalizeTags` trims and dedupes case-insensitively, keeping the user's casing.
  - `Configuration/SettingsTransfer` exports/imports `AppSettings` (optionally AES-256-GCM, PBKDF2 key from a
    password); machine-local fields (window, sign-in entry, MCP, auto-export) are left out and kept on restore
    (`KeepLocal`). Saved passwords (`SecretKeys`: the profile/script/S3 keys the settings refer to, never the MCP
    token or the export password) go only inside an *encrypted* export, and import drops any for other keys.
    `SettingsAutoExporter` listens to `SettingsStore.Saved` and rewrites the user's export file (debounced, only
    when the settings or saved passwords changed); its password lives in `ICredentialStore` under
    `SettingsTransfer.AutoExportPasswordKey`. A restore writes the passwords to `ICredentialStore` and first saves
    the current settings (no passwords) as `settings.before-restore.json` beside `settings.json`.
- **`src/SolutionDeployer.App`** — Avalonia MVVM (CommunityToolkit.Mvvm). DI is wired in
  `App.axaml.cs`. `Services/UpdateService` wraps Velopack. `Program.cs` calls `VelopackApp.Build().Run()`
  first — keep it first.
  The app can outlive its window: `AppSettings.CloseToTray` hides it to a tray icon on close (`ShutdownMode` is
  explicit; quit from the tray), `StartWithOperatingSystem` registers a sign-in entry (`AutoStartService`: Run key /
  XDG autostart / LaunchAgent) that launches with `--background`, and `SingleInstance` (mutex + named pipe) makes a
  second launch show the running copy's window instead.
  The main window's top is a menu bar (File/Deploy/Tools/Help; shortcuts are `Window.KeyBindings`, `InputGesture`
  only displays them) over a slim icon toolbar. Icons are `StreamGeometry` resources in `App.axaml` drawn by
  `PathIcon`, so they take the theme's foreground — no icon package. `OptionsWindow` binds straight to
  `MainWindowViewModel` (options save as they change) and returns an `OptionsFollowUp` so the backup windows,
  which the main window owns, open after it closes. File › Exit calls `Shutdown()` like the tray's Quit, so it quits
  even with close-to-tray.
  Rows (`ITaggedTarget`) show tag chips; "Edit tags…" in the row menu opens `TagEditorWindow` from code-behind. The list's
  tag filter (`TagFilterOptions`) shows targets with **all** ticked tags AND the text filter; Select All ticks visible rows only.
- **`src/SolutionDeployer.Mcp`** — MCP server (official `ModelContextProtocol.AspNetCore` SDK, Kestrel in-process) on
  `http://localhost:47821/mcp`, loopback only, `Authorization: Bearer` token required (token kept in
  `ICredentialStore`, never settings). `DeployerTools` call `IDeployerHost`, which the app implements in
  `Services/McpDeployerBridge` over `MainWindowViewModel` (on the UI thread) — an MCP deploy *is* the window's
  deploy, minus the confirm/summary dialogs. Deploys return a `DeploymentRun` at once; clients poll `get_run`.
  `list_targets` returns each target's tags and takes `tags` (target must have all of them, plus `filter`).
  Passwords never travel over MCP: targets must have theirs saved in the app.
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
