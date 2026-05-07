namespace DotnetSdkManager.Core.Process;

public interface IProcessRunner
{
    public ValueTask<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> args,
        int timeoutSeconds = 30,
        CancellationToken ct = default);
}
