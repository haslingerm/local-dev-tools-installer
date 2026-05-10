using System.Security.Cryptography;
using DevToolsManager.Core.Models;
using DevToolsManager.Core.Platform;
using DevToolsManager.Core.Process;
using DevToolsManager.Core.Util;

namespace DevToolsManager.Core.Install;

/// <summary>
/// Adapts a .NET SDK release to the shared <see cref="ProductInstaller"/> pipeline.
/// All download / verify / extract / swap mechanics live in the shared installer;
/// this class only carries SDK-specific validation, smoke testing, and labeling.
/// </summary>
public sealed class SdkInstaller
{
    private readonly IPlatformIntegration _platform;
    private readonly ProductInstaller _productInstaller;
    private readonly SdkSmokeTest _smokeTest;

    public SdkInstaller(
        IPlatformIntegration platform,
        ProductInstaller productInstaller,
        IProcessRunner runner)
    {
        _platform = platform;
        _productInstaller = productInstaller;
        _smokeTest = new SdkSmokeTest(runner);
    }

    public Task<string> InstallAsync(
        SdkRelease release,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        PathSafety.RequireValidSdkVersion(release.Version, nameof(release.Version));
        if (!string.IsNullOrEmpty(release.ChannelVersion))
        {
            PathSafety.RequireValidChannelVersion(release.ChannelVersion, nameof(release.ChannelVersion));
        }

        Directory.CreateDirectory(_platform.InstallRoot);
        var targetInstallDir = PathSafety.CombineSafe(_platform.InstallRoot, release.Version);

        var request = new InstallRequest(
            DownloadUrl: release.DownloadUrl,
            FileName: release.FileName,
            ExpectedHash: release.Hash,
            HashAlgorithm: HashAlgorithmName.SHA512,
            ExpectedSize: release.Size,
            IsHashVerified: release.IsHashVerified,
            SideloadPath: release.SideloadPath,
            TargetInstallDir: targetInstallDir,
            SmokeTest: (dir, c) => _smokeTest.TestInstallAsync(dir, release.Version, c),
            ExtractLimits: release.IsHashVerified ? null : new ArchiveExtractionLimits(),
            CompletionMessage: $"SDK {release.Version} installed successfully.");

        return _productInstaller.InstallAsync(request, progress, ct);
    }
}
