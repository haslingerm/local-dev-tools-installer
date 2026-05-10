# Plan — JetBrains IDE installer (Rider, WebStorm, …)

This document is the source of truth for the `feat/jetbrains-ide-installer`
branch. It is written so any session — even a fresh one with no chat history —
can pick up exactly where the previous session stopped.

---

## 1. Goal

Extend the existing tool so that, on Windows and Linux, **without admin rights
and without JetBrains Toolbox**, a school PC user can install the latest Rider
(and later WebStorm, IntelliJ, etc.) from the same UI that already manages .NET
SDKs.

### Primary use case (load-bearing)

A student sits down at a school PC minutes before an exam. The summer image is
months old. They need:

1. The **latest .NET SDK**, even though Windows Update / the school image only
   has whatever shipped with the summer build.
2. The **latest Rider** that knows how to compile against that SDK, even though
   the bundled Rider on the image was released the previous summer.

The flow must be:

- **Fast** — large downloads parallelized; cached / sideloaded if available.
- **Reliable** — atomic swaps, smoke tests, idempotent re-runs.
- **Mistake-proof** — one big button, plain language, no jargon, no terminal.
- **No admin** — everything in `%LOCALAPPDATA%` / `~/.local/share`.

### Non-goals

- Not Toolbox — explicitly out of scope per user requirement.
- Not macOS — current platform integration is Win/Linux only.
- Not Rider plugins / settings sync — students can sign in to their JetBrains
  account in Rider afterwards.
- Not licence handling — students bring their own JetBrains educational licence.
- Not a fully unified product abstraction — SDK and IDE share an installer
  pipeline but their *catalog* / *discovery* / *post-install* concerns stay
  product-specific. We are not building a universal package manager.

---

## 2. Background — what the existing tool does today

The project is a small Avalonia 12 + CommunityToolkit.MVVM desktop app
targeting net10. It has:

- `DotnetSdkManager.Core` — catalog client, discovery, install pipeline,
  platform abstraction (Win/Linux), state.
- `DotnetSdkManager.App` — Avalonia UI with three pages: Bootstrap (sets up
  PATH once), SdkList (installed SDKs), Catalog (browse + install).

Install pipeline (relevant to this feature):

```
download → SHA-512 verify → safe extract (entry-count/size limits)
        → smoke test (`dotnet --info` checks version + base path)
        → atomic move staging → installRoot/<version>
        → optional active link swap + env write (HKCU\Environment / shell rc)
```

Key abstractions we will reuse:

- `IPlatformIntegration` — already gives us `DataDir`, `CacheDir`, archive
  extension, current RID, junction/symlink primitives, env writer.
- `ArchiveExtractor` — handles both `.zip` and `.tar.gz` with bounded
  extraction (entry count, total size, link safety) — works as-is for
  JetBrains archives.
- `PathSafety` — version/path validation, root-escape protection.
- `SideloadScanner` — drop-archive-next-to-exe pattern; will be extended.
- `StateManager` — JSON state file with `SchemaVersion` already in place.

Env propagation truths the existing code has solved:

- **Windows:** `HKCU\Environment` `Path` + `DOTNET_ROOT`, with a
  `WM_SETTINGCHANGE` broadcast so a freshly launched process tree sees the
  new env. Rider launched after bootstrap therefore inherits `DOTNET_ROOT`.
- **Linux:** `.profile`, `.bashrc`, `.zshrc`, fish `conf.d/*.fish` — patched
  with markers so we can update idempotently. **Caveat:** Linux GUI apps
  launched from the desktop environment do NOT reliably read shell rc files.
  We will need an additional mechanism for IDE shortcuts (see §6.4).

---

## 3. Decisions

The decisions below are settled — the implementation phases assume them.
Anything still open is in §11 "Open questions".

### 3.1 Toolbox: not used

Per the user requirement. Direct ZIP / tar.gz extraction only.

### 3.2 Project naming

**Decision: keep the assembly / namespace names** (`DotnetSdkManager.*`) for
this branch. Rename is a separate refactor; doing it now would conflate two
concerns and make the diff hard to review. The window title and user-visible
strings will be updated in Phase 6 to reflect the broader scope (e.g.
"Dev Tools Manager"). A future renaming PR can move the namespaces.

### 3.3 IDE catalog source

`https://data.services.jetbrains.com/products/releases?code=<CODE>&type=release`
plus `&latest=true` for the "latest only" fast path. One endpoint covers all
JetBrains IDEs; product is selected by the `code` parameter.

| Product   | Code |
|-----------|------|
| Rider     | RD   |
| WebStorm  | WS   |
| IntelliJ Ultimate | IIU |
| IntelliJ Community | IIC |
| PyCharm Pro | PCP |
| PyCharm Community | PCC |

Adding a new IDE = one enum value + display metadata. No new code.

### 3.4 Hash verification

