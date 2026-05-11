using DevToolsManager.Core.Platform;
using DevToolsManager.Core.Process;

namespace DevToolsManager.Core.Install;

public sealed class SdkSmokeTest
{
    private readonly IProcessRunner _runner;

    public SdkSmokeTest(IProcessRunner runner) => _runner = runner;

    public async ValueTask<(bool ok, string output)> TestInstallAsync(
        string installDir,
        string expectedVersion,
        CancellationToken ct = default)
    {
        var dotnetExe = Path.Combine(installDir, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (!File.Exists(dotnetExe))
        {
            return (false, $"dotnet executable not found at {dotnetExe}");
        }

        var result = await _runner.RunAsync(dotnetExe, ["--info"], 30, ct);
        if (!result.Success)
        {
            return (false, $"dotnet --info failed (exit {result.ExitCode}):\n{result.Stdout}\n{result.Stderr}");
        }

        var output = result.Stdout;
        if (!output.Contains(expectedVersion, StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"Version mismatch. Expected {expectedVersion} in output:\n{output}");
        }

        if (!output.Contains(installDir, StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"Base Path not inside expected dir {installDir}:\n{output}");
        }

        return (true, output);
    }

    public async ValueTask<(bool ok, string output)> TestDefaultSwitchAsync(
        IPlatformIntegration platform,
        string expectedVersion,
        CancellationToken ct = default)
    {
        var output = await platform.RunInShellAsync("dotnet --version", 30, ct);
        var ok = output.Trim().StartsWith(expectedVersion, StringComparison.OrdinalIgnoreCase);
        return (ok, output);
    }
}
