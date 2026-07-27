using System.Text;
using System.Text.RegularExpressions;
using EFCoreSqlPreview.Core.Analysis;
using EFCoreSqlPreview.Core.Execution;
using EFCoreSqlPreview.Core.Generation;

namespace EFCoreSqlPreview.ToolWindows
{
    /// <summary>
    /// Turns the analyzer's and worker's structured output into the sentences the tool window shows.
    /// </summary>
    internal static class PreviewFormatting
    {
        /// <summary>
        /// Matches the C# compiler's canonical diagnostic line so worker errors can be pointed back at the
        /// user's own document.
        /// </summary>
        private static readonly Regex CompilerDiagnostic = new(
            @"^(?<path>[^(]+)\((?<line>\d+),(?<col>\d+)\)\s*:\s*(?<sev>error|warning)\s+(?<id>[A-Za-z]+\d+)\s*:\s*(?<msg>.*)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Builds the one-line result-shape summary, e.g.
        /// <c>await ….ToListAsync() -&gt; List&lt;ProductDto&gt; (DTO projection, async)</c>.
        /// </summary>
        /// <param name="analysis">What the analyzer understood about the selection.</param>
        /// <param name="result">What the worker observed at runtime, when it got that far.</param>
        /// <returns>A display string; never empty.</returns>
        public static string DescribeResult(QueryAnalysisResult? analysis, ResultDescription? result)
        {
            if (analysis is null && result is null)
            {
                return "Result: not determined yet.";
            }

            var terminal = analysis?.TerminalOperator;
            var isAsync = result?.IsAsync ?? terminal?.IsAsync ?? false;
            var builder = new StringBuilder("Result: ");

            if (terminal?.IsAwaited == true)
            {
                builder.Append("await ");
            }

            builder.Append(terminal?.Name is { Length: > 0 } name
                ? "…." + name + "()"
                : "… (deferred query, no terminal operator)");

            var shapeText = DescribeShape(analysis, result);
            if (shapeText.Length > 0)
            {
                builder.Append(" -> ").Append(shapeText);
            }

            var notes = new List<string>();
            var elementNote = DescribeElementKind(analysis, result);
            if (elementNote is { Length: > 0 })
            {
                notes.Add(elementNote);
            }

            notes.Add(isAsync ? "async" : "sync");

            if (terminal?.Source == TerminalSource.Synthesized)
            {
                notes.Add("terminal operator supplied by the preview");
            }

            return builder.Append(" (").Append(string.Join(", ", notes)).Append(')').ToString();
        }

        /// <summary>
        /// Produces the message shown when the selection contains something this tool deliberately does not
        /// preview.
        /// </summary>
        /// <param name="outOfScope">The analyzer's finding.</param>
        /// <returns>A user-facing explanation.</returns>
        public static string DescribeOutOfScope(OutOfScopeInfo outOfScope)
        {
            var what = outOfScope.Reason switch
            {
                OutOfScopeReason.ExecuteUpdate => "ExecuteUpdate",
                OutOfScopeReason.ExecuteDelete => "ExecuteDelete",
                OutOfScopeReason.SaveChanges => "SaveChanges",
                OutOfScopeReason.RawSql => "raw SQL",
                OutOfScopeReason.Mutation => "a mutating call",
                OutOfScopeReason.ThirdPartyBulk => "a third-party bulk operation",
                _ => outOfScope.OffendingMethodName ?? "an unsupported construct",
            };

            var builder = new StringBuilder()
                .Append("EF Core SQL Preview handles SELECT (read) queries only, and this selection uses ")
                .Append(what)
                .Append('.');

            if (outOfScope.Message is { Length: > 0 })
            {
                builder.Append(' ').Append(outOfScope.Message);
            }

            if (!outOfScope.IsHardBlock)
            {
                builder.Append(" The SQL below is still the read part of the query.");
            }
            else
            {
                builder.Append(" Select just the read part of the query and try again.");
            }

            return builder.ToString();
        }

        /// <summary>
        /// Renders the diagnostics tab: analyzer findings first, then compiler errors remapped from the
        /// generated worker back to the user's document.
        /// </summary>
        /// <param name="analysis">The analysis whose diagnostics to list.</param>
        /// <param name="worker">The generated worker, needed for its line map.</param>
        /// <param name="documentText">The user's document, used to turn offsets into line and column numbers.</param>
        /// <param name="documentPath">The user's document path, used in the remapped diagnostic prefix.</param>
        /// <param name="workerOutput">The worker's combined stdout and stderr.</param>
        /// <returns>The diagnostics text, or an empty string when there is nothing to report.</returns>
        public static string DescribeDiagnostics(
            QueryAnalysisResult? analysis,
            GeneratedWorker? worker,
            string documentText,
            string documentPath,
            string workerOutput)
        {
            var builder = new StringBuilder();

            if (analysis is not null && analysis.Diagnostics.Count > 0)
            {
                builder.AppendLine("Analysis");
                builder.AppendLine("--------");
                foreach (var diagnostic in analysis.Diagnostics)
                {
                    var (line, column) = OffsetToLineColumn(documentText, diagnostic.Span.Start);
                    builder
                        .Append(Path.GetFileName(documentPath))
                        .Append('(').Append(line).Append(',').Append(column).Append("): ")
                        .Append(diagnostic.Severity.ToString().ToLowerInvariant()).Append(' ')
                        .Append(diagnostic.Id).Append(": ")
                        .AppendLine(diagnostic.Message);
                }

                builder.AppendLine();
            }

            var compiler = RemapCompilerDiagnostics(worker, documentText, documentPath, workerOutput);
            if (compiler.Count > 0)
            {
                builder.AppendLine("Compiler");
                builder.AppendLine("--------");
                foreach (var line in compiler)
                {
                    builder.AppendLine(line);
                }
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>
        /// Extracts C# compiler diagnostics from the worker's output and rewrites the ones that land inside
        /// the generated program so they point at the user's selection instead.
        /// </summary>
        /// <param name="worker">The generated worker, or <see langword="null"/> when generation never ran.</param>
        /// <param name="documentText">The user's document text.</param>
        /// <param name="documentPath">The user's document path.</param>
        /// <param name="workerOutput">The worker's combined stdout and stderr.</param>
        /// <returns>The remapped diagnostic lines, in the order they appeared.</returns>
        public static IReadOnlyList<string> RemapCompilerDiagnostics(
            GeneratedWorker? worker,
            string documentText,
            string documentPath,
            string workerOutput)
        {
            if (string.IsNullOrEmpty(workerOutput))
            {
                return Array.Empty<string>();
            }

            var results = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var raw in workerOutput.Split('\n'))
            {
                var line = raw.TrimEnd('\r').Trim();
                var match = CompilerDiagnostic.Match(line);
                if (!match.Success)
                {
                    continue;
                }

                var text = line;
                var path = match.Groups["path"].Value.Trim();

                if (worker is not null
                    && string.Equals(path, worker.SourcePath, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(match.Groups["line"].Value, out var generatedLine)
                    && worker.LineMap.TryGetValue(generatedLine, out var offset))
                {
                    var (userLine, userColumn) = OffsetToLineColumn(documentText, offset);
                    text = string.Concat(
                        Path.GetFileName(documentPath),
                        "(", userLine.ToString(), ",", userColumn.ToString(), "): ",
                        match.Groups["sev"].Value, " ", match.Groups["id"].Value, ": ",
                        match.Groups["msg"].Value);
                }

                if (seen.Add(text))
                {
                    results.Add(text);
                }
            }

            return results;
        }

        /// <summary>
        /// Converts a character offset into a 1-based line and column.
        /// </summary>
        /// <param name="text">The text the offset indexes into.</param>
        /// <param name="offset">The character offset; out-of-range values are clamped.</param>
        /// <returns>The 1-based line and column.</returns>
        public static (int Line, int Column) OffsetToLineColumn(string text, int offset)
        {
            if (string.IsNullOrEmpty(text) || offset <= 0)
            {
                return (1, 1);
            }

            var limit = Math.Min(offset, text.Length);
            var line = 1;
            var lineStart = 0;

            for (var i = 0; i < limit; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    lineStart = i + 1;
                }
            }

            return (line, limit - lineStart + 1);
        }

        private static string DescribeShape(QueryAnalysisResult? analysis, ResultDescription? result)
        {
            var shape = result?.Shape ?? analysis?.TerminalOperator.Shape.ToString();

            // The worker infers the shape from the returned value's type, which cannot tell First from Single -
            // both hand back a bare element - so it reports every one-element terminal as SingleElement. The
            // analyzer read the operator's name and does know, so its answer wins for exactly that case.
            var analyzed = analysis?.TerminalOperator.Shape;
            if (shape == nameof(ResultShape.SingleElement)
                && analyzed is ResultShape.FirstElement or ResultShape.LastElement)
            {
                shape = analyzed.Value.ToString();
            }

            if (string.IsNullOrEmpty(shape) || shape == nameof(ResultShape.Unknown))
            {
                return string.Empty;
            }

            var element = result?.ElementType
                ?? analysis?.Projection.TypeName
                ?? (analysis?.Projection.Kind == ProjectionKind.Anonymous ? "anonymous" : null);

            return shape switch
            {
                nameof(ResultShape.Dictionary) =>
                    $"Dictionary<{result?.KeyType ?? "TKey"}, {element ?? "TValue"}>",
                nameof(ResultShape.List) or nameof(ResultShape.HashSet) or nameof(ResultShape.Array)
                    or nameof(ResultShape.Lookup) or nameof(ResultShape.AsyncEnumerable)
                    or nameof(ResultShape.DeferredQueryable) or nameof(ResultShape.DeferredEnumerable) =>
                    element is null ? shape! : $"{shape}<{element}>",
                nameof(ResultShape.Boolean) => "bool",
                _ => element ?? shape!,
            };
        }

        private static string? DescribeElementKind(QueryAnalysisResult? analysis, ResultDescription? result)
        {
            var kind = result?.ElementKind ?? analysis?.Projection.ElementKind.ToString();
            return kind switch
            {
                nameof(ElementKind.Dto) => "DTO projection",
                nameof(ElementKind.Entity) => "entity",
                nameof(ElementKind.Anonymous) => "anonymous projection",
                nameof(ElementKind.Tuple) => "tuple projection",
                nameof(ElementKind.Scalar) => "scalar",
                nameof(ElementKind.Grouping) => "grouping",
                _ => null,
            };
        }
    }
}