JetBrains publishes a sidecar file `<archive>.sha256` containing the hex
SHA-256 followed by the filename. Our installer must:

1. Fetch `<download_url>.sha256` (small HTTP GET, no caching needed — fetched
   alongside the catalog response).
2. Parse first whitespace-delimited token as the expected hex digest.
3. Compute SHA-256 of the downloaded archive incrementally, compare.

The existing `SdkInstaller.VerifyHashAsync` is hardcoded to SHA-512. We
parameterize via `HashAlgorithmName` when extracting the shared installer
(see Phase 3).

### 3.5 Smoke test for IDEs

**Use `build.txt`, not the launcher.**

Every JetBrains IDE archive ships a `build.txt` at its root containing the
build number (e.g. `RD-261.12345.67`). The smoke test:

1. Verify `build.txt` exists in the staging dir.
2. Read it, compare to `release.Build` from the catalog.

This avoids forking a process, avoids the GUI side-effects of `--version`
(some launcher versions still init the JBR or touch DPI APIs), and works
identically on Windows and Linux.

### 3.6 Install layout

```
<DataDir>/
  sdks/<version>/                    ← existing, .NET SDKs
  ides/
    rider/
      <version>/                     ← extracted Rider archive root
      active                         ← junction / symlink → currently active version
    webstorm/
      <version>/
      active
  shortcuts/
    rider-launcher.sh                ← Linux only; sourced env + exec rider.sh
  cache/                             ← existing, archive cache
  sideload/                          ← existing, .NET archives
  sideload-ides/                     ← new, JetBrains archives
```

`active` per product enables atomic version switching without re-creating
shortcuts (the shortcut targets `…/active/bin/rider64.exe` etc.).

### 3.7 Multiple IDE versions

Supported. Default behavior installs one version at a time and replaces the
previous "active" link. Advanced UI allows keeping older versions (same
pattern as SDKs).

### 3.8 IDE config dir policy

JetBrains IDE settings default to:

- Windows: `%APPDATA%\JetBrains\Rider<ver>\`,
  caches in `%LOCALAPPDATA%\JetBrains\Rider<ver>\`
- Linux: `~/.config/JetBrains/Rider<ver>/`,
  caches in `~/.cache/JetBrains/Rider<ver>/`

Two modes available in the IDE install UI:

1. **Default (recommended for personal accounts):** OS defaults. Settings
   survive across upgrades; lost on profile wipe.
2. **Self-contained (recommended for shared exam machines):** drop an
   `idea.properties` next to `bin/rider.sh` redirecting all directories under
   `${idea.home}/`. The IDE state lives inside the install dir, so persisting
   the install dir persists everything.

```properties
idea.config.path=${idea.home}/config
idea.system.path=${idea.home}/system
idea.plugins.path=${idea.home}/config/plugins
idea.log.path=${idea.home}/system/log
ide.no.platform.update=true
```

`ide.no.platform.update=true` disables the in-place updater so the version
the school deployed stays the version students get.

### 3.9 Linux GUI env propagation — the wrapper script

Apps launched from the desktop environment (.desktop files) do NOT reliably
inherit env from `.profile` / `.bashrc`. So Rider on Linux would not see
`DOTNET_ROOT` / the active SDK on PATH unless we do something extra.

**Decision:** generate a wrapper script `<DataDir>/shortcuts/rider-launcher.sh`
and point the `.desktop` `Exec=` line at it:

```sh
#!/bin/sh
active="$HOME/.local/share/dotnet-sdk-manager/active"
if [ -d "$active" ]; then
    export DOTNET_ROOT="$active"
    case ":$PATH:" in *":$active:"*) ;; *) export PATH="$active:$PATH" ;; esac
