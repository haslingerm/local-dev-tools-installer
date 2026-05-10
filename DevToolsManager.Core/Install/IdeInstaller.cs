using System.Security.Cryptography;
using DevToolsManager.Core.Catalog.JetBrains;
using DevToolsManager.Core.Models;
using DevToolsManager.Core.Platform;
using DevToolsManager.Core.Util;

namespace DevToolsManager.Core.Install;

/// <summary>
/// Adapts a JetBrains <see cref="IdeRelease"/> to the shared
/// <see cref="ProductInstaller"/> pipeline, plus the post-install glue
/// (active link + Start Menu / .desktop shortcut). SHA-256 sidecar is fetched
/// at install time, not stored on the release record.
/// </summary>
public sealed class IdeInstaller
{
    private readonly IPlatformIntegration _platform;
    private readonly ProductInstaller _productInstaller;
    private readonly JetBrainsCatalogClient _catalog;
    private readonly IdeSmokeTest _smokeTest = new();

    public IdeInstaller(
        IPlatformIntegration platform,
        ProductInstaller productInstaller,
        JetBrainsCatalogClient catalog)
    {
        _platform = platform;
        _productInstaller = productInstaller;
        _catalog = catalog;
    }

    public async Task<string> InstallAsync(
        IdeRelease release,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        PathSafety.RequireValidIdeVersion(release.Version, nameof(release.Version));

        var slug = JetBrainsProductInfo.Slug(release.Product);
        var productRoot = PathSafety.CombineSafe(_platform.IdeInstallRoot, slug);
        Directory.CreateDirectory(productRoot);
        var targetInstallDir = PathSafety.CombineSafe(productRoot, release.Version);

        // Fetch the SHA-256 sidecar before kicking off the (much larger) archive
        // download — fail fast if the catalog is broken.
        var isFromCatalog = release.SideloadPath is null;
        var expectedHash = "";
        if (isFromCatalog)
        {
            expectedHash = await _catalog.FetchSha256Async(release.ChecksumUrl, ct);
        }

        var displayName = JetBrainsProductInfo.DisplayName(release.Product);
        var request = new InstallRequest(
            DownloadUrl: release.DownloadUrl,
            FileName: release.FileName,
            ExpectedHash: expectedHash,
            HashAlgorithm: HashAlgorithmName.SHA256,
            ExpectedSize: release.Size,
            IsHashVerified: isFromCatalog,
            SideloadPath: release.SideloadPath,
            TargetInstallDir: targetInstallDir,
            SmokeTest: (stagingDir, c) => _smokeTest.TestInstallAsync(
                ResolveIdeRoot(stagingDir), release.Build, c),
            ExtractLimits: isFromCatalog ? null : new ArchiveExtractionLimits(),
            CompletionMessage: $"{displayName} {release.Version} installed.");

        var installedDir = await _productInstaller.InstallAsync(request, progress, ct);

        // The .tar.gz path on Linux usually wraps everything in a single inner
        // directory ("JetBrains Rider-2026.1.1/"); the .win.zip on Windows
        // typically does not. Resolve the actual IDE root either way.
        var ideRoot = ResolveIdeRoot(installedDir);

        // Atomic version switch via the per-product active link, then the
        // user-facing shortcut points at active/bin/...
        await _platform.CreateOrUpdateIdeLinkAsync(slug, ideRoot, ct);

        var activeRoot = Path.Combine(productRoot, "active");
        await _platform.CreateOrUpdateShortcutAsync(BuildShortcutSpec(release.Product, activeRoot), ct);

        return installedDir;
    }

    /// <summary>
    /// Returns the directory that contains <c>build.txt</c> — the actual IDE
    /// root, regardless of whether the archive extracted directly or via a
    /// single inner directory.
    /// </summary>
    public static string ResolveIdeRoot(string extractedDir)
    {
        if (File.Exists(Path.Combine(extractedDir, "build.txt")))
        {
            return extractedDir;
        }

        // Pull at most two children — if there's exactly one and it has build.txt,
        // that's the inner dir we want.
        var subs = Directory.EnumerateDirectories(extractedDir).Take(2).ToList();
        if (subs.Count == 1 && File.Exists(Path.Combine(subs[0], "build.txt")))
        {
            return subs[0];
        }

        // Fall back: the smoke test will surface a clear error pointing at this dir.
        return extractedDir;
    }

    private static IdeShortcutSpec BuildShortcutSpec(JetBrainsProduct product, string activeRoot)
    {
        var launcher = Path.Combine(activeRoot, JetBrainsProductInfo.LauncherForCurrentOs(product));
        var icon = Path.Combine(activeRoot, JetBrainsProductInfo.IconForCurrentOs(product));
        return new IdeShortcutSpec(
            ProductSlug: JetBrainsProductInfo.Slug(product),
            DisplayName: JetBrainsProductInfo.DisplayName(product),
            ExecutablePath: launcher,
            IconPath: icon,
            StartupWmClass: JetBrainsProductInfo.StartupWmClass(product),
            Comment: JetBrainsProductInfo.Comment(product));
    }
}
