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

**Decision: rename to `DevToolsManager`** — done up front as Phase 1.

- Solution: `DevToolsManager.slnx`
- Projects: `DevToolsManager.App`, `DevToolsManager.Core`
- Namespaces: `DevToolsManager.App.*`, `DevToolsManager.Core.*`
- Data dir: Windows `%LOCALAPPDATA%\DevToolsManager\`,
  Linux `~/.local/share/dev-tools-manager/`
- Shell rc markers: `# >>> dev-tools-manager >>>`
- Window title: "Dev Tools Manager"
- HTTP User-Agent: `DevToolsManager/1.0`

Doing this first means every later phase ships under the final name. The
data-dir change is a clean break (initial commit was unverified, no users
yet to migrate).

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

**Decision: OS defaults only. No `idea.properties` mode.**

JetBrains IDE settings go where the IDE puts them by default:

- Windows: `%APPDATA%\JetBrains\Rider<ver>\`,
  caches in `%LOCALAPPDATA%\JetBrains\Rider<ver>\`
- Linux: `~/.config/JetBrains/Rider<ver>/`,
  caches in `~/.cache/JetBrains/Rider<ver>/`

Rationale: school-PC user profiles are wiped on each session anyway, so any
extra "self-contained" mode adds complexity without solving a real problem.
Students who use this on personal machines get the standard JetBrains
behavior they'd expect from any other install method. Skip it.

We do *not* generate an `idea.properties`, so the bundled in-place updater
remains active. That's acceptable: students typically use the tool once at
exam start, then close it. If the IDE notifies about a patch mid-session,
they can ignore. Documenting in the README is enough.

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

All names below are the post-Phase-1 names (`DevToolsManager.*`).

```
DevToolsManager.Core/
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
│   ├── BootstrapManager.cs           ← NEW: extracted from BootstrapPageViewModel,
│   │                                    runs PATH setup idempotently
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

DevToolsManager.App/
├── ViewModels/
│   ├── MainWindowViewModel.cs        ← TabControl over 4 tab VMs
│   ├── ProductTabViewModel.cs        ← NEW: shared "latest + install + show all" base
│   ├── DotnetTabViewModel.cs         ← NEW: .NET tab (kicks off bootstrap on first install)
│   ├── RiderTabViewModel.cs          ← NEW
│   ├── WebStormTabViewModel.cs       ← NEW
│   ├── CleanupTabViewModel.cs        ← NEW
│   ├── CleanupItemViewModel.cs       ← NEW: per-row in Cleanup tab
│   ├── SdkListPageViewModel.cs       ← DELETED (folded into Cleanup tab)
│   ├── CatalogPageViewModel.cs       ← kept, reused inside .NET tab's "show all" expander
│   ├── BootstrapPageViewModel.cs     ← DELETED (replaced by BootstrapManager)
│   ├── ReleaseChannelViewModel.cs    ← unchanged
│   ├── ReleaseItemViewModel.cs       ← unchanged
│   └── SdkItemViewModel.cs           ← unchanged (used in Cleanup tab)
├── Views/
│   ├── MainWindow.axaml              ← TabControl
│   ├── ProductTabView.axaml          ← NEW: shared template for product tabs
│   ├── CleanupTabView.axaml          ← NEW
│   ├── BootstrapPageView.axaml       ← DELETED
│   └── SdkListPageView.axaml         ← DELETED
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
┌──────────────────────────────────────────────────────────┐
│  [ .NET ]  [ Rider ]  [ WebStorm ]  [ Cleanup ]          │ ← tabs
├──────────────────────────────────────────────────────────┤
│                                                          │
│   tab content                                            │
│                                                          │
└──────────────────────────────────────────────────────────┘
```

Each product tab is symmetric: "Latest available + one big button to
install it." The student's mental model is identical across products.
There is no "Settings" tab and no "Exam Mode" tab — exam-day behavior is
just what the per-product tabs already do by default.

### 5.2 Per-product tab (.NET / Rider / WebStorm)

The default state on tab entry shows only the latest version:

```
   .NET SDK
   ───────────────────────────────────────

   Latest:  10.0.105  (released 2026-04-08)

   ┌─────────────────────────────────────────┐
   │   Currently installed: 10.0.103         │
   │                                         │
   │      [ Update to 10.0.105 (210 MB) ]    │
   └─────────────────────────────────────────┘

   ▾ Show all versions
