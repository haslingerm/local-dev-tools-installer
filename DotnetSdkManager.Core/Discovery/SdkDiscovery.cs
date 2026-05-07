using DotnetSdkManager.Core.Models;
using DotnetSdkManager.Core.Platform;

namespace DotnetSdkManager.Core.Discovery;

public sealed class SdkDiscovery
{
    private readonly IPlatformIntegration _platform;

    public SdkDiscovery(IPlatformIntegration platform) => _platform = platform;

    public IReadOnlyList<SdkInfo> Scan(string? activeVersion)
    {
        var result = new List<SdkInfo>();

        // Managed SDKs first so a duplicate version in a system path can't hide a managed install.
        // The active flag is restricted to managed SDKs because only managed paths can be the
        // current symlink/junction target.
        if (Directory.Exists(_platform.InstallRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(_platform.InstallRoot))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith('.') || !IsValidVersion(name))
                {
                    continue;
                }

                result.Add(new SdkInfo(name, dir, SdkSource.Managed, name == activeVersion));
            }
        }

        foreach (var scanPath in _platform.SystemSdkScanPaths)
        {
            if (!Directory.Exists(scanPath))
            {
                continue;
            }

            foreach (var dir in Directory.EnumerateDirectories(scanPath))
            {
                var version = Path.GetFileName(dir);
                if (!IsValidVersion(version))
                {
                    continue;
                }

                result.Add(new SdkInfo(version, dir, SdkSource.SystemInstalled, IsActive: false));
            }
        }

        return result.OrderByDescending(s => ParseVersion(s.Version))
                     .ThenBy(s => s.Source == SdkSource.Managed ? 0 : 1)
                     .ToList();
    }

    private static bool IsValidVersion(string name) =>
        name.Length > 0 && char.IsDigit(name[0]);

    private static Version ParseVersion(string v)
    {
        Version.TryParse(v.Split('-')[0], out var parsed);
        return parsed ?? new Version(0, 0);
    }
}
