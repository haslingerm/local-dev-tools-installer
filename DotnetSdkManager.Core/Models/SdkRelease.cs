namespace DotnetSdkManager.Core.Models;

public record SdkRelease(
    string Version,
    string ChannelVersion,
    string DownloadUrl,
    string Hash,
    long Size,
    string FileName
)
{
    public bool IsInstalled { get; init; }
    public string? SideloadPath { get; init; }
    public bool HasSideload => SideloadPath is not null;
    public bool IsHashVerified { get; init; } = true;
}
