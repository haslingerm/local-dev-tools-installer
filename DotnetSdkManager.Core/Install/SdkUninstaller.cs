using DotnetSdkManager.Core.Discovery;
using DotnetSdkManager.Core.Models;
using DotnetSdkManager.Core.Platform;
using DotnetSdkManager.Core.Process;
using DotnetSdkManager.Core.State;
using DotnetSdkManager.Core.Util;

namespace DotnetSdkManager.Core.Install;

public sealed class SdkUninstaller
{
    private readonly IPlatformIntegration _platform;
    private readonly IProcessRunner _runner;
    private readonly SdkDiscovery _discovery;
    private readonly StateManager _stateManager;
    private readonly StubManager _stubManager;

    public SdkUninstaller(
        IPlatformIntegration platform,
        IProcessRunner runner,
        SdkDiscovery discovery,
        StateManager stateManager,
        StubManager stubManager)
    {
        _platform = platform;
        _runner = runner;
        _discovery = discovery;
        _stateManager = stateManager;
        _stubManager = stubManager;
    }

    public async Task<string?> UninstallAsync(
        SdkInfo sdk,
        IReadOnlyList<SdkInfo> allSdks,
        CancellationToken ct = default)
    {
        if (sdk.Source != SdkSource.Managed)
        {
            throw new InvalidOperationException("Only managed SDKs can be uninstalled.");
        }

        if (!PathSafety.IsInsideRoot(_platform.InstallRoot, sdk.InstallPath))
        {
            throw new InvalidOperationException(
                $"Refusing to uninstall '{sdk.InstallPath}': path is outside the managed install root.");
        }

        var state = _stateManager.Load();
        string? fallbackMessage = null;

        if (state.ActiveVersion == sdk.Version)
        {
            var fallback = PickFallback(sdk.Version, allSdks);
            if (fallback is not null)
            {
                await SwitchDefaultAsync(fallback, ct);
                fallbackMessage = $"Switched default to {fallback.Version}";
            }
            else
            {
                await SwitchToStubAsync(ct);
                fallbackMessage = "No other SDK available. Created stub.";
            }
        }

        try
        {
            var dotnetExe = Path.Combine(sdk.InstallPath, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            await _runner.RunAsync(dotnetExe, ["build-server", "shutdown"], 15, ct);
        }
        catch
        {
            // best-effort: free locked files
        }

        await Task.Delay(500, ct);
        Directory.Delete(sdk.InstallPath, recursive: true);

        return fallbackMessage;
    }

    public async Task SwitchDefaultAsync(SdkInfo sdk, CancellationToken ct = default)
    {
        if (sdk.Source != SdkSource.Managed)
        {
            throw new InvalidOperationException(
                "Only managed SDKs can be set as the default with the current symlink/junction design.");
        }

        await _platform.CreateOrUpdateLinkAsync(sdk.InstallPath, ct);
        var state = _stateManager.Load();
        state.ActiveVersion = sdk.Version;
        _stateManager.Save(state);
    }

    public async Task SwitchToStubAsync(CancellationToken ct = default)
    {
        var stubDir = await _stubManager.EnsureStubAsync(ct);
        await _platform.CreateOrUpdateLinkAsync(stubDir, ct);
        var state = _stateManager.Load();
        state.ActiveVersion = null;
        _stateManager.Save(state);
    }

    private static SdkInfo? PickFallback(string removedVersion, IReadOnlyList<SdkInfo> all) =>
        all.Where(s => s.Version != removedVersion && s.Source == SdkSource.Managed)
           .OrderByDescending(s => ParseVersion(s.Version))
           .FirstOrDefault();

    private static Version ParseVersion(string v)
    {
        Version.TryParse(v.Split('-')[0], out var parsed);
        return parsed ?? new Version(0, 0);
    }
}