fi
exec "$HOME/.local/share/dotnet-sdk-manager/ides/rider/active/bin/rider.sh" "$@"
```

Belt-and-suspenders alternative: also write
`~/.config/environment.d/dotnet-sdk-manager.conf`. Systemd user managers pick
this up. Not all DEs use systemd-user fully, so the wrapper stays primary.

### 3.10 Shortcut creation

- **Windows:** `IShellLink` + `IPersistFile` COM interop. Same P/Invoke style
  as the existing `CreateJunction` code. Drop into
  `%APPDATA%\Microsoft\Windows\Start Menu\Programs\JetBrains Rider.lnk`.
  IconLocation = `bin\rider64.exe,0` (extracts embedded icon).
- **Linux:** write `.desktop` text file to
  `~/.local/share/applications/jetbrains-rider.desktop` referencing
  `bin/rider.png` as the icon.
  Run `update-desktop-database` (best-effort, not required) so the menu
  refreshes immediately.

The desktop entry uses `StartupWMClass=jetbrains-rider` so the running window
groups under the same icon.

### 3.11 No PATH entry for IDE bins

Different from SDKs. Putting `bin/` on `PATH` adds a `rider`/`rider.sh`
binary that's rarely useful interactively and would conflict if a student
later installs Toolbox on their personal machine. Use shortcuts only.

---

## 4. Architecture

### 4.1 New layout (post-implementation)

```
DotnetSdkManager.Core/
├── Catalog/                          ← .NET catalog (unchanged)
│   ├── ReleasesCatalogClient.cs
│   ├── ReleasesIndexEntry.cs
│   └── ChannelReleases.cs
├── Catalog.JetBrains/                ← NEW
│   ├── JetBrainsProduct.cs           ← enum + metadata table
│   ├── JetBrainsCatalogClient.cs     ← single endpoint, all products
│   ├── JetBrainsReleaseDto.cs        ← maps the data.services JSON
│   └── ShaSidecarFetcher.cs          ← parse `<url>.sha256`
├── Discovery/
│   ├── SdkDiscovery.cs               ← unchanged
│   └── IdeDiscovery.cs               ← NEW: scan ides/<product>/<version>
├── Install/
│   ├── ArchiveExtractor.cs           ← unchanged
│   ├── ProductInstaller.cs           ← NEW: shared download/verify/extract/swap
│   ├── SdkInstaller.cs               ← refactored to delegate to ProductInstaller
│   ├── IdeInstaller.cs               ← NEW: SHA-256, build.txt smoke, post-install glue
│   ├── SdkSmokeTest.cs               ← unchanged
│   ├── IdeSmokeTest.cs               ← NEW: build.txt verifier
│   ├── SdkUninstaller.cs             ← unchanged
│   ├── IdeUninstaller.cs             ← NEW: remove version, switch active
│   ├── StubManager.cs                ← unchanged
│   └── ShortcutWriter.cs             ← NEW: Win .lnk + Linux .desktop + wrapper
├── Models/
│   ├── AppState.cs                   ← +ActiveIdes dictionary, schema v2
│   ├── SdkInfo.cs                    ← unchanged
│   ├── SdkRelease.cs                 ← unchanged
│   ├── IdeInfo.cs                    ← NEW
│   ├── IdeRelease.cs                 ← NEW
│   └── InstallProgress.cs            ← unchanged (already generic enough)
├── Platform/
│   ├── IPlatformIntegration.cs       ← +IdeRoot, +CreateOrUpdateIdeLinkAsync,
│   │                                    +CreateShortcutAsync
│   ├── WindowsPlatformIntegration.cs ← +IShellLink shortcut writer
│   └── LinuxPlatformIntegration.cs   ← +.desktop writer + wrapper script
├── Process/                          ← unchanged
├── Sideload/
│   ├── SideloadScanner.cs            ← unchanged (.NET sideloads)
│   └── IdeSideloadScanner.cs         ← NEW: scan sideload-ides/ for JetBrains archives
├── State/                            ← StateManager unchanged; AppState evolves
└── Util/
    └── PathSafety.cs                 ← +RequireValidIdeVersion (loose: x.y.z[.w])

DotnetSdkManager.App/
├── ViewModels/
│   ├── MainWindowViewModel.cs        ← +Tabs, +ExamModeTabVm
│   ├── BootstrapPageViewModel.cs     ← unchanged
│   ├── SdkListPageViewModel.cs       ← unchanged
│   ├── CatalogPageViewModel.cs       ← unchanged
│   ├── IdeOverviewPageViewModel.cs   ← NEW: per-product card list
│   ├── IdeProductCardViewModel.cs    ← NEW: install/update/launch/uninstall per product
│   ├── ExamModePageViewModel.cs      ← NEW: one-click flow
│   └── ReleaseChannelViewModel.cs    ← unchanged
├── Views/
│   ├── MainWindow.axaml              ← TabControl replaces left-nav
│   ├── IdeOverviewPageView.axaml     ← NEW
│   ├── IdeProductCardView.axaml      ← NEW
│   └── ExamModePageView.axaml        ← NEW
└── (rest unchanged)
```

### 4.2 The shared installer

`ProductInstaller` exposes a single public method:

```csharp
public async Task<string> InstallAsync(
    InstallRequest request,
    IProgress<InstallProgress>? progress = null,
    CancellationToken ct = default);
```

where:

```csharp
public sealed record InstallRequest(
    string DownloadUrl,
    string FileName,
    string ExpectedHash,
    HashAlgorithmName HashAlgorithm,
    long ExpectedSize,
    bool IsHashVerified,
    string? SideloadPath,
    string TargetInstallDir,           // already-resolved, must be inside InstallRoot
    Func<string, CancellationToken, ValueTask<(bool ok, string output)>> SmokeTest,
    ArchiveExtractionLimits? ExtractLimits);
