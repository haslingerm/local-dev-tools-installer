namespace DevToolsManager.Core.Models;

/// <summary>
/// An installed IDE on this machine — discovered by <c>IdeDiscovery</c>.
/// Only managed installs are surfaced; system-wide / Toolbox installs are
/// out of scope.
/// </summary>
public record IdeInfo(
    JetBrainsProduct Product,
    string Version,
    string InstallPath,
    bool IsActive = false);
