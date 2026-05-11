namespace DevToolsManager.Core.Process;

public interface IProcessRunner
{
    public ValueTask<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> args,
        int timeoutSeconds = 30,
        CancellationToken ct = default);

    /// <summary>
    /// Starts <paramref name="executable"/> without waiting for it to exit.
    /// Suitable for launching GUI applications that should remain running after this call returns.
    /// </summary>
    public ValueTask LaunchAsync(
        string executable,
        IEnumerable<string> args,
        CancellationToken ct = default);
}
