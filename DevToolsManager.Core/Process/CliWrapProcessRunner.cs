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

    public ValueTask LaunchAsync(
        string executable,
        IEnumerable<string> args,
        CancellationToken ct = default)
    {
        // Start the process and do not wait for it to exit — GUI applications run
        // independently of this process.  Unhandled failures (e.g. launch errors)
        // surface on the background task; we observe them here to avoid unobserved-
        // exception noise but otherwise do not block the caller.
        var task = Cli.Wrap(executable)
            .WithArguments(args)
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.Null)
            .WithStandardErrorPipe(PipeTarget.Null)
            .ExecuteAsync(ct)
            .Task;

        _ = task.ContinueWith(
            t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

        return ValueTask.CompletedTask;
    }
}
