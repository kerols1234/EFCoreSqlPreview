using Microsoft.CodeAnalysis.Text;

namespace EFCoreSqlPreview.Core.Analysis;

/// <summary>
/// A single observation the analyzer made about the selection.
/// </summary>
/// <param name="Id">A stable identifier from <see cref="AnalysisDiagnosticIds"/>.</param>
/// <param name="Severity">How badly this affects the run.</param>
/// <param name="Message">Human-readable text, ready to show in the tool window.</param>
/// <param name="Span">The span in the user's document the diagnostic points at.</param>
public sealed record AnalysisDiagnostic(
    string Id,
    AnalysisDiagnosticSeverity Severity,
    string Message,
    TextSpan Span)
{
    /// <summary>Creates an <see cref="AnalysisDiagnosticSeverity.Info"/> diagnostic.</summary>
    /// <param name="id">A stable identifier from <see cref="AnalysisDiagnosticIds"/>.</param>
    /// <param name="message">Human-readable text.</param>
    /// <param name="span">The span the diagnostic points at.</param>
    /// <returns>The diagnostic.</returns>
    public static AnalysisDiagnostic Info(string id, string message, TextSpan span)
        => new(id, AnalysisDiagnosticSeverity.Info, message, span);

    /// <summary>Creates an <see cref="AnalysisDiagnosticSeverity.Warning"/> diagnostic.</summary>
    /// <param name="id">A stable identifier from <see cref="AnalysisDiagnosticIds"/>.</param>
    /// <param name="message">Human-readable text.</param>
    /// <param name="span">The span the diagnostic points at.</param>
    /// <returns>The diagnostic.</returns>
    public static AnalysisDiagnostic Warning(string id, string message, TextSpan span)
        => new(id, AnalysisDiagnosticSeverity.Warning, message, span);

    /// <summary>Creates an <see cref="AnalysisDiagnosticSeverity.Error"/> diagnostic.</summary>
    /// <param name="id">A stable identifier from <see cref="AnalysisDiagnosticIds"/>.</param>
    /// <param name="message">Human-readable text.</param>
    /// <param name="span">The span the diagnostic points at.</param>
    /// <returns>The diagnostic.</returns>
    public static AnalysisDiagnostic Error(string id, string message, TextSpan span)
        => new(id, AnalysisDiagnosticSeverity.Error, message, span);
}

/// <summary>
/// Stable identifiers for every diagnostic the analyzer can emit.
/// </summary>
/// <remarks>These appear in the tool window and in tests, so they must never be renumbered.</remarks>
public static class AnalysisDiagnosticIds
{
    /// <summary>No expression could be located at or around the selection.</summary>
    public const string NoQueryFound = "EFSP0001";

    /// <summary>The resolved node is not an expression (a comment, a declaration, ...).</summary>
    public const string SelectionNotAnExpression = "EFSP0002";

    /// <summary>The selection covers a declaration with more than one declarator.</summary>
    public const string MultipleDeclaratorsInSelection = "EFSP0003";

    /// <summary>The selection has no terminal operator; one was synthesized.</summary>
    public const string NoTerminalOperator = "EFSP0010";

    /// <summary>An async terminal was not awaited in the user's source.</summary>
    public const string AsyncTerminalNotAwaited = "EFSP0011";

    /// <summary>A synchronous terminal was awaited, implying an unknown async extension.</summary>
    public const string AwaitOnSyncTerminal = "EFSP0012";

    /// <summary>The terminal name ends in <c>Async</c> but is not a known EF Core operator.</summary>
    public const string UnknownCustomAsyncTerminal = "EFSP0013";

    /// <summary><c>Last</c>/<c>LastAsync</c> without an <c>OrderBy</c>; EF cannot translate that.</summary>
    public const string LastWithoutOrderBy = "EFSP0014";

    /// <summary>The chain leaves server translation (<c>AsEnumerable</c>, <c>ToLookup</c>).</summary>
    public const string ClientEvaluationBoundary = "EFSP0015";

    /// <summary><c>AsSplitQuery</c> is in the chain, so more than one command may be produced.</summary>
    public const string MayProduceMultipleCommands = "EFSP0016";

    /// <summary><c>ProjectTo&lt;T&gt;</c> needs an AutoMapper configuration that cannot be constructed automatically.</summary>
    public const string ProjectToRequiresMapper = "EFSP0020";

    /// <summary>A target-typed <c>new(...)</c> projection whose type name is not syntactically recoverable.</summary>
    public const string TargetTypedNewNotResolvable = "EFSP0021";

    /// <summary>The projection is a statement lambda, which EF Core cannot translate anyway.</summary>
    public const string StatementLambdaProjection = "EFSP0022";

    /// <summary>The DbContext type could not be resolved from this file alone.</summary>
    public const string ContextTypeNotResolvableSyntactically = "EFSP0030";

    /// <summary>The declared context type looks like an interface.</summary>
    public const string ContextMayBeInterface = "EFSP0031";

    /// <summary>The head of the chain could not be classified.</summary>
    public const string ContextRootUnresolved = "EFSP0032";

    /// <summary>The context was reached through <c>?.</c>; the worker drops the null-conditional.</summary>
    public const string ConditionalAccessOnContext = "EFSP0033";

    /// <summary>A free variable has no reproducible value and needs one from the user.</summary>
    public const string FreeVariableNeedsValue = "EFSP0040";

    /// <summary>A free variable's declared type is <c>var</c> or unknown, so no declaration can be emitted.</summary>
    public const string FreeVariableTypeUnknown = "EFSP0041";

    /// <summary>A default value was guessed for something that looks like an enum.</summary>
    public const string EnumDefaultGuess = "EFSP0042";

    /// <summary>An identifier was skipped because it is probably a type name (exclusion E14).</summary>
    public const string SkippedProbableTypeName = "EFSP0043";

    /// <summary>A preceding statement had to be replayed verbatim and may not behave the same.</summary>
    public const string PrologueMayNotBeReproducible = "EFSP0050";

    /// <summary>The selection contains a construct this tool does not preview.</summary>
    public const string OutOfScopeConstruct = "EFSP0060";
}
