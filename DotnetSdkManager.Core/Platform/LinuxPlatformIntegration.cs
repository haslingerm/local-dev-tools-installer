using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DotnetSdkManager.Core.Process;
using DotnetSdkManager.Core.Util;

namespace DotnetSdkManager.Core.Platform;

[SupportedOSPlatform("linux")]
public sealed class LinuxPlatformIntegration : IPlatformIntegration
{
    private readonly IProcessRunner _runner;
    private readonly string _home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly string _dataHome;

    private const string MarkerStart = "# >>> dotnet-sdk-manager >>>";
    private const string MarkerEnd = "# <<< dotnet-sdk-manager <<<";

    public LinuxPlatformIntegration(IProcessRunner runner)
    {
        _runner = runner;
        _dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
            ?? Path.Combine(_home, ".local", "share");
    }

    public string DataDir => Path.Combine(_dataHome, "dotnet-sdk-manager");
    public string InstallRoot => Path.Combine(DataDir, "sdks");
    public string ActiveLinkPath => Path.Combine(DataDir, "active");
    public string CacheDir => Path.Combine(DataDir, "cache");
    public string SideloadDir => Path.Combine(DataDir, "sideload");
    public string ArchiveExtension => ".tar.gz";

    public string CurrentRid =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";

    public IEnumerable<string> SystemSdkScanPaths
    {
        get
        {
            yield return "/usr/share/dotnet/sdk";
            yield return "/usr/lib/dotnet/sdk";
            yield return "/snap/dotnet-sdk/current/sdk";
            yield return Path.Combine(_home, ".dotnet", "sdk");
        }
    }

    public async ValueTask WriteEnvironmentAsync(string activeLinkPath, CancellationToken ct = default)
    {
        var quoted = ShellQuote(activeLinkPath);
        var posixBlock = $"""
            {MarkerStart}
            export DOTNET_ROOT={quoted}
            case ":$PATH:" in *":{quoted}:"*) ;; *) export PATH={quoted}:$PATH ;; esac
            {MarkerEnd}
            """;

        var rcFiles = new List<string>
        {
            Path.Combine(_home, ".profile"),
            Path.Combine(_home, ".bashrc"),
        };

        var zshrc = Path.Combine(_home, ".zshrc");
        if (File.Exists(zshrc))
        {
            rcFiles.Add(zshrc);
        }

        foreach (var rcFile in rcFiles)
        {
            await PatchRcFileAsync(rcFile, posixBlock, ct);
        }

        await WriteFishConfigAsync(activeLinkPath, ct);

        var procPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!procPath.Contains(activeLinkPath, StringComparison.Ordinal))
        {
            Environment.SetEnvironmentVariable(
                "PATH",
                string.IsNullOrEmpty(procPath) ? activeLinkPath : $"{activeLinkPath}:{procPath}");
        }
        Environment.SetEnvironmentVariable("DOTNET_ROOT", activeLinkPath);
    }

    private static async ValueTask PatchRcFileAsync(string path, string block, CancellationToken ct)
    {
        var existing = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : "";

        string updated;
        var startIdx = existing.IndexOf(MarkerStart, StringComparison.Ordinal);
        var endIdx = existing.IndexOf(MarkerEnd, StringComparison.Ordinal);

        if (startIdx >= 0 && endIdx >= 0 && endIdx > startIdx)
        {
            updated = existing[..startIdx] + block + existing[(endIdx + MarkerEnd.Length)..];
        }
        else
        {
            updated = existing.TrimEnd() + Environment.NewLine + Environment.NewLine + block + Environment.NewLine;
        }

        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, updated, ct);
        File.Move(tmp, path, overwrite: true);
    }

    private async ValueTask WriteFishConfigAsync(string activeLinkPath, CancellationToken ct)
    {
        var fishConfDir = Path.Combine(_home, ".config", "fish", "conf.d");
        if (!Directory.Exists(Path.Combine(_home, ".config", "fish")))
        {
            return;
        }

        Directory.CreateDirectory(fishConfDir);
        var quoted = FishQuote(activeLinkPath);
        var content =
            $"""
             # dotnet-sdk-manager
             set -gx DOTNET_ROOT {quoted}
             if not contains -- {quoted} $PATH
                 set -gx PATH {quoted} $PATH
             end
             """ + "\n";

        var fishConfFile = Path.Combine(fishConfDir, "dotnet-sdk-manager.fish");
        var tmp = fishConfFile + ".tmp";
        await File.WriteAllTextAsync(tmp, content, ct);
        File.Move(tmp, fishConfFile, overwrite: true);
    }

    public ValueTask CreateOrUpdateLinkAsync(string targetPath, CancellationToken ct = default)
    {
        var fullTarget = Path.GetFullPath(targetPath);
        if (!Directory.Exists(fullTarget))
        {
            throw new DirectoryNotFoundException($"Link target does not exist: '{fullTarget}'.");
        }

        if (!PathSafety.IsInsideRoot(DataDir, fullTarget) &&
            !IsInsideKnownSystemSdkRoot(fullTarget))
        {
            throw new InvalidOperationException(
                $"Refusing to create active link to '{fullTarget}': not under managed or system SDK root.");
        }

        Directory.CreateDirectory(DataDir);

        if (File.Exists(ActiveLinkPath) || Directory.Exists(ActiveLinkPath))
        {
            File.Delete(ActiveLinkPath);
        }

        File.CreateSymbolicLink(ActiveLinkPath, fullTarget);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<string> RunInShellAsync(string command, int timeoutSeconds = 30, CancellationToken ct = default)
    {
        var result = await _runner.RunAsync("bash", ["-lc", command], timeoutSeconds, ct);
        return result.Stdout;
    }

    public bool IsBootstrapped()
    {
        var profile = Path.Combine(_home, ".profile");
        if (!File.Exists(profile))
        {
            return false;
        }

        var content = File.ReadAllText(profile);
        return content.Contains(MarkerStart, StringComparison.Ordinal);
    }

    private bool IsInsideKnownSystemSdkRoot(string path)
    {
        foreach (var root in SystemSdkScanPaths)
        {
            if (PathSafety.IsInsideRoot(root, path))
            {
                return true;
            }
        }
        return false;
    }

    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\\''") + "'";

    private static string FishQuote(string value) =>
        "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
}