```

Three states for the primary card:

| State | Card content | Button |
|---|---|---|
| Nothing installed | "Not installed" | `[ Install 10.0.105 (210 MB) ]` |
| Older installed | "Currently installed: X" | `[ Update to Y (210 MB) ]` |
| Latest installed | "✓ 10.0.105 — up to date" | `[ Open Rider ]` (IDE only); none for .NET |

During install: progress bar replaces the button, status line below.

After successful install: button transitions to "Up to date" state, plus
a small "✓ Done" toast for ~3 seconds.

Idempotency: re-clicking the install button while already up-to-date is
impossible (button changes). Re-entry to the tab re-fetches catalog,
re-evaluates state.

`Show all versions` (collapsed by default) reveals the existing
catalog-browser view: full channel list for .NET, full release list for
JetBrains. Power users / older-version installs go through there.

### 5.3 Bootstrap is invisible

The first `.NET install` action transparently performs the existing
bootstrap (PATH + DOTNET_ROOT in HKCU\Environment / shell rc files) before
extracting the SDK. No separate bootstrap page, no toggle. If bootstrap
fails (rare — only writeable-state issue on some locked-down configs), the
install fails with a clear message.

This means a fresh user does not encounter the term "bootstrap" or "PATH"
anywhere. They click `Install 10.0.105`, wait, done.

### 5.4 Cleanup tab

The disk-space recovery view. Lists **only managed installs** (system .NET
SDKs and any future system-installed IDEs are read-only and never appear
here):

```
   Cleanup
   ───────────────────────────────────────────────────

   Reclaim disk space by removing managed installs you no longer need.
   Active versions and system installs are protected.

   Total managed: 3.4 GB

   .NET SDKs (managed)
   ┌─────────────────────────────────────────────────┐
   │  10.0.105  (active)            210 MB           │
   │  10.0.103                      209 MB  [Remove] │
   │  9.0.205                       195 MB  [Remove] │
   └─────────────────────────────────────────────────┘

   JetBrains Rider
   ┌─────────────────────────────────────────────────┐
   │  2026.1.1  (active)          1.6 GB             │
   │  2025.3.4                    1.5 GB    [Remove] │
   └─────────────────────────────────────────────────┘

   JetBrains WebStorm
   (none installed)
