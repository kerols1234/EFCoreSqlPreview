using EFCoreSqlPreview.Core.Infrastructure;

namespace EFCoreSqlPreview.Core.Tests.Fakes;

/// <summary>
/// An <see cref="IProcessRunner"/> that returns canned results, so the runner's pipeline can be tested without
/// a three-second <c>dotnet run</c>.
/// </summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Queue<Func<ProcessRunRequest, ProcessRunResult>> responses = new();

    /// <summary>Every request the runner made, in order.</summary>
    public List<ProcessRunRequest> Requests { get; } = new();

    /// <summary>The result returned once <see cref="responses"/> is exhausted.</summary>
    public ProcessRunResult Default { get; set; } = Ok(string.Empty);

    /// <summary>Thrown instead of returning, when set.</summary>
    public Exception? ThrowOnRun { get; set; }

    /// <summary>Set when the cancellation token was already cancelled at launch time.</summary>
    public bool ObservedCancellation { get; private set; }

    /// <summary>Queues one result for the next run.</summary>
    /// <param name="result">The result to return.</param>
    /// <returns>This instance, for chaining.</returns>
    public FakeProcessRunner Enqueue(ProcessRunResult result)
    {
        this.responses.Enqueue(_ => result);
        return this;
    }

    /// <summary>Queues a result computed from the request.</summary>
    /// <param name="factory">Produces the result.</param>
    /// <returns>This instance, for chaining.</returns>
    public FakeProcessRunner Enqueue(Func<ProcessRunRequest, ProcessRunResult> factory)
    {
        this.responses.Enqueue(factory);
        return this;
    }

    /// <inheritdoc />
    public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
    {
        this.Requests.Add(request);
        this.ObservedCancellation |= cancellationToken.IsCancellationRequested;

        if (this.ThrowOnRun is not null)
        {
            throw this.ThrowOnRun;
        }

        var next = this.responses.Count > 0 ? this.responses.Dequeue()(request) : this.Default;
        return Task.FromResult(next);
    }

    /// <summary>A successful run whose stdout is the given text.</summary>
    /// <param name="standardOutput">Text the worker wrote to stdout.</param>
    /// <returns>A zero-exit result.</returns>
    public static ProcessRunResult Ok(string standardOutput)
        => new(0, standardOutput, string.Empty, TimeSpan.FromSeconds(2), TimedOut: false, Canceled: false);

    /// <summary>A failed run.</summary>
    /// <param name="standardOutput">Text the build wrote to stdout, where diagnostics land.</param>
    /// <param name="standardError">Text the build wrote to stderr.</param>
    /// <param name="exitCode">The exit code.</param>
    /// <returns>A non-zero-exit result.</returns>
    public static ProcessRunResult Failed(string standardOutput, string standardError = "", int exitCode = 1)
        => new(exitCode, standardOutput, standardError, TimeSpan.FromSeconds(1), TimedOut: false, Canceled: false);

    /// <summary>A run that exhausted its time budget.</summary>
    /// <returns>A killed result flagged as timed out.</returns>
    public static ProcessRunResult TimedOut()
        => new(-1, string.Empty, string.Empty, TimeSpan.FromSeconds(120), TimedOut: true, Canceled: false);

    /// <summary>A run the caller cancelled.</summary>
    /// <returns>A killed result flagged as cancelled.</returns>
    public static ProcessRunResult Canceled()
        => new(-1, string.Empty, string.Empty, TimeSpan.FromSeconds(1), TimedOut: false, Canceled: true);
}
