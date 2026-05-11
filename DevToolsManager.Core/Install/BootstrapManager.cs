using DevToolsManager.Core.Platform;
using DevToolsManager.Core.State;

namespace DevToolsManager.Core.Install;

/// <summary>
/// Owns the one-time "set up PATH + DOTNET_ROOT" handshake that used to be a
/// dedicated bootstrap page. The UI now calls <see cref="EnsureAsync"/>
/// transparently on the first .NET install — see plan §5.3.
/// </summary>
public sealed class BootstrapManager
{
    private readonly IPlatformIntegration _platform;
    private readonly StateManager _stateManager;
    private readonly StubManager _stubManager;

    public BootstrapManager(
        IPlatformIntegration platform,
        StateManager stateManager,
        StubManager stubManager)
    {
        _platform = platform;
        _stateManager = stateManager;
        _stubManager = stubManager;
    }

    /// <summary>
    /// Idempotent: returns immediately if the live environment already has the
    /// PATH entry and the state record agrees. Otherwise writes the env vars,
    /// creates the stub, points the active link at it, and flips the state flag.
    /// </summary>
    public async Task EnsureAsync(CancellationToken ct = default)
    {
        var state = _stateManager.Load();
        if (state.Bootstrapped && _platform.IsBootstrapped())
        {
            return;
        }

        Directory.CreateDirectory(_platform.InstallRoot);

        var stubDir = await _stubManager.EnsureStubAsync(ct);

        await _platform.WriteEnvironmentAsync(_platform.ActiveLinkPath, ct);
        await _platform.CreateOrUpdateLinkAsync(stubDir, ct);

        state.Bootstrapped = true;
        _stateManager.Save(state);
    }
}