```

The class owns:
- Download with progress + size-tolerance bound.
- Hash compute via `IncrementalHash.Create(request.HashAlgorithm)`.
- Bounded extract via `ArchiveExtractor`.
- Smoke test via the supplied delegate.
- Atomic staging → install dir move with backup-and-rollback.
- Cache cleanup on success.

`SdkInstaller` becomes a ~40-line wrapper that builds an `InstallRequest`
from `SdkRelease`. `IdeInstaller` is the same shape with different fields.

### 4.3 Why not unify the catalog clients?

The two feeds are structurally different (channel-of-releases-of-sdks vs
flat-list-of-releases) and the `.NET` feed is used by `CatalogPageViewModel`
in ways tied to the channel concept (collapsible UI sections per major
version). Forcing a common abstraction would obscure the `.NET` feed's
shape without simplifying anything. Instead: separate clients, separate
view-model trees per product family, single shared installer at the bottom.

### 4.4 AppState v2 (backward-compatible read)

```jsonc
{
  "schemaVersion": 2,
  "bootstrapped": true,
  "activeVersion": "10.0.100",         // .NET SDK
  "activeIdes": {
    "RD": "2026.1.1",
    "WS": "2026.1.0"
  }
}
```

Loader: if `schemaVersion < 2`, default `activeIdes` to empty; bump on next
save. If JSON parsing fails, return a fresh `AppState` (existing behavior).

---

## 5. UI redesign

### 5.1 Top-level navigation: `TabControl`

```
┌───────────────────────────────────────────────────────────┐
│  [ Exam Mode ]  [ .NET SDKs ]  [ IDEs ]  [ Settings ]     │ ← tabs
├───────────────────────────────────────────────────────────┤
│                                                           │
│   tab content                                             │
│                                                           │
└───────────────────────────────────────────────────────────┘
```

Order matters: **Exam Mode is the first tab**, opens by default. A nervous
student should not have to think; the right answer is on the screen.

### 5.2 Exam Mode tab

A dedicated landing page:

```
        Get this PC ready for your exam
        ───────────────────────────────────────

        ┌────────────────────────────────────────┐
        │  ✓  .NET SDK 10.0.105 — installed      │
        │  ✓  Rider 2026.1.1   — installed       │
        │                                        │
        │            [ All set! ]                │
        └────────────────────────────────────────┘

  or, when not ready:

        ┌────────────────────────────────────────┐
        │  ⟳  Will install:                      │
        │     • .NET SDK 10.0.105   (~210 MB)   │
        │     • Rider 2026.1.1      (~1.2 GB)   │
        │                                        │
        │       [ Prepare this PC (≈ 5 min) ]   │
        └────────────────────────────────────────┘

  during install:

        ┌────────────────────────────────────────┐
        │  Downloading & installing...           │
        │                                        │
        │  .NET SDK 10.0.105   ████████░░  82 % │
        │  Rider 2026.1.1      ████░░░░░░  41 % │
        │                                        │
        │  Don't close this window.             │
        └────────────────────────────────────────┘
