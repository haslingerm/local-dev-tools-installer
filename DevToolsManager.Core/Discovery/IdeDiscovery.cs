using DevToolsManager.Core.Models;
using DevToolsManager.Core.Platform;
using DevToolsManager.Core.Util;

namespace DevToolsManager.Core.Discovery;

/// <summary>
/// Scans the managed IDE root (<c>&lt;DataDir&gt;/ides/&lt;slug&gt;/&lt;version&gt;</c>)
/// for installed IDE versions of a given product. Only managed installs are
/// surfaced; system / Toolbox installs are out of scope.
/// </summary>
public sealed class IdeDiscovery
{
    private readonly IPlatformIntegration _platform;

    public IdeDiscovery(IPlatformIntegration platform) => _platform = platform;

    public IReadOnlyList<IdeInfo> Scan(JetBrainsProduct product, string? activeVersion = null)
    {
        var slug = JetBrainsProductInfo.Slug(product);
        var productRoot = Path.Combine(_platform.IdeInstallRoot, slug);
        if (!Directory.Exists(productRoot))
        {
            return [];
        }

        var result = new List<IdeInfo>();
        foreach (var dir in Directory.EnumerateDirectories(productRoot))
        {
            var name = Path.GetFileName(dir);
            // Skip the active link, staging dirs, backups, and anything else.
            if (name == "active" || name.StartsWith('.') || name.Contains(".backup-"))
            {
                continue;
            }
            if (!PathSafety.IsValidIdeVersion(name))
            {
                continue;
            }
            result.Add(new IdeInfo(product, name, dir, name == activeVersion));
        }

        return result.OrderByDescending(i => ParseVersion(i.Version)).ToList();
    }

    private static Version ParseVersion(string v)
    {
        Version.TryParse(v, out var parsed);
        return parsed ?? new Version(0, 0);
    }
}
