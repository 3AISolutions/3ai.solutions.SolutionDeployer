# IconGen

Standalone tool that rasterises the app icon. **Not** part of the solution.

`src/SolutionDeployer.App/Assets/app-icon.svg` is the source of truth. This tool renders it (via
Svg.Skia) into the platform assets the app and installers consume:

- `app-icon.ico` — Windows exe icon (`<ApplicationIcon>`), installer icon, and the runtime window icon.
- `app-icon.png` — 256×256, Linux AppImage icon.
- `app-icon.icns` — macOS bundle icon.

Regenerate after editing the SVG:

```bash
dotnet run --project tools/IconGen
```

The generated `.ico` / `.png` / `.icns` are committed so CI/builds don't need this tool.
