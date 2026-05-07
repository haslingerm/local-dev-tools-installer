using DotnetSdkManager.Core.Platform;

namespace DotnetSdkManager.Core.Sideload;

public record SideloadEntry(string Version, string Rid, string Path);

public sealed class SideloadScanner
{
    private readonly IPlatformIntegration _platform;

    public SideloadScanner(IPlatformIntegration platform) => _platform = platform;

    public IReadOnlyList<SideloadEntry> Scan()
    {
        var results = new List<SideloadEntry>();
        var exeDir = AppContext.BaseDirectory;

        ScanDirectory(exeDir, results);

        if (Directory.Exists(_platform.SideloadDir))
        {
            ScanDirectory(_platform.SideloadDir, results);
        }

        return results;
    }

    private static void ScanDirectory(string dir, List<SideloadEntry> results)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "dotnet-sdk-*"))
        {
            var entry = TryParse(file);
            if (entry is not null)
            {
                results.Add(entry);
            }
        }
    }

    private static SideloadEntry? TryParse(string filePath)
    {
        var name = Path.GetFileName(filePath);
        if (!name.StartsWith("dotnet-sdk-", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string stripped;
        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            stripped = name[..^7];
        }
        else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            stripped = name[..^4];
        }
        else
        {
            return null;
        }

        var prefix = "dotnet-sdk-";
        var remainder = stripped[prefix.Length..];

        var knownRids = new[] { "win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64" };
        foreach (var rid in knownRids)
        {
            if (remainder.EndsWith("-" + rid, StringComparison.OrdinalIgnoreCase))
            {
                var version = remainder[..^(rid.Length + 1)];
                return new SideloadEntry(version, rid, filePath);
            }
        }

        return null;
    }
}
