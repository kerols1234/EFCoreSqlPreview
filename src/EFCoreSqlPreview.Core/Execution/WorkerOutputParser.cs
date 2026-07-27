using System.Text.Json;
using EFCoreSqlPreview.Core.Generation;

namespace EFCoreSqlPreview.Core.Execution;

/// <summary>
/// Extracts the worker's JSON payload from its stdout and turns it into a <see cref="PreviewResponse"/>.
/// </summary>
/// <remarks>
/// Build diagnostics, both errors and warnings, are written to stdout rather than stderr, so the payload
/// generally arrives after an arbitrary amount of unrelated text. Only the summary line "The build failed."
/// goes to stderr.
/// </remarks>
public static class WorkerOutputParser
{
    /// <summary>
    /// Finds the JSON payload between the sentinels.
    /// </summary>
    /// <param name="standardOutput">The worker's complete stdout.</param>
    /// <param name="payload">The JSON between the sentinels, when present.</param>
    /// <returns><see langword="true"/> when both sentinels were found in order.</returns>
    /// <remarks>
    /// The search starts from the last begin sentinel: a compiler error can quote a source line that contains
    /// the sentinel literal, and the real payload is always written last.
    /// </remarks>
    public static bool TryExtractPayload(string? standardOutput, out string payload)
    {
        payload = string.Empty;
        if (string.IsNullOrEmpty(standardOutput))
        {
            return false;
        }

        var begin = standardOutput!.LastIndexOf(PreviewResponse.PayloadBeginSentinel, StringComparison.Ordinal);
        if (begin < 0)
        {
            return false;
        }

        var contentStart = begin + PreviewResponse.PayloadBeginSentinel.Length;
        var end = standardOutput.IndexOf(PreviewResponse.PayloadEndSentinel, contentStart, StringComparison.Ordinal);
        if (end < 0)
        {
            return false;
        }

        payload = standardOutput.Substring(contentStart, end - contentStart).Trim();
        return payload.Length > 0;
    }

    /// <summary>
    /// Deserializes a worker payload.
    /// </summary>
    /// <param name="payload">The JSON extracted by <see cref="TryExtractPayload"/>.</param>
    /// <returns>The parsed response, or a <see cref="PreviewErrorKind.NoPayload"/> failure when it is malformed.</returns>
    public static PreviewResponse ParsePayload(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return PreviewResponse.Failure(PreviewErrorKind.NoPayload, "The worker produced an empty payload.");
        }

        try
        {
            var response = JsonSerializer.Deserialize<PreviewResponse>(payload, PreviewResponse.JsonOptions);
            return response ?? PreviewResponse.Failure(PreviewErrorKind.NoPayload, "The worker payload deserialized to null.");
        }
        catch (JsonException ex)
        {
            return PreviewResponse.Failure(
                PreviewErrorKind.NoPayload,
                "The worker payload could not be read: " + ex.Message);
        }
    }

    /// <summary>
    /// Turns a completed worker run into a response, classifying the failure when no payload was produced.
    /// </summary>
    /// <param name="standardOutput">The worker's stdout.</param>
    /// <param name="standardError">The worker's stderr.</param>
    /// <param name="exitCode">The worker's exit code.</param>
    /// <param name="timedOut">Whether the run was killed for exceeding its budget.</param>
    /// <param name="canceled">Whether the caller cancelled the run.</param>
    /// <returns>The response to show.</returns>
    public static PreviewResponse Parse(
        string standardOutput,
        string standardError,
        int exitCode,
        bool timedOut = false,
        bool canceled = false)
    {
        if (TryExtractPayload(standardOutput, out var payload))
        {
            return ParsePayload(payload);
        }

        if (timedOut)
        {
            return PreviewResponse.Failure(
                PreviewErrorKind.Timeout,
                "The preview did not finish in time and was stopped. The first run on a machine whose NuGet cache is "
                + "missing the EF Core provider can take a while; try again, or raise the timeout.");
        }

        if (canceled)
        {
            return PreviewResponse.Failure(PreviewErrorKind.Timeout, "The preview was cancelled.");
        }

        var combined = (standardOutput ?? string.Empty) + "\n" + (standardError ?? string.Empty);

        // Restore is checked first: a NuGet error is also shaped like a compiler error, and it is the specific
        // way a dialect with no matching EF Core major version fails.
        if (LooksLikeRestoreFailure(combined))
        {
            return PreviewResponse.Failure(
                PreviewErrorKind.ProviderVersionMismatch,
                "The worker's packages could not be restored, which usually means the chosen dialect has no build "
                + "matching the project's EF Core major version.\n\n" + SummarizeBuildFailure(combined));
        }

        if (LooksLikeCompileFailure(combined))
        {
            return PreviewResponse.Failure(PreviewErrorKind.CompileError, SummarizeBuildFailure(combined));
        }

        return PreviewResponse.Failure(
            PreviewErrorKind.NoPayload,
            $"The worker exited with code {exitCode} without producing a result.\n\n" + Tail(combined, 4000));
    }

    /// <summary>Detects the presence of C# or MSBuild errors in the combined output.</summary>
    /// <param name="output">Combined stdout and stderr.</param>
    /// <returns><see langword="true"/> when the output contains a compiler or MSBuild error.</returns>
    public static bool LooksLikeCompileFailure(string output)
        => DiagnosticRemapper.ErrorPattern.IsMatch(output ?? string.Empty);

    /// <summary>Detects a NuGet restore failure, which is how a wrong-major provider usually shows up.</summary>
    /// <param name="output">Combined stdout and stderr.</param>
    /// <returns><see langword="true"/> when the output contains a NuGet error code.</returns>
    public static bool LooksLikeRestoreFailure(string output)
        => (output ?? string.Empty).IndexOf(": error NU", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// Keeps only the diagnostic lines from a build failure, so the tool window shows the cause rather than
    /// hundreds of lines of MSBuild noise.
    /// </summary>
    /// <param name="output">Combined stdout and stderr.</param>
    /// <returns>The error lines, or the tail of the output when none were recognised.</returns>
    public static string SummarizeBuildFailure(string output)
    {
        var lines = (output ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        var errors = lines.Where(l => DiagnosticRemapper.ErrorPattern.IsMatch(l)).Distinct(StringComparer.Ordinal).Take(40).ToList();
        return errors.Count > 0 ? string.Join("\n", errors) : Tail(output ?? string.Empty, 4000);
    }

    /// <summary>Reads the candidate context types a discovery run reported.</summary>
    /// <param name="response">A discovery worker's response.</param>
    /// <returns>The candidate type names, in the order the worker listed them.</returns>
    public static IReadOnlyList<string> ReadContextCandidates(PreviewResponse response)
    {
        if (response is null)
        {
            return Array.Empty<string>();
        }

        var results = new List<string>();
        foreach (var warning in response.Warnings)
        {
            if (warning.StartsWith(WorkerTemplate.ContextCandidatePrefix, StringComparison.Ordinal))
            {
                results.Add(warning.Substring(WorkerTemplate.ContextCandidatePrefix.Length).Trim());
            }
        }

        return results;
    }

    private static string Tail(string text, int maxLength)
        => text.Length <= maxLength ? text.Trim() : text.Substring(text.Length - maxLength).Trim();
}