```

Behavior:

- On entry, computes "what's missing" from current state vs latest catalog.
- One button. Idempotent. Re-running after success says "All set" and is a
  no-op.
- Downloads parallelized. Installation is sequential per product (cheap;
  archives are I/O-bound).
- After success: fixed-position "Open Rider" button that runs the shortcut.
- Errors land in a single visible message + a "Retry" button. Never a
  cryptic stack trace.

### 5.3 IDEs tab

Cards per product (Rider, WebStorm, …). Each card shows:

- Product name + currently installed version (or "Not installed").
- Latest available version from catalog, with delta indicator.
- Buttons: `Install latest` / `Update to X` / `Launch` / `Manage versions…`.

`Manage versions…` opens a sub-page identical in spirit to the .NET catalog
browser: list of available versions, install / uninstall actions.

### 5.4 .NET SDKs tab

Wraps the existing `SdkListPageView` and `CatalogPageView` as two sub-tabs
(or kept side-by-side; final layout is a Phase 6 refinement).

### 5.5 Settings tab (new)

- Bootstrap status (move from blocking page to opt-in setting).
- "Self-contained IDE config" toggle (applies on next install).
- "Disable IDE auto-update" toggle (default on).
- Sideload directory path display.
- About / version / repo link.

Bootstrap stops being a blocking modal. If the user hits Exam Mode without
bootstrapping, the flow performs bootstrap as part of the prepare step
silently. This removes a stumbling block for the nervous-student case.

---

## 6. Glue: making Rider find the active .NET SDK

This section is critical and easy to get wrong. The default behavior we want:

> Click "Open Rider" → Rider starts → New project → templates show .NET 10.

Rider locates .NET SDKs in this order (per JetBrains docs and observed
behavior):

1. `DOTNET_ROOT` env var.
2. `dotnet` on `PATH`.
3. Hard-coded discovery paths (`C:\Program Files\dotnet\`,
   `/usr/share/dotnet/`, `/usr/lib/dotnet/`, `~/.dotnet/`).
4. Rider settings: `Settings | Build, Execution, Deployment | Toolset and
   Build | .NET Toolset | Use this .NET SDK`.

Our existing bootstrap sets `DOTNET_ROOT` and prepends the active link to
`PATH`. So (1) and (2) point at our managed SDK. Items (3) and (4) are
fallbacks we don't need to touch.

### 6.1 Windows

Rider launched fresh after bootstrap inherits `DOTNET_ROOT` from
`HKCU\Environment`. The `WM_SETTINGCHANGE` broadcast triggers
already-running explorer to refresh, so a freshly spawned process tree gets
the new env. **No additional work needed.** Verified by the existing tests
in `SdkSmokeTest.TestDefaultSwitchAsync`.

### 6.2 Linux

Shell rc files won't help GUI launches. The `rider-launcher.sh` wrapper
described in §3.9 sets `DOTNET_ROOT` and prepends to `PATH` *in the
launching process*, then `exec`s `rider.sh`. Rider's child processes
(MSBuild, Roslyn, dotnet test) inherit and see the active SDK.

Smoke test for the glue (manual, in Phase 8): drop a hello-world C#10 project
in `~/RiderTest`, click Run, see `Hello, World!` on stdout. This confirms
the chain end-to-end.

### 6.3 Switching the active SDK while Rider is running

Rider caches the SDK list at startup. Switching SDK in our UI doesn't
update a running Rider; the user has to restart Rider. We surface this in
the SDK switch confirmation: "Restart Rider to pick up the new SDK."

### 6.4 Edge case: no SDK selected (stub)

If the user uninstalls their last SDK, the existing tool installs a stub
that prints an error. Rider in this state will fall back to its hardcoded
discovery paths. Document in the README that "no .NET SDK selected" means
Rider may use a system .NET if one exists. Do not try to "block" Rider from
finding system SDKs — that's outside our scope.

---

## 7. Step-by-step implementation order

Each phase is a self-contained change-set that compiles, runs, and is
intended to be committed as one logical unit. Phases are numbered to match
the corresponding tasks in the task list.

### Phase 1 — Project naming refactor (deferred)

Decision recorded in §3.2. **Skipped on this branch.** Window title and
display strings change in Phase 6.

### Phase 2 — Extend `IPlatformIntegration` for IDEs

Add to the interface:
- `string IdeInstallRoot { get; }` — `<DataDir>/ides`
- `string ShortcutDir { get; }` — `<DataDir>/shortcuts` (Linux only; Windows
  shortcut goes to Start Menu directly)
- `ValueTask CreateOrUpdateIdeLinkAsync(string product, string targetPath, CancellationToken ct)` —
  manages `<IdeInstallRoot>/<product>/active`.
- `ValueTask CreateShortcutAsync(IdeShortcutSpec spec, CancellationToken ct)` —
  Win .lnk via `IShellLink`, Linux .desktop + wrapper script.

`IdeShortcutSpec` (in `Platform/`):
```csharp
public sealed record IdeShortcutSpec(
    string ProductCode,        // "RD"
    string DisplayName,        // "JetBrains Rider"
    string ExecutablePath,     // resolved to .../active/bin/rider64.exe or .../active/bin/rider.sh
    string IconPath,           // resolved
    string StartupWmClass,     // "jetbrains-rider"
    string Comment);           // ".NET cross-platform IDE"
```

Linux variant of `CreateShortcutAsync`:
1. Generate `<ShortcutDir>/<product>-launcher.sh` with the wrapper from §3.9
   (chmod 755).
2. Generate `~/.local/share/applications/<product>.desktop` referencing the
   wrapper.
3. Best-effort run `update-desktop-database ~/.local/share/applications`.

Windows variant: generate the .lnk via `IShellLink::SetPath` /
`SetIconLocation` / `SetDescription` / `SetWorkingDirectory` /
`IPersistFile::Save`. Place in
`%APPDATA%\Microsoft\Windows\Start Menu\Programs\<DisplayName>.lnk`.

**Acceptance:** unit-test or manual: call `CreateShortcutAsync` with a fake
target on each platform, verify the shortcut appears in the user's app
menu and resolves correctly.

### Phase 3 — Extract `ProductInstaller`

Move the body of `SdkInstaller.InstallAsync` into a new
`ProductInstaller.InstallAsync(InstallRequest, …)` per §4.2. Replace the
hash code with `IncrementalHash.Create(request.HashAlgorithm)`.

Refactor `SdkInstaller`:
```csharp
public async Task<string> InstallAsync(
    SdkRelease release, IProgress<InstallProgress>? progress, CancellationToken ct)
{
    PathSafety.RequireValidSdkVersion(release.Version, nameof(release.Version));
    var targetDir = PathSafety.CombineSafe(_platform.InstallRoot, release.Version);
    return await _productInstaller.InstallAsync(
        new InstallRequest(
            release.DownloadUrl, release.FileName, release.Hash,
            HashAlgorithmName.SHA512, release.Size,
            release.IsHashVerified, release.SideloadPath,
            targetDir,
            (dir, ct) => _smokeTest.TestInstallAsync(dir, release.Version, ct),
            release.IsHashVerified ? null : new ArchiveExtractionLimits()),
        progress, ct);
}
```

**Acceptance:** existing UI must still install a .NET SDK successfully on
Windows and Linux. No behavior change for users.

### Phase 4 — JetBrains catalog + IDE installer

Files (per §4.1):

`Catalog.JetBrains/JetBrainsProduct.cs`:
```csharp
public enum JetBrainsProduct { Rider, WebStorm }   // extend later

