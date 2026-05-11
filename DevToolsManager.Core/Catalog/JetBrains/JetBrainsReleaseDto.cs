using System.Text.Json.Serialization;

namespace DevToolsManager.Core.Catalog.JetBrains;

/// <summary>
/// Per-platform download entry under <c>release.downloads.&lt;key&gt;</c> in the
/// JetBrains data-services releases response. Keys we care about:
/// <c>windowsZip</c>, <c>windowsZipARM64</c>, <c>linux</c>, <c>linuxARM64</c>.
/// </summary>
public sealed class JetBrainsDownloadDto
{
    [JsonPropertyName("link")]
    public string Link { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("checksumLink")]
    public string? ChecksumLink { get; set; }
}

/// <summary>
/// One release entry in the JetBrains data-services response. Many fields the
/// API returns (<c>whatsnew</c>, <c>patches</c>, <c>uninstallFeedbackLinks</c>,
/// <c>licenseRequired</c>, …) are intentionally omitted — we only model what we
/// need.
/// </summary>
public sealed class JetBrainsReleaseDto
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("build")]
    public string Build { get; set; } = "";

    [JsonPropertyName("downloads")]
    public Dictionary<string, JetBrainsDownloadDto> Downloads { get; set; } = new();
}
