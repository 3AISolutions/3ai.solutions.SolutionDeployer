# 3ai Solution Deployer

A cross-platform (Windows / macOS / Linux) desktop app for publishing .NET solutions from their
publish profiles. Open a `.sln` or `.slnx`, tick any combination of project → profile targets, and
deploy them — sequentially or in parallel — with live build output. Self-updates from GitHub Releases.

## Features

- **Solution parsing** for both `.sln` and `.slnx`, via the official
  `Microsoft.VisualStudio.SolutionPersistence` serializer (the one that backs `dotnet sln`).
- **Profile discovery** — finds `.pubxml` and `.PublishSettings` files under each project's
  `Properties/PublishProfiles`, reading method (MSDeploy / FileSystem / FTP), server URL, IIS app
  path and username for display. `*.pubxml.user` (encrypted secrets) is ignored.
- **Any combination** of project + profile targets selectable per run; tri-state project checkboxes.
- **Two publish engines**, selectable per profile:
  - `dotnet publish` — cross-platform, uses the .NET SDK.
  - `msbuild.exe /t:Publish` — Windows-only (located via vswhere), for classic Web Deploy /
    full-framework targets.
- **Per-target credentials** (username/password) for MSDeploy publishes. Passwords are never written
  to disk and are redacted from the logged command line; usernames can be remembered.
- **Live output console** with per-job tagging and error highlighting; cancel mid-run.
- **Self-update** via [Velopack](https://velopack.io) from GitHub Releases.

## The publish commands

The engines reproduce the standard publish-profile invocations:

```
# dotnet engine
dotnet publish <project> --configuration Release \
  /p:PublishProfile=<profile> /p:UserName=<user> /p:Password=<pw> /p:AllowUntrustedCertificate=true

# msbuild engine (Windows)
msbuild <project> /restore /t:Publish /p:Configuration=Release \
  /p:PublishProfile=<profile> /p:UserName=<user> /p:Password=<pw> /p:AllowUntrustedCertificate=true /v:minimal /m
```

## Project layout

| Path | What |
|------|------|
| `src/SolutionDeployer.Core` | Domain models, solution/profile parsing, publish engines, orchestration. No UI dependency. |
| `src/SolutionDeployer.App`  | Avalonia (MVVM) desktop UI + Velopack self-update. |
| `tests/SolutionDeployer.Core.Tests` | xUnit tests for parsing & profile discovery. |

## Building & running

```bash
dotnet build 3ai.solutions.SolutionDeployer.slnx
dotnet test  3ai.solutions.SolutionDeployer.slnx
dotnet run --project src/SolutionDeployer.App
```

Requires the **.NET 10 SDK**.

## Configuring updates

Set the GitHub repository (`owner/repo`) in the top-right field of the app — it is persisted to
`%AppData%/3ai.SolutionDeployer/settings.json` (or the platform equivalent). The in-app updater only
applies updates to **installed** builds (those produced by the release pipeline below); running from
source is a safe no-op.

## Releasing

Push a tag like `v1.2.3`. The [`release.yml`](.github/workflows/release.yml) workflow builds a
self-contained app on Windows, macOS and Linux, packs each with Velopack's `vpk`, and uploads the
installers + delta packages to the matching GitHub Release. The running app then discovers and
applies them.

## License

Licensed under [Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International](LICENSE)
(CC BY-NC-SA 4.0). You may share and adapt the material for **non-commercial** purposes, with
attribution, and must distribute derivatives under the same license.
