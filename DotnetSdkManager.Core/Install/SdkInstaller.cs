using System.Security.Cryptography;
using DotnetSdkManager.Core.Models;
using DotnetSdkManager.Core.Platform;
using DotnetSdkManager.Core.Process;
using DotnetSdkManager.Core.Util;

namespace DotnetSdkManager.Core.Install;

public sealed class SdkInstaller
{
    private const long DownloadSizeTolerance = 64 * 1024;

    private readonly HttpClient _http;
    private readonly IPlatformIntegration _platform;
    private readonly SdkSmokeTest _smokeTest;

    public SdkInstaller(HttpClient http, IPlatformIntegration platform, IProcessRunner runner)
    {
        _http = http;
        _platform = platform;
        _smokeTest = new SdkSmokeTest(runner);
    }

    public async Task<string> InstallAsync(
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
        var installDir = PathSafety.CombineSafe(_platform.InstallRoot, release.Version);
        var stagingDir = PathSafety.CombineSafe(_platform.InstallRoot, $".staging-{release.Version}-{Guid.NewGuid():N}");
        var backupDir = installDir + ".backup-" + Guid.NewGuid().ToString("N");

        string archivePath;
        string? downloadedPath = null;
        if (release.SideloadPath is not null)
        {
            archivePath = release.SideloadPath;
        }
        else
        {
            PathSafety.RequireValidFileName(release.FileName, nameof(release.FileName));
            archivePath = await DownloadAsync(release, progress, ct);
            downloadedPath = archivePath;
        }

        try
        {
            await VerifyHashAsync(archivePath, release.Hash, release.IsHashVerified, progress, ct);

            progress?.Report(new InstallProgress(0, null, InstallPhase.Extracting, "Extracting..."));
            var limits = release.IsHashVerified ? null : new ArchiveExtractionLimits();
            await ArchiveExtractor.ExtractAsync(archivePath, stagingDir, limits, ct);

            progress?.Report(new InstallProgress(0, null, InstallPhase.SmokeTesting, "Running smoke test..."));
            var (ok, output) = await _smokeTest.TestInstallAsync(stagingDir, release.Version, ct);
            if (!ok)
            {
                throw new InvalidOperationException($"Smoke test failed:\n{output}");
            }

            var hadExisting = Directory.Exists(installDir);
            if (hadExisting)
            {
                Directory.Move(installDir, backupDir);
            }

            try
            {
                Directory.Move(stagingDir, installDir);
            }
            catch
            {
                if (hadExisting && Directory.Exists(backupDir))
                {
                    TryDeleteDirectory(installDir);
                    Directory.Move(backupDir, installDir);
                }
                throw;
            }

            if (hadExisting)
            {
                TryDeleteDirectory(backupDir);
            }
        }
        catch
        {
            TryDeleteDirectory(stagingDir);
            throw;
        }
        finally
        {
            if (downloadedPath is not null && File.Exists(downloadedPath))
            {
                try { File.Delete(downloadedPath); } catch { /* best-effort */ }
            }
        }

        progress?.Report(new InstallProgress(0, null, InstallPhase.Done, $"SDK {release.Version} installed successfully."));
        return installDir;
    }

    private async Task<string> DownloadAsync(
        SdkRelease release,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(_platform.CacheDir);
        var partPath = PathSafety.CombineSafe(_platform.CacheDir, release.FileName + ".part");
        var finalPath = PathSafety.CombineSafe(_platform.CacheDir, release.FileName);

        using var response = await _http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? (release.Size > 0 ? release.Size : (long?)null);
        var maxAllowed = release.Size > 0
            ? release.Size + DownloadSizeTolerance
            : long.MaxValue;

        await using (var src = await response.Content.ReadAsStreamAsync(ct))
        await using (var dest = File.Create(partPath))
        {
            var buf = new byte[81920];
            long downloaded = 0;
            int read;

            while ((read = await src.ReadAsync(buf, ct)) > 0)
            {
                downloaded += read;
                if (downloaded > maxAllowed)
                {
                    throw new InvalidOperationException(
                        $"Download exceeds expected size {release.Size} bytes (received {downloaded}).");
                }
                await dest.WriteAsync(buf.AsMemory(0, read), ct);
                progress?.Report(new InstallProgress(downloaded, total, InstallPhase.Downloading));
            }

            if (release.Size > 0 && downloaded < release.Size - DownloadSizeTolerance)
            {
                throw new InvalidOperationException(
                    $"Download truncated: expected {release.Size} bytes, received {downloaded}.");
            }
        }

        File.Move(partPath, finalPath, overwrite: true);
        return finalPath;
    }

    private static async Task VerifyHashAsync(
        string path,
        string expectedHash,
        bool required,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(expectedHash))
        {
            return;
        }

        progress?.Report(new InstallProgress(0, null, InstallPhase.Verifying, "Verifying SHA-512..."));

        await using var stream = File.OpenRead(path);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        var buf = new byte[81920];
        int read;

        while ((read = await stream.ReadAsync(buf, ct)) > 0)
        {
            sha.AppendData(buf, 0, read);
        }

        var actual = Convert.ToHexString(sha.GetHashAndReset());
        if (!actual.Equals(expectedHash.Replace("-", ""), StringComparison.OrdinalIgnoreCase))
        {
            if (required)
            {
                throw new InvalidOperationException($"Hash mismatch.\nExpected: {expectedHash}\nActual:   {actual}");
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
