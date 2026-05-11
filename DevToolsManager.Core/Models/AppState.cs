namespace DevToolsManager.Core.Models;

public class AppState
{
    public int SchemaVersion { get; set; } = 2;
    public bool Bootstrapped { get; set; }
    public string? ActiveVersion { get; set; }
    public Dictionary<string, string> ActiveIdes { get; set; } = new();
}
