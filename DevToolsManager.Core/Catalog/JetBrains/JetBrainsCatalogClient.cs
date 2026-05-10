using System.Globalization;
using System.Text.Json;
using DevToolsManager.Core.Models;
using DevToolsManager.Core.Platform;
using DevToolsManager.Core.Util;

namespace DevToolsManager.Core.Catalog.JetBrains;

/// <summary>
/// Catalog client for the JetBrains data-services releases endpoint.
/// One client serves all products — the product code is just a query parameter.
/// Caches responses on disk for 24 hours; falls back to stale cache on network
/// failure (same model as the .NET <c>ReleasesCatalogClient</c>).
/// </summary>
public sealed class JetBrainsCatalogClient
{
    private const string EndpointFormat =
        "https://data.services.jetbrains.com/products/releases?code={0}&type=release";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly IPlatformIntegration _platform;

    public JetBrainsCatalogClient(HttpClient http, IPlatformIntegration platform)
    {
        _http = http;
        _platform = platform;
    }

    /// <summary>
    /// Returns release entries for the given product, filtered to platform-appropriate
    /// downloads (Windows zip on win-x64/arm64, Linux tar.gz on linux-x64/arm64).
    /// Releases without a download for the current RID are skipped.
    /// </summary>
    public async Task<IReadOnlyList<IdeRelease>> GetReleasesAsync(
        JetBrainsProduct product,
        bool latestOnly = false,
        CancellationToken ct = default)
    {
        var code = JetBrainsProductInfo.Code(product);
        var url = string.Format(CultureInfo.InvariantCulture, EndpointFormat, code);
        if (latestOnly)
        {
            url += "&latest=true";
        }

        var cacheKey = $"jetbrains-{code.ToLowerInvariant()}{(latestOnly ? "-latest" : "")}.json";
        var json = await FetchAsync(cacheKey, url, ct);

        var doc = JsonSerializer.Deserialize<Dictionary<string, List<JetBrainsReleaseDto>>>(json, JsonOpts);
        if (doc is null || !doc.TryGetValue(code, out var releases))
        {
            return [];
        }

        var downloadKey = DownloadKeyForRid(_platform.CurrentRid);
        var result = new List<IdeRelease>();

        foreach (var dto in releases)
        {
            if (!PathSafety.IsValidIdeVersion(dto.Version))
            {
                continue;
            }
            if (!dto.Downloads.TryGetValue(downloadKey, out var dl) || string.IsNullOrEmpty(dl.Link))
            {
                continue;
            }

            string fileName;
            try
            {
                fileName = Path.GetFileName(new Uri(dl.Link).AbsolutePath);
                PathSafety.RequireValidFileName(fileName, nameof(fileName));
            }
            catch
            {
                continue;
            }

            result.Add(new IdeRelease(
                Product: product,
                Version: dto.Version,
                Build: dto.Build,
                DownloadUrl: dl.Link,
                ChecksumUrl: dl.ChecksumLink ?? (dl.Link + ".sha256"),
                Size: dl.Size,
                FileName: fileName));
        }

        return result;
    }

    /// <summary>
    /// Fetches the SHA-256 sidecar (<c>&lt;archive&gt;.sha256</c>) and returns the
    /// hex digest. JetBrains sidecars are <c>&lt;digest&gt;  &lt;filename&gt;</c>;
    /// only the first whitespace-delimited token is the digest.
    /// </summary>
    public async Task<string> FetchSha256Async(string checksumUrl, CancellationToken ct = default)
    {
        // Small file (~120 bytes); not worth a disk cache. One GET per install.
        using var response = await _http.GetAsync(checksumUrl, ct);
        response.EnsureSuccessStatusCode();
        var content = (await response.Content.ReadAsStringAsync(ct)).Trim();

        var firstWhitespace = content.IndexOfAny([' ', '\t', '\r', '\n']);
        return firstWhitespace < 0 ? content : content[..firstWhitespace];
    }

    private async Task<string> FetchAsync(string cacheKey, string url, CancellationToken ct)
    {
        Directory.CreateDirectory(_platform.CacheDir);
        var cachePath = PathSafety.CombineSafe(_platform.CacheDir, cacheKey);

        if (File.Exists(cachePath))
        {
            var lastWrite = File.GetLastWriteTimeUtc(cachePath);
            if (DateTime.UtcNow - lastWrite < CacheTtl)
            {
                return await File.ReadAllTextAsync(cachePath, ct);
            }
        }

        try
        {
            var content = await _http.GetStringAsync(url, ct);
            await File.WriteAllTextAsync(cachePath, content, ct);
            return content;
        }
        catch (HttpRequestException) when (File.Exists(cachePath))
        {
            return await File.ReadAllTextAsync(cachePath, ct);
        }
    }

    private static string DownloadKeyForRid(string rid) => rid switch
    {
        "win-x64" => "windowsZip",
        "win-arm64" => "windowsZipARM64",
        "linux-x64" => "linux",
        "linux-arm64" => "linuxARM64",
        _ => throw new PlatformNotSupportedException(
            $"JetBrains catalog has no download key for RID '{rid}'."),
    };
}