public static class JetBrainsProductInfo {
    public static string Code(JetBrainsProduct p) => p switch {
        JetBrainsProduct.Rider => "RD",
        JetBrainsProduct.WebStorm => "WS",
        _ => throw new ArgumentOutOfRangeException(nameof(p)) };
    public static string DisplayName(JetBrainsProduct p) => …;
    public static string LinuxLauncherRelative(JetBrainsProduct p) => "bin/rider.sh" or "bin/webstorm.sh";
    public static string WindowsLauncherRelative(JetBrainsProduct p) => "bin/rider64.exe" or "bin/webstorm64.exe";
    public static string LinuxIconRelative(JetBrainsProduct p) => "bin/rider.png" or "bin/webstorm.png";
    public static string StartupWmClass(JetBrainsProduct p) => …;
}
```

`Catalog.JetBrains/JetBrainsCatalogClient.cs`:
- Single method `GetReleasesAsync(JetBrainsProduct p, bool latestOnly, CancellationToken ct)`.
- Same caching pattern as `ReleasesCatalogClient` (24h TTL, ETag aware).
- Picks the platform-appropriate download per RID:
  - `win-x64` → `downloads.windowsZip`
  - `win-arm64` → `downloads.windowsZipARM64`
  - `linux-x64` → `downloads.linux`
  - `linux-arm64` → `downloads.linuxARM64`
- Fetches `<link>.sha256` (no caching, small) and stuffs the digest into
  the resulting `IdeRelease`.

`Models/IdeRelease.cs`:
```csharp
public record IdeRelease(
    JetBrainsProduct Product,
    string Version,            // "2026.1.1"
    string Build,              // "RD-261.12345.67" — for build.txt smoke test
    string DownloadUrl,
    string Sha256,
    long Size,
    string FileName)
{
    public bool IsInstalled { get; init; }
    public string? SideloadPath { get; init; }
    public bool HasSideload => SideloadPath is not null;
}
```

`Install/IdeSmokeTest.cs`:
```csharp
public sealed class IdeSmokeTest {
    public ValueTask<(bool ok, string output)> TestInstallAsync(
        string installDir, string expectedBuild, CancellationToken ct) {
        var path = Path.Combine(installDir, "build.txt");
        if (!File.Exists(path)) return new((false, $"build.txt not found in {installDir}"));
        var actual = (await File.ReadAllTextAsync(path, ct)).Trim();
        if (!actual.Equals(expectedBuild, StringComparison.OrdinalIgnoreCase))
            return (false, $"build.txt mismatch. Expected '{expectedBuild}', got '{actual}'");
        return (true, actual);
    }
}
```

`Install/IdeInstaller.cs`:
```csharp
public async Task<string> InstallAsync(
    IdeRelease release, IProgress<InstallProgress>? progress, CancellationToken ct) {
    var productSlug = JetBrainsProductInfo.Code(release.Product).ToLowerInvariant();
    var productRoot = Path.Combine(_platform.IdeInstallRoot, productSlug);
    Directory.CreateDirectory(productRoot);
    var targetDir = PathSafety.CombineSafe(productRoot, release.Version);

    var installDir = await _productInstaller.InstallAsync(
        new InstallRequest(
            release.DownloadUrl, release.FileName, release.Sha256,
            HashAlgorithmName.SHA256, release.Size,
            IsHashVerified: true,
            release.SideloadPath, targetDir,
            (dir, ct) => _ideSmoke.TestInstallAsync(EffectiveIdeRoot(dir, release.Product), release.Build, ct),
            ExtractLimits: null),
        progress, ct);

    if (_options.SelfContainedConfig) WriteIdeaProperties(installDir, release.Product);
    await _platform.CreateOrUpdateIdeLinkAsync(productSlug, EffectiveIdeRoot(installDir, release.Product), ct);
    await _platform.CreateShortcutAsync(BuildShortcutSpec(release.Product), ct);
    return installDir;
}
```

The Linux `.tar.gz` for JetBrains products extracts to a single inner
directory like `JetBrains Rider-2026.1.1/`, *not* directly to the dest. We
need to detect this and either:

- **Option A:** strip the leading directory during extraction. Requires
  changes to `ArchiveExtractor`.
- **Option B:** extract as-is and resolve the IDE root after the fact via
  `EffectiveIdeRoot` (look for the single subdir containing `bin/`).

Decision: **Option B** — keep `ArchiveExtractor` simple, resolve at the
caller. `EffectiveIdeRoot` looks for `<dir>/build.txt`; if missing, looks at
single-immediate-subdirectory and recurses one level. Helpful for Windows
ZIP too where the structure varies between products.

**Acceptance:** install latest Rider on each platform from the UI. Verify
`build.txt` matches catalog. Open shortcut, Rider launches, "About" shows
expected version.

### Phase 5 — Extend `AppState`

Add `Dictionary<string, string> ActiveIdes` to `AppState`. Bump
`SchemaVersion` to 2. Loader handles missing dict by initializing empty.

`StateManager.Load()` already swallows parse failures and returns a fresh
state, so the existing fallback handles the schema-mismatch case
gracefully.

**Acceptance:** read pre-existing state.json (schema 1) without errors;
write state.json (schema 2) and re-load round-trips correctly.

### Phase 6 — UI redesign (tabs + IDE views)

1. Replace `MainWindow.axaml` left-nav with a `TabControl`. Tabs:
   `Exam Mode`, `.NET SDKs`, `IDEs`, `Settings`.
2. Create `IdeOverviewPageView` + VM. Hosts an `ItemsControl` of
   `IdeProductCardView`s (one per known product). Card shows install
   status, latest available, action button.
3. Create per-product card VM that wires to `IdeInstaller`,
   `IdeUninstaller`, `IdeDiscovery`, `JetBrainsCatalogClient`.
4. Wire bootstrap into Settings tab as a status indicator + optional
   button. Drop the blocking-page behavior in favor of inline bootstrap
   from Exam Mode.
5. Update window title to "Dev Tools Manager" (or similar — final name
   chosen during UI review).

**Acceptance:** Manual: open app, verify each tab loads without errors,
verify Rider card shows correct state when installed/not-installed.

### Phase 7 — Exam Mode flow

`ExamModePageViewModel.PrepareAsync()`:

```
1. Load AppState; ensure bootstrapped (call platform.WriteEnvironmentAsync silently).
2. Fetch latest .NET SDK (channel index → first STS/LTS release) and latest Rider release.
3. Compute "needs":
     needsSdk    = latestSdk.Version    not in installed managed SDKs
     needsRider  = latestRider.Version  not in installed Rider versions
