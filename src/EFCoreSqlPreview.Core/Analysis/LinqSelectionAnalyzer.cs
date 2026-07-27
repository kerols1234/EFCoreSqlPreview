using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace EFCoreSqlPreview.Core.Analysis;

/// <summary>
/// The default <see cref="ILinqSelectionAnalyzer"/>: syntax-only Roslyn analysis of a selected LINQ query.
/// </summary>
public sealed class LinqSelectionAnalyzer : ILinqSelectionAnalyzer
{
    /// <summary>
    /// The identifier <see cref="QueryAnalysisResult.NormalizedQueryText"/> and
    /// <see cref="QueryAnalysisResult.PrecedingStatements"/> substitute for the user's DbContext expression.
    /// </summary>
    /// <remarks>Bound to the generator's own constant so the two can never drift apart.</remarks>
    public const string ContextPlaceholder = Generation.WorkerIdentifiers.Context;

    /// <summary>
    /// The parse options the analyzer uses. Exposed so callers can cache trees that are compatible with it.
    /// </summary>
    /// <remarks>
    /// <see cref="LanguageVersion.Preview"/> is deliberate: the analyzer must never fail to parse because the
    /// user is on a newer C#. Documentation parsing is off because XML docs are pure overhead here.
    /// </remarks>
    public static CSharpParseOptions ParseOptions { get; } = new(
        languageVersion: LanguageVersion.Preview,
        documentationMode: DocumentationMode.None,
        kind: SourceCodeKind.Regular);

    /// <summary>A shared, stateless instance.</summary>
    public static LinqSelectionAnalyzer Instance { get; } = new();

    /// <summary>Analyses a selection with <see cref="AnalyzerOptions.Default"/>.</summary>
    /// <param name="documentText">The full document text.</param>
    /// <param name="selection">The selection span.</param>
    /// <returns>The analysis result.</returns>
    public static QueryAnalysisResult Analyze(string documentText, TextSpan selection)
        => Instance.Analyze(documentText, selection, AnalyzerOptions.Default);

    /// <inheritdoc />
    public QueryAnalysisResult Analyze(string documentText, TextSpan selection, AnalyzerOptions options)
    {
        documentText ??= string.Empty;

        SyntaxTree tree;
        try
        {
            tree = CSharpSyntaxTree.ParseText(documentText, ParseOptions);
        }
        catch (Exception ex)
        {
            return QueryAnalysisResult.Failure(
                AnalysisStatus.ParseFailure,
                new TextSpan(0, 0),
                AnalysisDiagnostic.Error(
                    AnalysisDiagnosticIds.NoQueryFound,
                    "The document could not be parsed: " + ex.Message,
                    new TextSpan(0, 0)));
        }

        return this.Analyze(tree, selection, options);
    }

    /// <inheritdoc />
    public QueryAnalysisResult Analyze(SyntaxTree tree, TextSpan selection, AnalyzerOptions options)
    {
        options ??= AnalyzerOptions.Default;

        if (tree is null)
        {
            return QueryAnalysisResult.Failure(
                AnalysisStatus.ParseFailure,
                new TextSpan(0, 0),
                AnalysisDiagnostic.Error(
                    AnalysisDiagnosticIds.NoQueryFound,
                    "No document was supplied.",
                    new TextSpan(0, 0)));
        }

        try
        {
            return AnalyzeCore(tree, selection, options);
        }
        catch (Exception ex)
        {
            // The user will select nonsense: a crash in the editor is never an acceptable answer.
            return QueryAnalysisResult.Failure(
                AnalysisStatus.NoQueryFound,
                selection,
                AnalysisDiagnostic.Error(
                    AnalysisDiagnosticIds.NoQueryFound,
                    "The selection could not be analysed: " + ex.Message,
                    selection));
        }
    }

    private static QueryAnalysisResult AnalyzeCore(SyntaxTree tree, TextSpan selection, AnalyzerOptions options)
    {
        var documentText = tree.ToString();
        var normalized = SelectionNormalizer.Normalize(documentText, selection);
        var root = tree.GetRoot();

        var resolution = SelectionResolver.Resolve(root, normalized);
        if (resolution.Expression is null)
        {
            return QueryAnalysisResult.Failure(
                AnalysisStatus.NoQueryFound,
                normalized,
                AnalysisDiagnostic.Error(
                    AnalysisDiagnosticIds.NoQueryFound,
                    "No LINQ query was found at the selection.",
                    normalized));
        }

        var query = resolution.Expression;
        var chain = QueryChainWalker.Walk(query);

        if (!QueryChainWalker.IsQueryShaped(chain))
        {
            return QueryAnalysisResult.Failure(
                AnalysisStatus.NotAQuery,
                normalized,
                AnalysisDiagnostic.Error(
                    AnalysisDiagnosticIds.SelectionNotAnExpression,
                    "The selection is not a LINQ query: select a query that starts from a DbSet or a DbContext.",
                    query.Span));
        }

        var diagnostics = new List<AnalysisDiagnostic>();

        if (resolution.Anchor is LocalDeclarationStatementSyntax declaration
            && declaration.Declaration.Variables.Count > 1)
        {
            diagnostics.Add(AnalysisDiagnostic.Info(
                AnalysisDiagnosticIds.MultipleDeclaratorsInSelection,
                "The selection covers several declarations; the last one that intersects it was previewed.",
                declaration.Span));
        }

        var contextRoot = ContextRootResolver.Resolve(chain, root, options, diagnostics);

        var prologue = SlicePrologue(resolution, chain, contextRoot, diagnostics);

        var freeVariables = FreeVariableCollector.Collect(
            chain, prologue, root, options, diagnostics, contextRoot.IdentifierName);

        var terminal = TerminalOperatorClassifier.Classify(chain, options, diagnostics);
        var projection = ProjectionClassifier.Classify(chain, diagnostics);
        var usings = UsingCollector.Collect(root, query);
        var outOfScope = DetectOutOfScope(query, prologue, contextRoot, options, diagnostics);

        var status = DetermineStatus(query, resolution.Anchor, contextRoot, freeVariables, outOfScope);

        return new QueryAnalysisResult(
            Status: status,
            SelectionSpan: normalized,
            QuerySpan: query.Span,
            QueryExpression: query.ToString(),
            NormalizedQueryText: ContextRootResolver.NormalizeQueryText(chain, contextRoot, ContextPlaceholder),
            PrecedingStatements: prologue
                .Select(s => ContextRootResolver.NormalizeStatementText(s, contextRoot, ContextPlaceholder))
                .ToArray(),
            Chain: QueryChainWalker.Describe(chain),
            TerminalOperator: terminal,
            Projection: projection,
            ContextRoot: contextRoot,
            FreeVariables: freeVariables,
            Usings: usings,
            OutOfScope: outOfScope,
            Diagnostics: diagnostics);
    }

