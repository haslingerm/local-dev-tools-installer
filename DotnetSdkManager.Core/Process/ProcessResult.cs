namespace DotnetSdkManager.Core.Process;

public record ProcessResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Success => ExitCode == 0;
}
