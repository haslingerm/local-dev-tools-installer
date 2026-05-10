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

    public ValueTask WriteEnvironmentAsync(string activeLinkPath, CancellationToken ct = default);
    public ValueTask CreateOrUpdateLinkAsync(string targetPath, CancellationToken ct = default);
    public ValueTask<string> RunInShellAsync(string command, int timeoutSeconds = 30, CancellationToken ct = default);
    public bool IsBootstrapped();
}
