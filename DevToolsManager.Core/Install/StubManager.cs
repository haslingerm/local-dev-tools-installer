using DevToolsManager.Core.Platform;

namespace DevToolsManager.Core.Install;

public sealed class StubManager
{
    private readonly IPlatformIntegration _platform;

    public StubManager(IPlatformIntegration platform) => _platform = platform;

    public string StubDir => Path.Combine(_platform.DataDir, "stub");

    public async ValueTask<string> EnsureStubAsync(CancellationToken ct = default)
    {
        var stubDir = StubDir;
        Directory.CreateDirectory(stubDir);

        if (OperatingSystem.IsWindows())
        {
            var stubCmd = Path.Combine(stubDir, "dotnet.cmd");
            const string cmdContent =
                "@echo off\r\n" +
                "echo No .NET SDK selected. Use DevToolsManager to install one. 1>&2\r\n" +
                "exit /b 1\r\n";
            if (!File.Exists(stubCmd) || await File.ReadAllTextAsync(stubCmd, ct) != cmdContent)
            {
                await File.WriteAllTextAsync(stubCmd, cmdContent, ct);
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            var stubExe = Path.Combine(stubDir, "dotnet");
            const string shContent =
                "#!/bin/sh\n" +
                "echo 'No .NET SDK selected. Use DevToolsManager to install one.' >&2\n" +
                "exit 1\n";
            if (!File.Exists(stubExe) || await File.ReadAllTextAsync(stubExe, ct) != shContent)
            {
                await File.WriteAllTextAsync(stubExe, shContent, ct);
            }

            File.SetUnixFileMode(stubExe,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        return stubDir;
    }
}
