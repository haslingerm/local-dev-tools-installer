namespace DevToolsManager.Core.Models;

public class AppState
{
    public int SchemaVersion { get; set; } = 1;
    public bool Bootstrapped { get; set; }
    public string? ActiveVersion { get; set; }
}
