using System.Text.Json;
using DevToolsManager.Core.Models;
using DevToolsManager.Core.Platform;

namespace DevToolsManager.Core.State;

public sealed class StateManager
{
    private readonly IPlatformIntegration _platform;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public StateManager(IPlatformIntegration platform) => _platform = platform;

    private string StatePath => Path.Combine(_platform.DataDir, "state.json");

    public AppState Load()
    {
        if (!File.Exists(StatePath)) return new AppState();
        try
        {
            var json = File.ReadAllText(StatePath);
            return JsonSerializer.Deserialize<AppState>(json) ?? new AppState();
        }
        catch { return new AppState(); }
    }

    public void Save(AppState state)
    {
        Directory.CreateDirectory(_platform.DataDir);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state, JsonOpts));
    }
}
