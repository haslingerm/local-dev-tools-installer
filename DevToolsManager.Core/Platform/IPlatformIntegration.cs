namespace DevToolsManager.Core.Platform;

public interface IPlatformIntegration
{
    public string DataDir { get; }
    public string InstallRoot { get; }
    public string ActiveLinkPath { get; }
    public string CacheDir { get; }
    public string SideloadDir { get; }
    public string ArchiveExtension { get; }
    public string CurrentRid { get; }
    public IEnumerable<string> SystemSdkScanPaths { get; }

    public string IdeInstallRoot { get; }
    public string IdeSideloadDir { get; }

    public ValueTask WriteEnvironmentAsync(string activeLinkPath, CancellationToken ct = default);
    public ValueTask CreateOrUpdateLinkAsync(string targetPath, CancellationToken ct = default);
    public ValueTask<string> RunInShellAsync(string command, int timeoutSeconds = 30, CancellationToken ct = default);
    public bool IsBootstrapped();

    /// <summary>
    /// Atomically points the version-stable active link
    /// <c>IdeInstallRoot/&lt;productSlug&gt;/active</c> at <paramref name="targetPath"/>,
    /// which must be inside <see cref="IdeInstallRoot"/>.
    /// </summary>
    public ValueTask CreateOrUpdateIdeLinkAsync(
        string productSlug,
        string targetPath,
        CancellationToken ct = default);

    /// <summary>
    /// Writes the user-facing launcher for an IDE.
    /// Windows: a .lnk in the user's Start Menu.
    /// Linux: a wrapper shell script (which sets DOTNET_ROOT/PATH so the IDE sees the
    /// active SDK) plus a .desktop file in <c>~/.local/share/applications/</c>.
    /// Idempotent — overwrites any existing launcher for the same product.
    /// </summary>
    public ValueTask CreateOrUpdateShortcutAsync(
        IdeShortcutSpec spec,
        CancellationToken ct = default);

    /// <summary>
    /// Removes the user-facing launcher previously written by
    /// <see cref="CreateOrUpdateShortcutAsync"/>. Best-effort; missing files are ignored.
    /// </summary>
    public ValueTask RemoveShortcutAsync(
        string productSlug,
        string displayName,
        CancellationToken ct = default);

    /// <summary>
    /// Launches the installed IDE identified by <paramref name="productSlug"/>.
    /// On Linux, the generated wrapper script (which exports <c>DOTNET_ROOT</c> / <c>PATH</c>
    /// so the IDE sees the managed SDK) is used when available; the raw
    /// <paramref name="executablePath"/> is used as a fallback.
    /// On Windows, <paramref name="executablePath"/> is launched directly.
    /// </summary>
    public ValueTask LaunchIdeAsync(
        string productSlug,
        string executablePath,
        CancellationToken ct = default);
}
