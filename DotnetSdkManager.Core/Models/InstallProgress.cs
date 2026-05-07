namespace DotnetSdkManager.Core.Models;

public enum InstallPhase { Downloading, Verifying, Extracting, SmokeTesting, Done, Failed }

public record InstallProgress(long Bytes, long? Total, InstallPhase Phase, string Message = "")
{
    public double? Percent => Total.HasValue && Total > 0 ? (double)Bytes / Total * 100 : null;
}
