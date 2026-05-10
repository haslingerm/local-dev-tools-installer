namespace DevToolsManager.Core.Models;

/// <summary>
/// A single IDE release surfaced by <c>JetBrainsCatalogClient</c> (or constructed
/// in-place for sideload scenarios). Mirror of <see cref="SdkRelease"/> for IDEs.
/// The hash is fetched lazily at install time from the sidecar .sha256 URL —
/// it is not stored on this record.
/// </summary>
public record IdeRelease(
    JetBrainsProduct Product,
    string Version,
    string Build,
    string DownloadUrl,
    string ChecksumUrl,
    long Size,
    string FileName)
{
    public bool IsInstalled { get; init; }
    public string? SideloadPath { get; init; }
    public bool HasSideload => SideloadPath is not null;
}
