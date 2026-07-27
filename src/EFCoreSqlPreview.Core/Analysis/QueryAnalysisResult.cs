using Microsoft.CodeAnalysis.Text;

namespace EFCoreSqlPreview.Core.Analysis;

/// <summary>
/// Everything the analyzer learned about a selection: the complete input to the worker code generator.
/// </summary>
/// <param name="Status">The overall outcome.</param>
/// <param name="SelectionSpan">The normalised input selection (whitespace trimmed, clamped, un-reversed).</param>
/// <param name="QuerySpan">Span of the resolved query expression.</param>
/// <param name="QueryExpression">Verbatim source of the resolved query expression.</param>
/// <param name="NormalizedQueryText">
/// The same expression with the context root rewritten to the reserved identifier <c>__efspCtx</c>, so the
/// generator never has to redo the substitution.
/// </param>
/// <param name="PrecedingStatements">
/// Statements from the enclosing block that must be replayed verbatim before the query, in source order.
/// Produced by backward liveness over the block (the <c>var q = ...; q = q.Where(...);</c> shape).
/// </param>
/// <param name="Chain">The query's method chain, head-first.</param>
/// <param name="TerminalOperator">The terminal operator, real or synthesized.</param>
/// <param name="Projection">The last <c>Select</c>/<c>SelectMany</c> projection.</param>
/// <param name="ContextRoot">The DbContext the query hangs off.</param>
/// <param name="FreeVariables">Identifiers the query references but does not declare.</param>
/// <param name="Usings">Using directives and namespace context to re-emit in the worker.</param>
/// <param name="OutOfScope">Whether the selection contains an unsupported construct.</param>
/// <param name="Diagnostics">Everything worth telling the user about this analysis.</param>
public sealed record QueryAnalysisResult(
    AnalysisStatus Status,
    TextSpan SelectionSpan,
    TextSpan QuerySpan,
    string QueryExpression,
    string NormalizedQueryText,
    IReadOnlyList<string> PrecedingStatements,
    IReadOnlyList<ChainCallInfo> Chain,
    TerminalOperatorInfo TerminalOperator,
    ProjectionInfo Projection,
    ContextRootInfo ContextRoot,
    IReadOnlyList<FreeVariable> FreeVariables,
    UsingsInfo Usings,
    OutOfScopeInfo OutOfScope,
    IReadOnlyList<AnalysisDiagnostic> Diagnostics)
{
    /// <summary>Whether a query was found and understood well enough to be worth generating a worker for.</summary>
    public bool Success => this.Status is AnalysisStatus.Success or AnalysisStatus.PartiallyResolved;

    /// <summary>Alias for <see cref="Success"/> used by the VSIX when deciding whether to spawn the worker.</summary>
    public bool CanRun => this.Success && !this.OutOfScope.IsHardBlock;

    /// <summary>Whether any diagnostic has <see cref="AnalysisDiagnosticSeverity.Error"/> severity.</summary>
    public bool HasErrors => this.Diagnostics.Any(d => d.Severity == AnalysisDiagnosticSeverity.Error);

    /// <summary>Convenience accessor for <c>OutOfScope.Reason</c>.</summary>
    public OutOfScopeReason OutOfScopeReason => this.OutOfScope.Reason;

    /// <summary>Convenience accessor for <c>Usings.ContainingNamespace</c>.</summary>
    public string? Namespace => this.Usings.ContainingNamespace;

    /// <summary>Whether any free variable still needs a value from the user before the SQL is trustworthy.</summary>
    public bool RequiresUserInput => this.FreeVariables.Any(v => v.RequiresUserValue);

    /// <summary>
    /// Builds a result representing a failed analysis, with every structured member set to its empty value.
    /// </summary>
    /// <param name="status">The failing status.</param>
    /// <param name="selection">The normalised selection span.</param>
    /// <param name="diagnostic">The diagnostic explaining the failure.</param>
    /// <returns>A populated failure result.</returns>
    public static QueryAnalysisResult Failure(AnalysisStatus status, TextSpan selection, AnalysisDiagnostic diagnostic)
        => new(
            Status: status,
            SelectionSpan: selection,
            QuerySpan: selection,
            QueryExpression: string.Empty,
            NormalizedQueryText: string.Empty,
            PrecedingStatements: Array.Empty<string>(),
            Chain: Array.Empty<ChainCallInfo>(),
            TerminalOperator: TerminalOperatorInfo.None,
            Projection: ProjectionInfo.None,
            ContextRoot: ContextRootInfo.Unresolved,
            FreeVariables: Array.Empty<FreeVariable>(),
            Usings: UsingsInfo.Empty,
            OutOfScope: OutOfScopeInfo.None,
            Diagnostics: new[] { diagnostic });
}
