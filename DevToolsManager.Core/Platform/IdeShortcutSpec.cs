namespace DevToolsManager.Core.Platform;

/// <summary>
/// Describes the user-facing launcher entry written for an installed IDE.
/// All paths must be fully resolved (the platform layer does not interpret
/// relative paths or "active" indirections).
/// </summary>
public sealed record IdeShortcutSpec(
    string ProductSlug,
    string DisplayName,
    string ExecutablePath,
    string IconPath,
    string StartupWmClass,
    string Comment);
