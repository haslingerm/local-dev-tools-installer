namespace DevToolsManager.Core.Models;

public enum SdkSource { SystemInstalled, Managed }

public record SdkInfo(
    string Version,
    string InstallPath,
    SdkSource Source,
    bool IsActive = false
);