```

Behavior:

- Sizes computed lazily after tab is shown (one-time enumeration; cached
  for tab session).
- `Remove` is a single click. No confirm dialog by default — but the
  button briefly transitions to `Confirm?` for ~2s on first click,
  becoming a real removal on the second click within that window. Cheap
  protection against fat-finger; no modal pop-up.
- Active versions show no `Remove` button by default. To remove the active
  one, the user has to switch active first (advanced); we surface this as
  a dimmed `Remove` with hover tooltip "This is the active version."
- For .NET specifically, the existing fallback logic (`PickFallback` →
  `SwitchToStubAsync`) makes removing the only active SDK technically
  safe, but we still hide that path from the Cleanup tab to keep students
  from cratering their setup.
- Removal uses existing `SdkUninstaller` for SDKs and the new
  `IdeUninstaller` for IDEs. After each removal, sizes refresh.

The Cleanup tab does NOT show:

- System-installed .NET SDKs (they're not ours to remove).
- The active `dev-tools-manager/active` symlink (it's a pointer, not a
  consumer of disk space worth surfacing).
- The cache dir (cleared automatically; not user-actionable).

### 5.5 What the UI deliberately does NOT do

- No "all installed versions" panel on a product tab. That's the Cleanup
  tab's job.
- No multi-product "install everything" button. Two clicks (.NET +
  Rider) is fine; the simplification is per-tab focus.
- No notification badge for available updates. Re-fetched on tab entry
  only. Avoids surprising the user with mid-session noise.

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

### Phase 1 — Project rename to `DevToolsManager`

Mechanical refactor done in one commit so every later phase ships under
the final name.

Steps:

1. `git mv DotnetSdkManager.App DevToolsManager.App` (and Core).
2. Rename the .csproj files inside.
3. Rename `DotnetSdkManager.slnx` → `DevToolsManager.slnx`; update the
   `<Project Path=…/>` entries.
4. Update namespaces across all `.cs` files: `DotnetSdkManager.App` →
   `DevToolsManager.App`, `DotnetSdkManager.Core` → `DevToolsManager.Core`.
5. Update Avalonia XAML namespaces (`xmlns:vm="using:DotnetSdkManager…"`)
   and `x:Class` directives.
6. Update string constants:
   - `WindowsPlatformIntegration.DataDir`: `"DotnetSdkManager"` →
     `"DevToolsManager"`.
   - `LinuxPlatformIntegration.DataDir`: `"dotnet-sdk-manager"` →
     `"dev-tools-manager"`.
   - Linux rc-file markers: `dotnet-sdk-manager` → `dev-tools-manager`.
   - Fish config filename and comment: same.
   - `App.axaml.cs` HttpClient User-Agent: `"DotnetSdkManager/1.0"` →
     `"DevToolsManager/1.0"`.
   - `StubManager` error message strings.
   - `MainWindow.axaml` `Title=".NET SDK Manager"` →
     `Title="Dev Tools Manager"`.
   - `BootstrapPageView.axaml` heading: `"Welcome to .NET SDK Manager"` →
     `"Welcome to Dev Tools Manager"` (the bootstrap page disappears in
     Phase 6 anyway, but keep the rebrand consistent in the meantime).
   - `app.manifest` `assemblyIdentity name`.
7. Update `publish.sh` paths.
8. Update `.gitignore` if it referenced the old name (it doesn't).
9. `dotnet build` and `dotnet publish -c Release` for each platform must
   succeed.

**Acceptance:** Build succeeds. App launches and on Windows writes its
data to `%LOCALAPPDATA%\DevToolsManager\`; Linux to
`~/.local/share/dev-tools-manager/`. Title bar says "Dev Tools Manager".

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

### Phase 6 — UI redesign: 4 tabs

1. Replace `MainWindow.axaml` left-nav with a `TabControl`. Tabs:
   `.NET`, `Rider`, `WebStorm`, `Cleanup`.
2. Create `ProductTabViewModel` (generic) + `ProductTabView`.
   - Hosts: latest-version card, primary action button, optional
     `▾ Show all versions` expander wrapping the existing catalog browser.
   - Three concrete VMs derive: `DotnetTabViewModel` (uses
     `ReleasesCatalogClient` + `SdkInstaller`), `RiderTabViewModel`
     (uses `JetBrainsCatalogClient(Rider)` + `IdeInstaller`),
     `WebStormTabViewModel` (same client, product=WebStorm).
3. Adapt the existing `CatalogPageView` into a sub-component reused by the
   `Show all versions` expander on the .NET tab. The existing per-channel
   collapse-and-load behavior stays as-is.
4. Build a similar all-versions browser for JetBrains products (flat
   release list, latest-on-top, latest-N visible with "show older").
5. Create `CleanupTabViewModel` + `CleanupTabView` per §5.4. Reuses
   `SdkDiscovery`, `IdeDiscovery`, `SdkUninstaller`, new `IdeUninstaller`.
   Per-row: lazy-computed size via `Directory.EnumerateFiles(path,
   "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)`,
   one-shot per session.
6. Bootstrap behavior change: `DotnetTabViewModel.InstallAsync` calls
   `BootstrapManager.EnsureAsync(ct)` first if `state.Bootstrapped` is
   false. Extract this from the deleted `BootstrapPageViewModel`. The
   first-launch code in `App.axaml.cs` no longer routes to a bootstrap
   page when un-bootstrapped — it routes to the .NET tab as normal.
7. Delete `BootstrapPageView*` files.

**Acceptance:** Manual: launch app, verify each of the four tabs loads.
On a fresh data dir, .NET tab shows "Not installed". Click Install. Verify
PATH set, SDK installed, button transitions to "✓ Up to date." Rider tab
similar. WebStorm tab similar. Cleanup tab lists what was just installed
with computed sizes; remove operations work and active versions are
protected.

### Phase 7 — Build, package, smoke-test

1. `./publish.sh` → produces single-file binaries for win-x64 and linux-x64.
2. Run on a clean Windows VM:
   - Open .NET tab, click Install latest. Wait. Verify status "✓ Up to date".
   - Open Rider tab, click Install latest. Wait. Click `Open Rider`.
     Rider launches; About shows expected version.
   - In Rider: New Project → C# Console App on the .NET we just installed.
     Click Run. Stdout shows "Hello, World!".
   - Open Cleanup tab. Verify managed SDK + Rider listed with sizes.
     Remove a non-active SDK; verify it disappears from disk.
3. Run on a clean Linux VM: same walkthrough. Confirm `.desktop` entry
   appears in the application menu and the wrapper script propagates
   `DOTNET_ROOT` so Rider sees the active SDK.
4. Verify on both platforms: re-clicking Install when already up-to-date
   is impossible (button has changed state); re-entering tab re-fetches
   catalog without errors.
5. Verify: removing the active link manually outside the app triggers
   re-bootstrap on next .NET install (existing reconcile logic).
6. Verify: sideload path works for both SDK and IDE archives (drop file
   into `sideload/` or `sideload-ides/`, verify it surfaces and installs).
7. Verify: WebStorm tab end-to-end flow on at least one platform.

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

These remain to be decided during implementation:

1. **Auto-update of the app itself** — *Default: out of scope.* `publish.sh`
   builds a single-file binary that the school admin re-deploys with each
   image. Confirm before Phase 7.
2. **Telemetry / crash reporting** — *Default: out of scope.* Confirm.
3. **WebStorm: any product-specific quirks?** — discovered during Phase 4
   smoke testing. The catalog endpoint is symmetric with Rider, so the
   default expectation is "drop in a new enum value, ship." Verify with a
   real install in Phase 7.
4. **`build.txt` exact format** — Phase 4 catalog returns build numbers
   like `261.23567.144`; archive's `build.txt` is expected to contain
   `RD-261.23567.144`. The smoke test (`Install/IdeSmokeTest.cs`) uses a
   substring match to handle both. Confirm against a real archive in
   Phase 7; if it turns out the formats are equal, simplify back to
   exact match.
5. **`IdeUninstaller` scope** — implement as part of Phase 6 (Cleanup tab
   is the only consumer). Should mirror `SdkUninstaller` minus the
   stub / environment fallback logic. When uninstalling the active
   version, pick the next-most-recent installed version of that product
   to be the new active; if none, remove the active link AND the
   shortcut.
6. **Branch publish timing** — `feat/jetbrains-ide-installer` is local
   only. Decide whether to push it before or after Phase 7 testing.

Already settled (do NOT re-litigate):

- Project name: `DevToolsManager` (§3.2).
- Self-contained config: not implemented (§3.8).
- Tab structure: `.NET / Rider / WebStorm / Cleanup` (§5.1).
- Multi-version IDE installs: supported, exposed via Cleanup tab (§5.4).
- Bootstrap: invisible, runs on first .NET install (§5.3).
- Smoke test for IDEs: build.txt substring match (Phase 4 decision; see
  open item #4 above for verification).

---

## 12. Current status — resume here

**Branch:** `feat/jetbrains-ide-installer` (local only, not pushed).

**Last verified:** `dotnet build DevToolsManager.slnx` and the same with
`-c Release` both succeed with 0 warnings, 0 errors.

### Done (commits, newest first)

| Phase | Commit  | Summary |
|---|---|---|
| 4 | `863c04a` | JetBrains catalog client + `IdeInstaller` + smoke / discovery / sideload (Core fully wired). |
| 3 | `23c6c3e` | Extracted `ProductInstaller` from `SdkInstaller`; SDK install path unchanged behaviorally. |
| 2 | `159b6cc` | Platform additions: `IdeInstallRoot`, IDE link, `IShellLinkW` shortcut on Windows, `.desktop` + wrapper-script shortcut on Linux. |
| 1 | `71d19e3` | Rebrand: `DotnetSdkManager` → `DevToolsManager` (namespaces, paths, markers, window title). |
|   | `2ed5b32` | Plan revised to user's decisions (drop self-contained config, 4-tab UI). |
|   | `5dc05c8` | Initial plan committed. |

`git log --oneline feat/jetbrains-ide-installer` shows the same.

### Next: Phase 5 — extend `AppState` with `ActiveIdes`

Small. Single-commit-sized. Concrete steps:

1. Open `DevToolsManager.Core/Models/AppState.cs`.
2. Bump `SchemaVersion` default to `2`.
3. Add `public Dictionary<string, string> ActiveIdes { get; set; } = new();`
   (keyed by `JetBrainsProductInfo.Code(product)`, e.g. `"RD"`, value =
   active version like `"2026.1.1"`).
4. Verify `StateManager.Load` round-trips an old (schema-1) state.json
   correctly — current loader already swallows parse failures and returns
   a fresh state, so the worst case is a clean reset, which is acceptable.
   Even better: a missing `ActiveIdes` field will deserialize to the
   default empty dict (System.Text.Json default behavior). No migration
   code needed.
5. `dotnet build` clean.
6. Commit: `feat(state): AppState v2 with ActiveIdes`.

### Then: Phase 6 — UI redesign (the largest remaining phase)

Spec is in §5 and §7-Phase-6. Bring `IdeUninstaller` into this phase
(see open item below) since it's a Cleanup-tab dependency. Expect this to
span more than one session.

### Phase 7 — final smoke-test

Need a clean Windows VM and a clean Linux VM (or fresh user accounts).
Walk through §7-Phase-7 acceptance steps. **Save real Windows / Linux
testing for when both VMs are reachable** — Phase 6 can land before Phase 7
runs.

### Cold-start checklist

If picking up in a fresh session with no chat history:

1. `git switch feat/jetbrains-ide-installer`.
2. `git log --oneline` — confirm the commit table above matches.
3. Re-read this file (§5, §7, §11 are the load-bearing sections; the
   rest is rationale).
4. Re-create the tracking task list. Phases in §7 map 1:1 to tasks:
   Phase 1 → done, Phase 2 → done, Phase 3 → done, Phase 4 → done,
   Phase 5 → next, Phase 6 → pending, Phase 7 → pending.
5. `dotnet build DevToolsManager.slnx` should succeed before adding new
   work.
6. If you change a decision while implementing, edit this file in the
   same commit.

### Open items I'd flag before starting Phase 5

Cross-referenced with §11. Read §11 first if these mention things you
don't recognize.

- **`build.txt` format**: Phase 4's smoke test does substring-match
  defensively because the catalog returns `261.23567.144` while the
  archive likely has `RD-261.23567.144`. Confirm during Phase 7 with a
  real install. If mismatched, fix in `Install/IdeSmokeTest.cs`.
- **`IdeUninstaller`**: not yet built. Plan §4.1 lists it; it's a small
  mirror of `SdkUninstaller` (no env / stub logic — just delete the
  version dir, switch active link if removing the active one, remove
  shortcut if no versions left). Fold into Phase 6 since the Cleanup
  tab is its only consumer.
- **Branch is unpublished**: `feat/jetbrains-ide-installer` exists only
  locally. Push when ready, or keep local until Phase 7 passes.
- **Self-test build.txt now-ish**: optional but cheap — extract
  `build.txt` from a small JetBrains archive (any IDE, smaller than
  Rider's 2GB — IntelliJ Community is ~1GB) before Phase 7 to confirm
  format ahead of full smoke testing.

---

*Edit this file as decisions evolve; treat it as the single source of
truth for the branch.*
