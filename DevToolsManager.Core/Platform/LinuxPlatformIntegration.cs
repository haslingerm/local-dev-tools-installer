using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DevToolsManager.Core.Process;
using DevToolsManager.Core.Util;

namespace DevToolsManager.Core.Platform;

[SupportedOSPlatform("linux")]
public sealed class LinuxPlatformIntegration : IPlatformIntegration
{
    private readonly IProcessRunner _runner;
    private readonly string _home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly string _dataHome;

    private const string MarkerStart = "# >>> dev-tools-manager >>>";
    private const string MarkerEnd = "# <<< dev-tools-manager <<<";

    public LinuxPlatformIntegration(IProcessRunner runner)
    {
        _runner = runner;
        _dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
            ?? Path.Combine(_home, ".local", "share");
    }

    public string DataDir => Path.Combine(_dataHome, "dev-tools-manager");
    public string InstallRoot => Path.Combine(DataDir, "sdks");
    public string ActiveLinkPath => Path.Combine(DataDir, "active");
    public string CacheDir => Path.Combine(DataDir, "cache");
    public string SideloadDir => Path.Combine(DataDir, "sideload");
    public string ArchiveExtension => ".tar.gz";

    public string IdeInstallRoot => Path.Combine(DataDir, "ides");
    public string IdeSideloadDir => Path.Combine(DataDir, "sideload-ides");

    private string ShortcutDir => Path.Combine(DataDir, "shortcuts");
    private string DesktopAppsDir => Path.Combine(_home, ".local", "share", "applications");

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
             # dev-tools-manager
             set -gx DOTNET_ROOT {quoted}
             if not contains -- {quoted} $PATH
                 set -gx PATH {quoted} $PATH
             end
             """ + "\n";

        var fishConfFile = Path.Combine(fishConfDir, "dev-tools-manager.fish");
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

    public ValueTask CreateOrUpdateIdeLinkAsync(
        string productSlug,
        string targetPath,
        CancellationToken ct = default)
    {
        PathSafety.RequireValidFileName(productSlug, nameof(productSlug));

        var fullTarget = Path.GetFullPath(targetPath);
        if (!Directory.Exists(fullTarget))
        {
            throw new DirectoryNotFoundException($"Link target does not exist: '{fullTarget}'.");
        }

        if (!PathSafety.IsInsideRoot(IdeInstallRoot, fullTarget))
        {
            throw new InvalidOperationException(
                $"Refusing to create active IDE link to '{fullTarget}': not under managed IDE root.");
        }

        var productRoot = PathSafety.CombineSafe(IdeInstallRoot, productSlug);
        Directory.CreateDirectory(productRoot);
        var linkPath = Path.Combine(productRoot, "active");

        if (File.Exists(linkPath) || Directory.Exists(linkPath))
        {
            File.Delete(linkPath);
        }

        File.CreateSymbolicLink(linkPath, fullTarget);
        return ValueTask.CompletedTask;
    }

    public async ValueTask CreateOrUpdateShortcutAsync(
        IdeShortcutSpec spec,
        CancellationToken ct = default)
    {
        PathSafety.RequireValidFileName(spec.ProductSlug, nameof(spec.ProductSlug));
        var slug = spec.ProductSlug.ToLowerInvariant();

        Directory.CreateDirectory(ShortcutDir);
        Directory.CreateDirectory(DesktopAppsDir);

        // 1. The wrapper script: exports DOTNET_ROOT and prepends the active SDK to PATH
        //    *before* exec-ing the IDE launcher, so JetBrains GUI processes (which don't
        //    inherit env from .profile / .bashrc on most desktop environments) still see
        //    the .NET SDK we manage.
        var wrapperPath = Path.Combine(ShortcutDir, $"{slug}-launcher.sh");
        var quotedActive = ShellQuote(ActiveLinkPath);
        var quotedExec = ShellQuote(spec.ExecutablePath);
        var wrapperContent = $"""
            #!/bin/sh
            # dev-tools-manager IDE launcher — auto-generated, do not edit.
            active={quotedActive}
            if [ -d "$active" ]; then
                export DOTNET_ROOT="$active"
                case ":$PATH:" in *":$active:"*) ;; *) export PATH="$active:$PATH" ;; esac
            fi
            exec {quotedExec} "$@"
            """ + "\n";

        var wrapperTmp = wrapperPath + ".tmp";
        await File.WriteAllTextAsync(wrapperTmp, wrapperContent, ct);
        File.Move(wrapperTmp, wrapperPath, overwrite: true);
        File.SetUnixFileMode(wrapperPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        // 2. The .desktop entry — references the wrapper, never the launcher directly.
        var desktopPath = Path.Combine(DesktopAppsDir, $"dev-tools-{slug}.desktop");
        var desktopContent = $"""
            [Desktop Entry]
            Version=1.0
            Type=Application
            Name={DesktopEscape(spec.DisplayName)}
            Comment={DesktopEscape(spec.Comment)}
            Exec="{wrapperPath}" %f
            Icon={spec.IconPath}
            Terminal=false
            Categories=Development;IDE;
            StartupWMClass={spec.StartupWmClass}
            StartupNotify=true
            """ + "\n";

        var desktopTmp = desktopPath + ".tmp";
        await File.WriteAllTextAsync(desktopTmp, desktopContent, ct);
        File.Move(desktopTmp, desktopPath, overwrite: true);

        // 3. Best-effort: refresh the desktop database so the menu picks up the new entry
        //    immediately. Non-fatal if update-desktop-database isn't installed.
        try
        {
            await _runner.RunAsync("update-desktop-database", [DesktopAppsDir], 10, ct);
        }
        catch
        {
            // ignore: tool not present, menu refreshes on next session anyway
        }
    }

    public ValueTask RemoveShortcutAsync(
        string productSlug,
        string displayName,
        CancellationToken ct = default)
    {
        PathSafety.RequireValidFileName(productSlug, nameof(productSlug));
        var slug = productSlug.ToLowerInvariant();
        var wrapperPath = Path.Combine(ShortcutDir, $"{slug}-launcher.sh");
        var desktopPath = Path.Combine(DesktopAppsDir, $"dev-tools-{slug}.desktop");

        if (File.Exists(wrapperPath))
        {
            try { File.Delete(wrapperPath); } catch { /* best-effort */ }
        }
        if (File.Exists(desktopPath))
        {
            try { File.Delete(desktopPath); } catch { /* best-effort */ }
        }

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

    /// <summary>
    /// Escape a string for use as a value in a .desktop entry, per the
    /// freedesktop.org Desktop Entry Specification (string type).
    /// </summary>
    private static string DesktopEscape(string value) => value
        .Replace("\\", "\\\\")
        .Replace("\n", "\\n")
        .Replace("\r", "\\r")
        .Replace("\t", "\\t");
}
