using System.Diagnostics;
using System.Text;

namespace EFCoreSqlPreview.Core.Infrastructure;

/// <summary>
/// One child process to launch.
/// </summary>
/// <param name="FileName">The executable, e.g. <c>dotnet</c>.</param>
/// <param name="Arguments">The command line, already quoted.</param>
/// <param name="WorkingDirectory">The directory to start in.</param>
public sealed record ProcessRunRequest(
    string FileName,
    string Arguments,
    string WorkingDirectory)
{
    /// <summary>Environment variables to set or, when the value is <see langword="null"/>, to remove.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; }
        = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long to wait before killing the process tree.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);
}

/// <summary>
/// What a child process produced.
/// </summary>
/// <param name="ExitCode">The exit code, or -1 when the process was killed.</param>
/// <param name="StandardOutput">Everything written to stdout.</param>
/// <param name="StandardError">Everything written to stderr.</param>
/// <param name="Elapsed">Wall-clock duration.</param>
/// <param name="TimedOut">Whether the time budget was exhausted.</param>
/// <param name="Canceled">Whether the caller's cancellation token fired.</param>
public sealed record ProcessRunResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Elapsed,
    bool TimedOut,
    bool Canceled);

/// <summary>
/// Launches child processes.
/// </summary>
public interface IProcessRunner
{
    /// <summary>Runs a process to completion, or kills its tree on timeout or cancellation.</summary>
    /// <param name="request">What to launch.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The captured output; never throws for a non-zero exit code.</returns>
    Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The real <see cref="IProcessRunner"/>.
/// </summary>
/// <remarks>
/// stdout and stderr are drained on the event callbacks rather than read synchronously: a build that emits
/// enough warnings to fill a pipe buffer would otherwise deadlock the run before the payload is ever written.
/// </remarks>
public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>The shared instance.</summary>
    public static ProcessRunner Default { get; } = new();

    /// <inheritdoc />
    public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var startInfo = new ProcessStartInfo(request.FileName, request.Arguments)
        {
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var pair in request.Environment)
        {
            if (pair.Value is null)
            {
                startInfo.EnvironmentVariables.Remove(pair.Key);
            }
            else
            {
                startInfo.EnvironmentVariables[pair.Key] = pair.Value;
            }
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource<int>();
        var stdoutDone = new TaskCompletionSource<bool>();
        var stderrDone = new TaskCompletionSource<bool>();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stdoutDone.TrySetResult(true);
            }
            else
            {
                lock (stdout)
                {
                    stdout.Append(e.Data).Append('\n');
                }
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stderrDone.TrySetResult(true);
            }
            else
            {
                lock (stderr)
                {
                    stderr.Append(e.Data).Append('\n');
                }
            }
        };

        process.Exited += (_, _) =>
        {
            try
            {
                exited.TrySetResult(process.ExitCode);
            }
            catch (InvalidOperationException)
            {
                exited.TrySetResult(-1);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // The worker never reads stdin, but leaving the handle open lets a stray prompt block forever.
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The process may already have gone; nothing to close.
        }

        var timedOut = false;
        var canceled = false;

        using (var timeoutSource = new CancellationTokenSource())
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token))
        {
            timeoutSource.CancelAfter(request.Timeout);

            var abort = new TaskCompletionSource<bool>();
            using (linked.Token.Register(() => abort.TrySetResult(true)))
            {
                var completed = await Task.WhenAny(exited.Task, abort.Task).ConfigureAwait(false);
                if (completed != exited.Task)
                {
                    timedOut = timeoutSource.IsCancellationRequested;
                    canceled = cancellationToken.IsCancellationRequested;
                    KillTree(process);

                    // Give the pipes a moment to flush whatever was written before the kill.
                    await Task.WhenAny(exited.Task, Task.Delay(2000)).ConfigureAwait(false);
                }
            }
        }

        // Both pipes signal null once flushed; bound the wait so a wedged child cannot hang the extension.
        await Task.WhenAny(
            Task.WhenAll(stdoutDone.Task, stderrDone.Task),
            Task.Delay(2000)).ConfigureAwait(false);

        stopwatch.Stop();

        var exitCode = exited.Task.Status == TaskStatus.RanToCompletion ? exited.Task.Result : -1;

        string outText;
        string errText;
        lock (stdout)
        {
            outText = stdout.ToString();
        }

        lock (stderr)
        {
            errText = stderr.ToString();
        }

        return new ProcessRunResult(exitCode, outText, errText, stopwatch.Elapsed, timedOut, canceled);
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }
        }
        catch (InvalidOperationException)
        {
            return;
        }

#if NET8_0_OR_GREATER
        try
        {
            process.Kill(entireProcessTree: true);
            return;
        }
        catch (Exception)
        {
            // Fall through to taskkill.
        }
#endif

        // netstandard2.0 has no tree-killing overload, and `dotnet run` spawns MSBuild worker nodes plus the
        // app itself; killing only the launcher would leave them holding the build lock.
        try
        {
            using var killer = Process.Start(new ProcessStartInfo("taskkill", "/T /F /PID " + process.Id)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            killer?.WaitForExit(5000);
        }
        catch (Exception)
        {
            try
            {
                process.Kill();
            }
            catch (Exception)
            {
                // Nothing further can be done; the timeout result already tells the caller what happened.
            }
        }
    }
}
