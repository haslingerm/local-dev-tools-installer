using System.Security.Cryptography;
using DevToolsManager.Core.Models;
using DevToolsManager.Core.Platform;
using DevToolsManager.Core.Util;

namespace DevToolsManager.Core.Install;

/// <summary>
/// Product-agnostic install pipeline shared by SDK and IDE installers.
/// Handles download → hash verify → bounded extract → smoke test → atomic swap.
/// Knows nothing about .NET versions, JetBrains products, or post-install glue.
/// Callers wrap it with product-specific logic.
/// </summary>
public sealed class ProductInstaller
{
    private const long DownloadSizeTolerance = 64 * 1024;

    private readonly HttpClient _http;
    private readonly IPlatformIntegration _platform;

    public ProductInstaller(HttpClient http, IPlatformIntegration platform)
    {
        _http = http;
        _platform = platform;
    }

    public async Task<string> InstallAsync(
        InstallRequest request,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var targetInstallDir = Path.GetFullPath(request.TargetInstallDir);
        var parent = Path.GetDirectoryName(targetInstallDir)
            ?? throw new ArgumentException(
                $"TargetInstallDir '{request.TargetInstallDir}' has no parent directory.",
                nameof(request));

        Directory.CreateDirectory(parent);
        var versionLeaf = Path.GetFileName(targetInstallDir);
        var stagingDir = Path.Combine(parent, $".staging-{versionLeaf}-{Guid.NewGuid():N}");
        var backupDir = targetInstallDir + ".backup-" + Guid.NewGuid().ToString("N");

        string archivePath;
        string? downloadedPath = null;
        if (request.SideloadPath is not null)
        {
            archivePath = request.SideloadPath;
        }
        else
        {
            PathSafety.RequireValidFileName(request.FileName, nameof(request.FileName));
            archivePath = await DownloadAsync(request, progress, ct);
            downloadedPath = archivePath;
        }

        try
        {
            await VerifyHashAsync(
                archivePath,
                request.ExpectedHash,
                request.HashAlgorithm,
                request.IsHashVerified,
                progress,
                ct);

            progress?.Report(new InstallProgress(0, null, InstallPhase.Extracting, "Extracting..."));
            await ArchiveExtractor.ExtractAsync(archivePath, stagingDir, request.ExtractLimits, ct);

            progress?.Report(new InstallProgress(0, null, InstallPhase.SmokeTesting, "Running smoke test..."));
            var (ok, output) = await request.SmokeTest(stagingDir, ct);
            if (!ok)
            {
                throw new InvalidOperationException($"Smoke test failed:\n{output}");
            }

            var hadExisting = Directory.Exists(targetInstallDir);
            if (hadExisting)
            {
                Directory.Move(targetInstallDir, backupDir);
            }

            try
            {
                Directory.Move(stagingDir, targetInstallDir);
            }
            catch
            {
                if (hadExisting && Directory.Exists(backupDir))
                {
                    TryDeleteDirectory(targetInstallDir);
                    Directory.Move(backupDir, targetInstallDir);
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

        progress?.Report(new InstallProgress(0, null, InstallPhase.Done, request.CompletionMessage));
        return targetInstallDir;
    }

    private async Task<string> DownloadAsync(
        InstallRequest request,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(_platform.CacheDir);
        var partPath = PathSafety.CombineSafe(_platform.CacheDir, request.FileName + ".part");
        var finalPath = PathSafety.CombineSafe(_platform.CacheDir, request.FileName);

        using var response = await _http.GetAsync(
            request.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength
            ?? (request.ExpectedSize > 0 ? request.ExpectedSize : (long?)null);
        var maxAllowed = request.ExpectedSize > 0
            ? request.ExpectedSize + DownloadSizeTolerance
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
                        $"Download exceeds expected size {request.ExpectedSize} bytes (received {downloaded}).");
                }
                await dest.WriteAsync(buf.AsMemory(0, read), ct);
                progress?.Report(new InstallProgress(downloaded, total, InstallPhase.Downloading));
            }

            if (request.ExpectedSize > 0 && downloaded < request.ExpectedSize - DownloadSizeTolerance)
            {
                throw new InvalidOperationException(
                    $"Download truncated: expected {request.ExpectedSize} bytes, received {downloaded}.");
            }
        }

        File.Move(partPath, finalPath, overwrite: true);
        return finalPath;
    }

    private static async Task VerifyHashAsync(
        string path,
        string expectedHash,
        HashAlgorithmName algorithm,
        bool required,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(expectedHash))
        {
            return;
        }

        progress?.Report(new InstallProgress(
            0, null, InstallPhase.Verifying, $"Verifying {algorithm.Name}..."));

        await using var stream = File.OpenRead(path);
        using var sha = IncrementalHash.CreateHash(algorithm);
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
                throw new InvalidOperationException(
                    $"Hash mismatch.\nExpected: {expectedHash}\nActual:   {actual}");
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
