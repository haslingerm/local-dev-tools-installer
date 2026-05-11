# Dev Tools Manager

Dev Tools Manager is a desktop app (Avalonia, .NET 10) that installs and manages local developer tools without admin rights.

It currently supports:
- .NET SDK
- JetBrains Rider
- JetBrains WebStorm

The app installs everything into user-local directories and keeps an active version pointer so you can switch to managed installs cleanly.

## What it does

- Detects installed versions and compares them with the latest catalog versions
- Installs the latest supported releases
- Maintains user-level environment wiring (`PATH`, `DOTNET_ROOT`) for managed .NET
- Creates platform-appropriate launch shortcuts for managed IDE installs
- Lists managed installs in a Cleanup tab and allows removing old, non-active versions

## Supported platforms

- Linux
- Windows

Release artifacts are currently published for:
- `linux-x64`
- `win-x64`

## Project structure

- `DevToolsManager.App` — Avalonia desktop UI
- `DevToolsManager.Core` — catalog, install, discovery, state, platform integration logic

## Run from source

Prerequisite: .NET SDK 10.x

```bash
dotnet restore DevToolsManager.slnx --locked-mode
dotnet build DevToolsManager.slnx -c Release
dotnet run --project DevToolsManager.App/DevToolsManager.App.csproj
```

## Build release binaries locally

Use the provided script:

```bash
./publish.sh
```

Or run the equivalent commands manually:

```bash
dotnet publish DevToolsManager.App/DevToolsManager.App.csproj /p:PublishProfile=linux-x64 /p:PublishSingleFile=true -c Release
dotnet publish DevToolsManager.App/DevToolsManager.App.csproj /p:PublishProfile=win-x64 /p:PublishSingleFile=true -c Release
```

Published files are written under:
- `publish/linux-x64/`
- `publish/win-x64/`

## State and data locations

Linux:
- Data root: `${XDG_DATA_HOME:-~/.local/share}/dev-tools-manager`

Windows:
- Data root: `%LOCALAPPDATA%\DevToolsManager`

Managed SDK/IDE installs, cache, sideload folders, and `state.json` live under the platform data root.

## CI / Release

GitHub Actions workflow: `.github/workflows/release.yml`

The workflow:
- restores dependencies
- publishes single-file binaries for Linux and Windows (`x64`)
- creates a GitHub release with the generated artifacts
