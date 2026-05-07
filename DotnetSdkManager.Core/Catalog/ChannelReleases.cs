using System.Text.Json.Serialization;

namespace DotnetSdkManager.Core.Catalog;

public class SdkFile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("rid")]
    public string Rid { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("hash")]
    public string Hash { get; set; } = "";

    [JsonPropertyName("size")]
    public string Size { get; set; } = "0";
}

public class SdkEntry
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("files")]
    public List<SdkFile> Files { get; set; } = [];
}

public class ChannelRelease
{
    [JsonPropertyName("release-version")]
    public string ReleaseVersion { get; set; } = "";

    [JsonPropertyName("release-date")]
    public string ReleaseDate { get; set; } = "";

    [JsonPropertyName("sdks")]
    public List<SdkEntry> Sdks { get; set; } = [];
}

public class ChannelReleases
{
    [JsonPropertyName("channel-version")]
    public string ChannelVersion { get; set; } = "";

    [JsonPropertyName("releases")]
    public List<ChannelRelease> Releases { get; set; } = [];
}
