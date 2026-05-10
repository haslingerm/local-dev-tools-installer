using DevToolsManager.Core.Models;
using DevToolsManager.Core.Platform;
using DevToolsManager.Core.Util;

namespace DevToolsManager.Core.Sideload;

public record IdeSideloadEntry(JetBrainsProduct Product, string Version, string Path);

/// <summary>
/// Scans for IDE archives placed next to the app executable or in the
/// <c>sideload-ides/</c> sub-folder of the data dir. Filenames must match
/// JetBrains' default download naming, e.g. <c>Rider-2026.1.1.win.zip</c> or
/// <c>WebStorm-2026.1.0.tar.gz</c>; <c>-aarch64</c> arch suffix is tolerated.
/// </summary>
public sealed class IdeSideloadScanner
{
    private static readonly (string Prefix, JetBrainsProduct Product)[] Prefixes =
    [
        ("Rider-",     JetBrainsProduct.Rider),
        ("WebStorm-",  JetBrainsProduct.WebStorm),
    ];

    private readonly IPlatformIntegration _platform;

    public IdeSideloadScanner(IPlatformIntegration platform) => _platform = platform;

    public IReadOnlyList<IdeSideloadEntry> Scan()
    {
        var results = new List<IdeSideloadEntry>();
        ScanDirectory(AppContext.BaseDirectory, results);
        if (Directory.Exists(_platform.IdeSideloadDir))
        {
            ScanDirectory(_platform.IdeSideloadDir, results);
        }
        return results;
    }

    private static void ScanDirectory(string dir, List<IdeSideloadEntry> results)
    {
        foreach (var (prefix, product) in Prefixes)
        {
            foreach (var file in Directory.EnumerateFiles(dir, $"{prefix}*"))
            {
                var entry = TryParse(file, prefix, product);
                if (entry is not null)
                {
                    results.Add(entry);
                }
            }
        }
    }

    private static IdeSideloadEntry? TryParse(string filePath, string prefix, JetBrainsProduct product)
    {
        var name = Path.GetFileName(filePath);
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string stripped;
        if (name.EndsWith(".win.zip", StringComparison.OrdinalIgnoreCase))
        {
            stripped = name[..^8];
        }
        else if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            stripped = name[..^7];
        }
        else
        {
            return null;
        }

        var versionPart = stripped[prefix.Length..];
        if (versionPart.EndsWith("-aarch64", StringComparison.OrdinalIgnoreCase))
        {
            versionPart = versionPart[..^"-aarch64".Length];
        }

        return PathSafety.IsValidIdeVersion(versionPart)
            ? new IdeSideloadEntry(product, versionPart, filePath)
            : null;
    }
}
