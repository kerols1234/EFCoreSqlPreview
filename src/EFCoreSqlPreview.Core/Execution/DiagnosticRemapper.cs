using System.Text.RegularExpressions;
using EFCoreSqlPreview.Core.Generation;
using Microsoft.CodeAnalysis.Text;

namespace EFCoreSqlPreview.Core.Execution;

/// <summary>
/// One compiler or MSBuild diagnostic from a worker build, resolved back to the user's document when it came
/// from generated code.
/// </summary>
/// <param name="Id">The diagnostic id, e.g. <c>CS0029</c> or <c>NU1107</c>.</param>
/// <param name="Severity">Either <c>error</c> or <c>warning</c>.</param>
/// <param name="Message">The diagnostic text.</param>
/// <param name="FilePath">The file the compiler named, or <see langword="null"/> when it named none.</param>
/// <param name="Line">The 1-based line in <paramref name="FilePath"/>, or 0 when unknown.</param>
/// <param name="Column">The 1-based column in <paramref name="FilePath"/>, or 0 when unknown.</param>
/// <param name="IsInGeneratedFile">Whether <paramref name="FilePath"/> is the generated worker.</param>
/// <param name="UserSpan">
/// Where in the user's document to point. Set only for diagnostics in the generated file that the line map
/// could resolve; a diagnostic in the user's own project already carries a clickable path.
/// </param>
/// <param name="RawText">The diagnostic line exactly as the build printed it.</param>
public sealed record RemappedDiagnostic(
    string Id,
    string Severity,
    string Message,
    string? FilePath,
    int Line,
    int Column,
    bool IsInGeneratedFile,
    TextSpan? UserSpan,
    string RawText)
{
    /// <summary>Whether this diagnostic is an error rather than a warning.</summary>
    public bool IsError => string.Equals(this.Severity, "error", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Parses build output into diagnostics and maps generated-file line numbers back to the user's selection.
/// </summary>
/// <remarks>
/// Two failure shapes matter and are handled differently. When the user's own project does not compile, the
/// compiler already names their file and line, and the path is clickable as-is. When a free variable was given
/// a value that does not bind, the compiler names the generated file, and only the generator's line map can
/// say which part of the selection is responsible.
/// </remarks>
public static class DiagnosticRemapper
{
    /// <summary>
    /// Matches an MSBuild-formatted diagnostic: <c>path(line,col): error CS0029: message [project]</c>.
    /// </summary>
    public static Regex DiagnosticPattern { get; } = new(
        @"^(?<file>[^()\r\n]+?)(?:\((?<line>\d+)(?:,(?<col>\d+))?(?:[-,]\d+)*\))?\s*:\s*(?<sev>error|warning)\s+(?<id>[A-Za-z]+[0-9]+)\s*:\s*(?<msg>.*?)\s*(?:\[[^\[\]]*\])?$",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

    /// <summary>Matches any error line, used to decide whether the build failed at all.</summary>
    public static Regex ErrorPattern { get; } = new(
        @":\s*error\s+[A-Za-z]+[0-9]+\s*:",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parses every diagnostic in a build's output.
    /// </summary>
    /// <param name="output">Combined stdout and stderr from the worker run.</param>
    /// <param name="worker">The generated worker, used to recognise and remap its own file. May be null.</param>
    /// <returns>The diagnostics in the order they were printed, deduplicated.</returns>
    public static IReadOnlyList<RemappedDiagnostic> Parse(string? output, GeneratedWorker? worker)
    {
        if (string.IsNullOrEmpty(output))
        {
            return Array.Empty<RemappedDiagnostic>();
        }

        var normalized = output!.Replace("\r\n", "\n");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<RemappedDiagnostic>();

        foreach (Match match in DiagnosticPattern.Matches(normalized))
        {
            var raw = match.Value.Trim();
            if (!seen.Add(raw))
            {
                continue;
            }

            var file = match.Groups["file"].Success ? match.Groups["file"].Value.Trim() : null;
            if (string.IsNullOrEmpty(file))
            {
                file = null;
            }

            var line = ParseInt(match.Groups["line"]);
            var column = ParseInt(match.Groups["col"]);
            var isGenerated = worker is not null
                && file is not null
                && string.Equals(System.IO.Path.GetFileName(file), System.IO.Path.GetFileName(worker.SourcePath), StringComparison.OrdinalIgnoreCase);

            results.Add(new RemappedDiagnostic(
                Id: match.Groups["id"].Value,
                Severity: match.Groups["sev"].Value.ToLowerInvariant(),
                Message: match.Groups["msg"].Value.Trim(),
                FilePath: file,
                Line: line,
                Column: column,
                IsInGeneratedFile: isGenerated,
                UserSpan: isGenerated ? MapToUserSpan(worker!, line) : null,
                RawText: raw));
        }

        return results;
    }

    /// <summary>Keeps only the errors from a parsed diagnostic list.</summary>
    /// <param name="diagnostics">Parsed diagnostics.</param>
    /// <returns>The errors, in order.</returns>
    public static IReadOnlyList<RemappedDiagnostic> Errors(IEnumerable<RemappedDiagnostic> diagnostics)
        => diagnostics.Where(d => d.IsError).ToList();

    /// <summary>
    /// Maps a 1-based line in the generated program to an offset in the user's document.
    /// </summary>
    /// <param name="worker">The generated worker carrying the line map.</param>
    /// <param name="generatedLine">The 1-based line the compiler named.</param>
    /// <returns>A zero-length span at the responsible offset, or <see langword="null"/> when nothing maps.</returns>
    /// <remarks>
    /// The nearest preceding mapped line is used when the exact line is not in the map: an error on a
    /// continuation line of a multi-line query belongs to the same part of the selection as its first line.
    /// </remarks>
    public static TextSpan? MapToUserSpan(GeneratedWorker worker, int generatedLine)
    {
        if (worker is null || generatedLine <= 0 || worker.LineMap.Count == 0)
        {
            return null;
        }

        if (worker.LineMap.TryGetValue(generatedLine, out var exact))
        {
            return new TextSpan(exact, 0);
        }

        var bestLine = -1;
        var bestOffset = -1;
        foreach (var pair in worker.LineMap)
        {
            if (pair.Key <= generatedLine && pair.Key > bestLine)
            {
                bestLine = pair.Key;
                bestOffset = pair.Value;
            }
        }

        return bestOffset >= 0 ? new TextSpan(bestOffset, 0) : null;
    }

    /// <summary>
    /// Renders diagnostics as the text shown in the tool window, with generated-file paths replaced by a note
    /// that points at the user's selection instead.
    /// </summary>
    /// <param name="diagnostics">Parsed diagnostics.</param>
    /// <param name="maxCount">How many to include before truncating.</param>
    /// <returns>A newline-separated summary.</returns>
    public static string Summarize(IReadOnlyList<RemappedDiagnostic> diagnostics, int maxCount = 25)
    {
        if (diagnostics.Count == 0)
        {
            return string.Empty;
        }

        var lines = new List<string>();
        foreach (var diagnostic in diagnostics.Take(maxCount))
        {
            lines.Add(diagnostic.IsInGeneratedFile
                ? $"{diagnostic.Severity} {diagnostic.Id}: {diagnostic.Message} (in the selected query)"
                : diagnostic.RawText);
        }

        if (diagnostics.Count > maxCount)
        {
            lines.Add($"... and {diagnostics.Count - maxCount} more.");
        }

        return string.Join("\n", lines);
    }

    private static int ParseInt(Group group)
        => group.Success && int.TryParse(group.Value, out var value) ? value : 0;
}