    private static IReadOnlyList<StatementSyntax> SlicePrologue(
        SelectionResolution resolution,
        QueryChain chain,
        ContextRootInfo contextRoot,
        ICollection<AnalysisDiagnostic> diagnostics)
    {
        var anchor = resolution.Anchor;
        var block = resolution.Block ?? anchor?.Parent as BlockSyntax;
        if (anchor is null || block is null)
        {
            return Array.Empty<StatementSyntax>();
        }

        HashSet<string> seed;

        if (resolution.Block is not null)
        {
            // Path A: the selection literally spans several statements, so everything the anchor reads is live.
            seed = new HashSet<string>(PrologueSlicer.Reads(anchor), StringComparer.Ordinal);
        }
        else if (contextRoot.Kind == ContextRootKind.LocalQuery && chain.Head is IdentifierNameSyntax head)
        {
            // Path B: the query hangs off a local that earlier statements built up.
            seed = new HashSet<string>(StringComparer.Ordinal) { head.Identifier.ValueText };
        }
        else
        {
            // A single-statement selection off the context itself needs no replay: free variables cover it.
            return Array.Empty<StatementSyntax>();
        }

        var sliced = PrologueSlicer.Slice(block, anchor, seed);

        foreach (var statement in sliced)
        {
            if (statement.DescendantNodesAndSelf().OfType<AwaitExpressionSyntax>().Any())
            {
                diagnostics.Add(AnalysisDiagnostic.Warning(
                    AnalysisDiagnosticIds.PrologueMayNotBeReproducible,
                    "A statement replayed before the query awaits something; the preview may not reproduce its effect.",
                    statement.Span));
            }
        }

        return sliced;
    }

    private static OutOfScopeInfo DetectOutOfScope(
        ExpressionSyntax query,
        IReadOnlyList<StatementSyntax> prologue,
        ContextRootInfo contextRoot,
        AnalyzerOptions options,
        ICollection<AnalysisDiagnostic> diagnostics)
    {
        var found = OutOfScopeDetector.Detect(query, contextRoot, options);

        if (!found.IsHardBlock)
        {
            foreach (var statement in prologue)
            {
                var inPrologue = OutOfScopeDetector.Detect(statement, contextRoot, options);
                if (inPrologue.IsHardBlock)
                {
                    found = inPrologue;
                    break;
                }

                if (!found.IsOutOfScope && inPrologue.IsOutOfScope)
                {
                    found = inPrologue;
                }
            }
        }

        if (found.IsOutOfScope)
        {
            diagnostics.Add(found.IsHardBlock
                ? AnalysisDiagnostic.Error(AnalysisDiagnosticIds.OutOfScopeConstruct, found.Message, found.Span)
                : AnalysisDiagnostic.Warning(AnalysisDiagnosticIds.OutOfScopeConstruct, found.Message, found.Span));
        }

        return found;
    }

    private static AnalysisStatus DetermineStatus(
        ExpressionSyntax query,
        StatementSyntax? anchor,
        ContextRootInfo contextRoot,
        IReadOnlyList<FreeVariable> freeVariables,
        OutOfScopeInfo outOfScope)
    {
        if (outOfScope.IsHardBlock)
        {
            return AnalysisStatus.OutOfScope;
        }

        if (outOfScope.IsOutOfScope
            || contextRoot.Confidence == ContextTypeConfidence.Unknown
            || contextRoot.Kind == ContextRootKind.Unresolved
            || freeVariables.Any(v => v.RequiresUserValue))
        {
            return AnalysisStatus.PartiallyResolved;
        }

        // Roslyn's error recovery still yields a usable chain from broken source, but the result is a guess.
        if (query.ContainsDiagnostics || (anchor?.ContainsDiagnostics ?? false))
        {
            return AnalysisStatus.PartiallyResolved;
        }

        return AnalysisStatus.Success;
    }
}
