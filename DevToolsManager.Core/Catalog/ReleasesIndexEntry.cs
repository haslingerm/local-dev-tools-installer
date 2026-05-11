using System.Text.Json.Serialization;

namespace DevToolsManager.Core.Catalog;

public class ReleasesIndexEntry
{
    [JsonPropertyName("channel-version")]
    public string ChannelVersion { get; set; } = "";

    [JsonPropertyName("latest-release")]
    public string LatestRelease { get; set; } = "";

    [JsonPropertyName("latest-release-date")]
    public string LatestReleaseDate { get; set; } = "";

    [JsonPropertyName("latest-sdk")]
    public string LatestSdk { get; set; } = "";

    [JsonPropertyName("support-phase")]
    public string SupportPhase { get; set; } = "";

    [JsonPropertyName("eol-date")]
    public string? EolDate { get; set; }

    [JsonPropertyName("releases.json")]
    public string ReleasesJsonUrl { get; set; } = "";
}

public class ReleasesIndex
{
    [JsonPropertyName("releases-index")]
    public List<ReleasesIndexEntry> Entries { get; set; } = [];
}
