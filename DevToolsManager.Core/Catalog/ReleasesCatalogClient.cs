using System.Net.Http.Headers;
using System.Text.Json;
using DevToolsManager.Core.Models;
using DevToolsManager.Core.Platform;
using DevToolsManager.Core.Util;

namespace DevToolsManager.Core.Catalog;

public sealed class ReleasesCatalogClient
{
    private const string IndexUrl = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly IPlatformIntegration _platform;

    public ReleasesCatalogClient(HttpClient http, IPlatformIntegration platform)
    {
        _http = http;
        _platform = platform;
    }

    public async Task<IReadOnlyList<ReleasesIndexEntry>> GetIndexAsync(CancellationToken ct = default)
    {
        var json = await FetchWithCacheAsync("releases-index.json", IndexUrl, ct);
        var index = JsonSerializer.Deserialize<ReleasesIndex>(json, JsonOpts);
        return index?.Entries ?? [];
    }

    public async Task<IReadOnlyList<SdkRelease>> GetReleasesForChannelAsync(
        ReleasesIndexEntry channel,
        CancellationToken ct = default)
    {
        PathSafety.RequireValidChannelVersion(channel.ChannelVersion, nameof(channel.ChannelVersion));

        var rid = _platform.CurrentRid;
        var cacheKey = $"releases-{channel.ChannelVersion}.json";
        var json = await FetchWithCacheAsync(cacheKey, channel.ReleasesJsonUrl, ct);

        var data = JsonSerializer.Deserialize<ChannelReleases>(json, JsonOpts);
        if (data is null)
        {
            return [];
        }

        var releases = new List<SdkRelease>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var release in data.Releases)
        {
            foreach (var sdk in release.Sdks)
            {
                if (!PathSafety.IsValidSdkVersion(sdk.Version))
                {
                    continue;
                }

                if (!seen.Add(sdk.Version))
                {
                    continue;
                }

                var file = sdk.Files.FirstOrDefault(f =>
                    f.Rid == rid &&
                    !f.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                    !f.Name.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase));

                if (file is null)
                {
                    continue;
                }

                try
                {
                    PathSafety.RequireValidFileName(file.Name, nameof(file.Name));
                }
                catch (ArgumentException)
                {
                    continue;
                }

                long.TryParse(file.Size, out var size);
                releases.Add(new SdkRelease(
                    Version: sdk.Version,
                    ChannelVersion: channel.ChannelVersion,
                    DownloadUrl: file.Url,
                    Hash: file.Hash,
                    Size: size,
                    FileName: file.Name));
            }
        }

        return releases;
    }

    private async Task<string> FetchWithCacheAsync(string cacheKey, string url, CancellationToken ct)
    {
        Directory.CreateDirectory(_platform.CacheDir);
        var cachePath = PathSafety.CombineSafe(_platform.CacheDir, cacheKey);
        var etagPath = cachePath + ".etag";

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
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (File.Exists(cachePath) && File.Exists(etagPath))
            {
                var etag = (await File.ReadAllTextAsync(etagPath, ct)).Trim();
                if (!string.IsNullOrEmpty(etag))
                {
                    try { request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag, true)); }
                    catch (FormatException) { /* corrupt etag — fetch fresh */ }
                }
            }

            var response = await _http.SendAsync(request, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.NotModified && File.Exists(cachePath))
            {
                File.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow);
                return await File.ReadAllTextAsync(cachePath, ct);
            }

            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(ct);
            await File.WriteAllTextAsync(cachePath, content, ct);

            var responseEtag = response.Headers.ETag?.Tag;
            if (responseEtag is not null)
            {
                await File.WriteAllTextAsync(etagPath, responseEtag, ct);
            }

            return content;
        }
        catch (HttpRequestException) when (File.Exists(cachePath))
        {
            return await File.ReadAllTextAsync(cachePath, ct);
        }
    }
}
