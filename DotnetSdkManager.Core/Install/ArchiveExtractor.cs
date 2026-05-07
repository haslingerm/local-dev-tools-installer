using System.Formats.Tar;
using System.IO.Compression;

namespace DotnetSdkManager.Core.Install;

public sealed record ArchiveExtractionLimits(
    long MaxTotalUncompressedBytes = 5L * 1024 * 1024 * 1024,
    long MaxFileBytes = 1L * 1024 * 1024 * 1024,
    int MaxEntries = 200_000,
    bool AllowLinks = false);

public static class ArchiveExtractor
{
    public static async Task ExtractAsync(
        string archivePath,
        string destinationDir,
        ArchiveExtractionLimits? limits = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(destinationDir);
        var fullDest = Path.GetFullPath(destinationDir);

        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            await ExtractZipAsync(archivePath, fullDest, limits, ct);
        }
        else if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            await ExtractTarGzAsync(archivePath, fullDest, limits, ct);
        }
        else
        {
            throw new NotSupportedException($"Unknown archive format: {archivePath}");
        }

        if (OperatingSystem.IsLinux())
        {
            var dotnetExe = Path.Combine(fullDest, "dotnet");
            if (File.Exists(dotnetExe))
            {
                try
                {
                    var mode = File.GetUnixFileMode(dotnetExe);
                    File.SetUnixFileMode(dotnetExe,
                        mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                }
                catch
                {
                    // best-effort
                }
            }
        }
    }

    private static Task ExtractZipAsync(string path, string dest, ArchiveExtractionLimits? limits, CancellationToken ct)
    {
        if (limits is null)
        {
            ZipFile.ExtractToDirectory(path, dest, overwriteFiles: true);
            return Task.CompletedTask;
        }

        using var archive = ZipFile.OpenRead(path);
        long totalBytes = 0;
        var entries = 0;
        var rootWithSep = dest.EndsWith(Path.DirectorySeparatorChar) ? dest : dest + Path.DirectorySeparatorChar;

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            entries++;
            if (entries > limits.MaxEntries)
            {
                throw new InvalidDataException($"Archive exceeds entry limit ({limits.MaxEntries}).");
            }

            if (entry.Length > limits.MaxFileBytes)
            {
                throw new InvalidDataException(
                    $"Archive entry '{entry.FullName}' exceeds per-file limit ({limits.MaxFileBytes} bytes).");
            }

            totalBytes += entry.Length;
            if (totalBytes > limits.MaxTotalUncompressedBytes)
            {
                throw new InvalidDataException(
                    $"Archive total uncompressed size exceeds limit ({limits.MaxTotalUncompressedBytes} bytes).");
            }

            var name = entry.FullName.Replace('\\', '/');
            if (Path.IsPathRooted(name) || name.Split('/').Any(p => p == ".."))
            {
                throw new InvalidDataException($"Archive entry rejects suspicious path: '{entry.FullName}'.");
            }

            var fullTarget = Path.GetFullPath(Path.Combine(dest, name));
            if (!fullTarget.StartsWith(rootWithSep, StringComparison.Ordinal) &&
                !string.Equals(fullTarget, dest, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Archive entry resolves outside destination: '{entry.FullName}'.");
            }

            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(fullTarget);
                continue;
            }

            var dir = Path.GetDirectoryName(fullTarget);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            entry.ExtractToFile(fullTarget, overwrite: true);
        }

        return Task.CompletedTask;
    }

    private static async Task ExtractTarGzAsync(string path, string dest, ArchiveExtractionLimits? limits, CancellationToken ct)
    {
        await using var fileStream = File.OpenRead(path);
        await using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);

        if (limits is null)
        {
            await TarFile.ExtractToDirectoryAsync(gzipStream, dest, overwriteFiles: true, cancellationToken: ct);
            return;
        }

        await using var tarReader = new TarReader(gzipStream, leaveOpen: true);
        long totalBytes = 0;
        var entries = 0;
        var rootWithSep = dest.EndsWith(Path.DirectorySeparatorChar) ? dest : dest + Path.DirectorySeparatorChar;

        TarEntry? entry;
        while ((entry = await tarReader.GetNextEntryAsync(copyData: false, ct)) is not null)
        {
            ct.ThrowIfCancellationRequested();
            entries++;
            if (entries > limits.MaxEntries)
            {
                throw new InvalidDataException($"Archive exceeds entry limit ({limits.MaxEntries}).");
            }

            var name = entry.Name.Replace('\\', '/');
            if (Path.IsPathRooted(name) || name.Split('/').Any(p => p == ".."))
            {
                throw new InvalidDataException($"Archive entry rejects suspicious path: '{entry.Name}'.");
            }

            var fullTarget = Path.GetFullPath(Path.Combine(dest, name));
            if (!fullTarget.StartsWith(rootWithSep, StringComparison.Ordinal) &&
                !string.Equals(fullTarget, dest, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Archive entry resolves outside destination: '{entry.Name}'.");
            }

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                case TarEntryType.DirectoryList:
                    Directory.CreateDirectory(fullTarget);
                    break;

                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                case TarEntryType.ContiguousFile:
                {
                    if (entry.Length > limits.MaxFileBytes)
                    {
                        throw new InvalidDataException(
                            $"Archive entry '{entry.Name}' exceeds per-file limit ({limits.MaxFileBytes} bytes).");
                    }

                    totalBytes += entry.Length;
                    if (totalBytes > limits.MaxTotalUncompressedBytes)
                    {
                        throw new InvalidDataException(
                            $"Archive total uncompressed size exceeds limit ({limits.MaxTotalUncompressedBytes} bytes).");
                    }

                    var dir = Path.GetDirectoryName(fullTarget);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    await entry.ExtractToFileAsync(fullTarget, overwrite: true, ct);
                    break;
                }

                case TarEntryType.SymbolicLink:
                case TarEntryType.HardLink:
                    if (!limits.AllowLinks)
                    {
                        throw new InvalidDataException(
                            $"Archive contains link entry which is not allowed for unverified archives: '{entry.Name}' -> '{entry.LinkName}'.");
                    }
                    break;

                default:
                    // skip block/char devices, fifos, etc.
                    break;
            }
        }
    }
}