4. If !(needsSdk || needsRider): set status "All set" and return.
5. Run downloads in parallel:
     var sdkTask   = needsSdk   ? _sdkInstaller.InstallAsync(latestSdk, sdkProgress, ct)   : Task.FromResult(null);
     var riderTask = needsRider ? _ideInstaller.InstallAsync(latestRider, riderProgress, ct) : Task.FromResult(null);
     await Task.WhenAll(sdkTask, riderTask);
6. If SDK installed: _sdkUninstaller.SwitchDefaultAsync(latestSdk); update state.ActiveVersion.
7. If Rider installed: state.ActiveIdes["RD"] = latestRider.Version; persist.
8. Show "All set! Open Rider →" button.
```

Two `IProgress<InstallProgress>` instances feed two progress bars in the
UI. Aggregate progress is just `(sdkPercent + riderPercent) / 2`.

Failure modes:
- Network down → catalog falls back to cache (already implemented).
- Hash mismatch → fail loudly, friendly message + retry button.
- Disk full → friendly message, no retry.
- Cancellation: leave staging dirs cleaned (already handled in
  `ProductInstaller`).

**Acceptance:** on a clean Windows VM and a clean Linux VM:
1. App launches with no SDK / no Rider.
2. Click "Prepare this PC" once.
3. ~5 minutes later (depending on bandwidth): SDK installed, Rider
   installed, shortcut on Start Menu / app menu, click shortcut, Rider
   opens, "About" shows expected version.
4. New project → C# console → run → "Hello, World!" on stdout.

### Phase 8 — Build, package, smoke-test

1. `./publish.sh` → produces single-file binaries for win-x64 and linux-x64.
2. Run on a clean Windows VM: walk Phase 7 acceptance.
3. Run on a clean Linux VM: walk Phase 7 acceptance.
4. Verify: re-running Exam Mode after success is a no-op ("All set").
5. Verify: switching default SDK while Rider installed shows "restart Rider"
   hint and works after restart.
6. Verify: removing the active link manually triggers re-bootstrap on next
   launch (existing reconcile logic).
7. Verify: sideload path works for both SDK and IDE archives (drop file
   into sideload-ides/, verify it surfaces in catalog and installs).

---

## 8. Critical correctness checks (don't skip)

These are the things most likely to silently break the exam-day experience.

1. **Atomic install**: Phase 4 must keep `ProductInstaller`'s
   staging-dir-then-move pattern. A Ctrl+C mid-extract must leave the
   previous version intact. Test by killing the app at extract phase.
2. **Active-link integrity after install**: re-running install of an
   already-installed version must not break the active link. The existing
   pattern moves to `<install>.backup-<guid>` and rolls back on failure;
   keep that for IDEs.
3. **Linux wrapper script env order**: `DOTNET_ROOT` and PATH prepend
   must happen *before* `exec rider.sh` so child processes inherit them.
   The wrapper must `exec` (not just call) so PIDs and signal handling
   stay clean.
4. **Hash sidecar parsing**: JetBrains' `.sha256` files contain
   `<digest>  <filename>`. Parse the *first whitespace-separated token*,
   not the whole file. Trim newlines. Be tolerant of CRLF.
5. **build.txt format**: Some products prefix the build (e.g.
   `RD-261.12345.67`), some don't. Use the catalog's `build` field as the
   expected value verbatim.
6. **Inner directory in tar.gz**: described in §4.1 / Phase 4. Use
   `EffectiveIdeRoot` to handle either layout.
7. **State migration**: schema v1 → v2 must not lose `ActiveVersion`.
   Add a unit test or manual round-trip.
8. **UAC / file locking on Windows**: a previously running Rider holding
   `bin\rider64.exe` will block extract / move. Detect via
   `Process.GetProcessesByName("rider64")` and prompt the user to close
   Rider before installing an update.

---

## 9. Risks & mitigations

| Risk | Mitigation |
|---|---|
| 1.2 GB Rider download on a flaky school network | 24h-cached catalog + sideload directory for offline files; ETag re-validation; `*.part` resumable file (already in download code). |
| Student under exam pressure clicks the wrong button | Exam Mode tab shown by default; button is the only obvious action; advanced is hidden. |
| New JetBrains release format changes the JSON shape | Single `JetBrainsCatalogClient` is the only adapter; isolated change surface. |
| WebStorm requires a launcher name we don't anticipate | `JetBrainsProductInfo` table centralizes per-product strings; new products = one row. |
| Antivirus quarantines the unsigned ZIP extraction | We extract to user-writable dirs; AV may still flag. Document. The .NET SDK from Microsoft is signed; Rider ZIP is also signed but our extracted bin tree is not re-signed. Out of our control. |
| Rider's in-place updater downloads a patch and corrupts our managed install | Self-contained mode sets `ide.no.platform.update=true`. Default mode users may opt out, accepting the risk. |
| Linux DE doesn't honor the .desktop file or icon | Wrapper script + .desktop is the standard pattern. Test on GNOME and KDE. Document fallback: students can run the wrapper directly from a terminal. |
| `~/.local/share/applications/` not refreshing menu | Best-effort `update-desktop-database`; on stubborn DEs the user may need to log out/in once. Document. |

---

## 10. What's deliberately **not** in scope

- Cross-platform unified update notification (e.g. "new Rider available!").
  We re-fetch on Exam Mode entry; that's enough.
- Per-user vs per-machine install mode. Single mode: per-user, no admin.
- Multi-user "lab manager" features. The school admin can pre-seed
  sideload-ides/ from their image; that's the integration point.
- `.editorconfig` / IDE settings sync. Out of scope for an installer.
- Rider plugins. Future work — we'd need a separate plugin catalog.

---

## 11. Open questions

Each of these has a default answer in the plan, but they're worth raising
when implementing:

1. **Project rename** (§3.2) — keep namespaces or rename? *Default: keep.*
2. **Self-contained config default** (§3.8) — on or off? *Default: off
   (user can toggle in Settings).* Schools may want a deployed config that
   forces it on.
3. **Window title / app name** for the rebrand — "Dev Tools Manager"?
   *Default: pick during Phase 6 with the user.*
4. **Multiple IDE versions retained** vs always-replace — *Default:
   support multi-version, default UI shows latest only.*
5. **Auto-update of the app itself** — out of scope; `publish.sh` builds a
   single-file binary that the school admin re-deploys. Confirm.
6. **Telemetry / crash reporting** — out of scope. Confirm.

---

## 12. Session resumption checklist

If you're picking this up cold, do this:

1. `git switch feat/jetbrains-ide-installer` — make sure you're on the
   right branch.
2. `git log --oneline` — see how far implementation has progressed.
3. Re-read this file end-to-end.
4. `TaskList` — see remaining phases. If the task list is empty (new
   session), the phases in §7 map 1:1 to tasks; recreate them.
5. `dotnet build` and `./publish.sh` should both succeed before adding new
   work to a phase.
6. Look at the most recent commit message for any deviations from the plan;
   update this file if the plan changed.

---

*End of plan. Edit this file as decisions evolve; treat it as the single
source of truth for the branch.*
