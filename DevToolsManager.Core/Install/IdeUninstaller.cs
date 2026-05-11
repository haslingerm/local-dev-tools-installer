using DevToolsManager.Core.Discovery;
using DevToolsManager.Core.Models;
using DevToolsManager.Core.Platform;
using DevToolsManager.Core.State;
using DevToolsManager.Core.Util;

namespace DevToolsManager.Core.Install;

/// <summary>
/// Removes a managed IDE version. Mirrors <see cref="SdkUninstaller"/> minus the
/// env/stub logic — IDEs are reached via shortcuts, not PATH. On removing the
/// currently active version: switches active to the next-most-recent installed
/// version; if none remain, removes the active link and the user-facing shortcut.
/// </summary>
public sealed class IdeUninstaller
{
    private readonly IPlatformIntegration _platform;
    private readonly IdeDiscovery _discovery;
    private readonly StateManager _stateManager;

    public IdeUninstaller(
        IPlatformIntegration platform,
        IdeDiscovery discovery,
        StateManager stateManager)
    {
        _platform = platform;
        _discovery = discovery;
        _stateManager = stateManager;
    }

    /// <returns>
    /// A human-readable message describing any fallback switch performed, or
    /// <c>null</c> if the removed version was not active.
    /// </returns>
    public async Task<string?> UninstallAsync(IdeInfo ide, CancellationToken ct = default)
    {
        if (!PathSafety.IsInsideRoot(_platform.IdeInstallRoot, ide.InstallPath))
        {
            throw new InvalidOperationException(
                $"Refusing to uninstall '{ide.InstallPath}': path is outside the managed IDE install root.");
        }

        var slug = JetBrainsProductInfo.Slug(ide.Product);
        var code = JetBrainsProductInfo.Code(ide.Product);
        var state = _stateManager.Load();

        string? fallbackMessage = null;
        var wasActive = state.ActiveIdes.TryGetValue(code, out var activeVer)
                        && string.Equals(activeVer, ide.Version, StringComparison.OrdinalIgnoreCase);

        if (wasActive)
        {
            var remaining = _discovery.Scan(ide.Product, ide.Version)
                .Where(i => !string.Equals(i.Version, ide.Version, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var fallback = remaining
                .OrderByDescending(i => ParseVersion(i.Version))
                .FirstOrDefault();

            if (fallback is not null)
            {
                await _platform.CreateOrUpdateIdeLinkAsync(slug, fallback.InstallPath, ct);
                state.ActiveIdes[code] = fallback.Version;
                fallbackMessage = $"Switched active to {fallback.Version}.";
            }
            else
            {
                await _platform.RemoveShortcutAsync(
                    slug, JetBrainsProductInfo.DisplayName(ide.Product), ct);
                RemoveActiveLink(slug);
                state.ActiveIdes.Remove(code);
                fallbackMessage = "No other versions left. Removed shortcut.";
            }

            _stateManager.Save(state);
        }

        Directory.Delete(ide.InstallPath, recursive: true);
        return fallbackMessage;
    }

    private void RemoveActiveLink(string slug)
    {
        var activeLink = Path.Combine(_platform.IdeInstallRoot, slug, "active");
        if (Directory.Exists(activeLink))
        {
            Directory.Delete(activeLink);
        }
        else if (File.Exists(activeLink))
        {
            File.Delete(activeLink);
        }
    }

    private static Version ParseVersion(string v)
    {
        Version.TryParse(v, out var parsed);
        return parsed ?? new Version(0, 0);
    }
}
