using CliWrap;
using CliWrap.Buffered;

namespace DevToolsManager.Core.Process;

public sealed class CliWrapProcessRunner : IProcessRunner
{
    public async ValueTask<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> args,
        int timeoutSeconds = 30,
        CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var result = await Cli.Wrap(executable)
                .WithArguments(args)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cts.Token);

            return new ProcessResult(result.ExitCode, result.StandardOutput.Trim(), result.StandardError.Trim());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProcessResult(-1, "", $"Process timed out after {timeoutSeconds}s");
        }
    }
}
