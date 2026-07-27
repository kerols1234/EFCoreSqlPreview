using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFCoreSqlPreview.Core.Analysis;

/// <summary>
/// Classifies the last call in a chain against <see cref="TerminalOperatorCatalog"/>, and synthesizes a
/// terminal when the selection has none.
/// </summary>
public static class TerminalOperatorClassifier
{
    private const string EntityFrameworkNamespace = "Microsoft.EntityFrameworkCore";

    /// <summary>Classifies the terminal operator of a chain.</summary>
    /// <param name="chain">The decomposed chain.</param>
    /// <param name="options">Analysis options, which supply the terminal to synthesize.</param>
    /// <param name="diagnostics">Collector for EFSP0010-EFSP0016 diagnostics.</param>
    /// <returns>The classified terminal; never <see langword="null"/>.</returns>
    public static TerminalOperatorInfo Classify(
        QueryChain chain,
        AnalyzerOptions options,
        ICollection<AnalysisDiagnostic> diagnostics)
    {
        if (chain is null)
        {
            return TerminalOperatorInfo.None;
        }

        options ??= AnalyzerOptions.Default;
        diagnostics ??= new List<AnalysisDiagnostic>();

        ReportChainPositionDiagnostics(chain, diagnostics);

        if (chain.Calls.Count == 0)
        {
            return Synthesize(chain, options, diagnostics);
        }

        var terminal = chain.Calls[chain.Calls.Count - 1];
        var name = QueryChainWalker.CallName(terminal);
        var descriptor = TerminalOperatorCatalog.Lookup(name);

        if (descriptor is null)
        {
            return ClassifyUnknown(chain, terminal, name, options, diagnostics);
        }

        var arguments = DescribeArguments(terminal);
        var isAsync = descriptor.IsAsync;
        var source = TerminalSource.Catalog;

        if (isAsync && !chain.IsAwaited)
        {
            diagnostics.Add(AnalysisDiagnostic.Warning(
                AnalysisDiagnosticIds.AsyncTerminalNotAwaited,
                $"'{name}' is asynchronous but the selection does not await it; the preview awaits it for you.",
                terminal.Span));
        }
        else if (!isAsync && chain.IsAwaited)
        {
            // Trust the source: awaiting a name the catalog calls synchronous means it is really a shadowing
            // extension method that returns a task.
            isAsync = true;
            source = TerminalSource.InferredFromAwait;
            diagnostics.Add(AnalysisDiagnostic.Warning(
                AnalysisDiagnosticIds.AwaitOnSyncTerminal,
                $"'{name}' is awaited but is not a known asynchronous operator; it is treated as asynchronous.",
                terminal.Span));
        }

        if (descriptor.Shape == ResultShape.LastElement && !HasOrderBy(chain))
        {
            diagnostics.Add(AnalysisDiagnostic.Warning(
                AnalysisDiagnosticIds.LastWithoutOrderBy,
                $"EF Core cannot translate '{name}' without an OrderBy earlier in the chain.",
                terminal.Span));
        }

        // AsEnumerable/ToLookup stop server translation, so the worker must still force enumeration of the
        // server-side prefix to make EF emit anything at all.
        string? synthesizedTerminalText = null;
        if (descriptor.Shape is ResultShape.DeferredEnumerable or ResultShape.Lookup or ResultShape.AsyncEnumerable)
        {
            synthesizedTerminalText = descriptor.Shape == ResultShape.AsyncEnumerable ? ".ToListAsync()" : ".ToList()";
        }

        return new TerminalOperatorInfo(
            Name: name,
            Source: source,
            IsAsync: isAsync,
            IsAwaited: chain.IsAwaited,
            Shape: descriptor.Shape,
            HasPredicateArgument: arguments.HasLambda,
            ArgumentCount: arguments.Texts.Count,
            TypeArguments: QueryChainWalker.TypeArguments(terminal),
            ArgumentTexts: arguments.Texts,
            SynthesizedTerminalText: synthesizedTerminalText,
            Span: terminal.Span,
            Descriptor: descriptor);
    }

    private static TerminalOperatorInfo ClassifyUnknown(
        QueryChain chain,
        InvocationExpressionSyntax terminal,
        string? name,
        AnalyzerOptions options,
        ICollection<AnalysisDiagnostic> diagnostics)
    {
        var arguments = DescribeArguments(terminal);

        // A name ending in Async that the catalog does not know is a user-defined extension: run it as written
        // and let the worker report whatever type comes back.
        if (name is not null && name.EndsWith("Async", StringComparison.Ordinal))
        {
            diagnostics.Add(AnalysisDiagnostic.Warning(
                AnalysisDiagnosticIds.UnknownCustomAsyncTerminal,
                $"'{name}' is not a known EF Core operator; the preview runs it as written and reports the runtime type.",
                terminal.Span));

            return new TerminalOperatorInfo(
                Name: name,
                Source: TerminalSource.CustomAsyncExtension,
                IsAsync: true,
                IsAwaited: chain.IsAwaited,
                Shape: ResultShape.Unknown,
                HasPredicateArgument: arguments.HasLambda,
                ArgumentCount: arguments.Texts.Count,
                TypeArguments: QueryChainWalker.TypeArguments(terminal),
                ArgumentTexts: arguments.Texts,
                SynthesizedTerminalText: null,
                Span: terminal.Span,
                Descriptor: null);
        }

        if (chain.IsAwaited)
        {
            diagnostics.Add(AnalysisDiagnostic.Warning(
                AnalysisDiagnosticIds.AwaitOnSyncTerminal,
                $"'{name}' is awaited but is not a known operator; it is treated as an asynchronous terminal.",
                terminal.Span));

            return new TerminalOperatorInfo(
                Name: name,
                Source: TerminalSource.InferredFromAwait,
                IsAsync: true,
                IsAwaited: true,
                Shape: ResultShape.Unknown,
                HasPredicateArgument: arguments.HasLambda,
                ArgumentCount: arguments.Texts.Count,
                TypeArguments: QueryChainWalker.TypeArguments(terminal),
                ArgumentTexts: arguments.Texts,
                SynthesizedTerminalText: null,
                Span: terminal.Span,
                Descriptor: null);
        }

        // Anything else (Where, AsNoTracking, a house IQueryable extension) leaves the chain deferred.
        return Synthesize(chain, options, diagnostics);
    }

    private static TerminalOperatorInfo Synthesize(
        QueryChain chain,
        AnalyzerOptions options,
        ICollection<AnalysisDiagnostic> diagnostics)
    {
        var terminalName = ChooseSynthesizedTerminal(chain, options);
        var text = "." + terminalName + "()";

        diagnostics.Add(AnalysisDiagnostic.Info(
            AnalysisDiagnosticIds.NoTerminalOperator,
            $"No terminal operator in the selection - {terminalName}() was added to produce SQL.",
            chain.Root.Span));

        return new TerminalOperatorInfo(
            Name: null,
            Source: TerminalSource.Synthesized,
            IsAsync: terminalName.EndsWith("Async", StringComparison.Ordinal),
            IsAwaited: chain.IsAwaited,
            Shape: ResultShape.DeferredQueryable,
            HasPredicateArgument: false,
            ArgumentCount: 0,
            TypeArguments: Array.Empty<string>(),
            ArgumentTexts: Array.Empty<string>(),
            SynthesizedTerminalText: text,
            Span: chain.Root.Span,
            Descriptor: TerminalOperatorCatalog.Lookup(terminalName));
    }

    private static string ChooseSynthesizedTerminal(QueryChain chain, AnalyzerOptions options)
    {
        var configured = string.IsNullOrWhiteSpace(options.SynthesizedTerminal) ? "ToListAsync" : options.SynthesizedTerminal;

        if (!configured.EndsWith("Async", StringComparison.Ordinal))
        {
            return configured;
        }

        // ToListAsync only exists once Microsoft.EntityFrameworkCore is imported, and awaiting it only compiles
        // inside an async method. Fall back to the synchronous form when neither holds.
        return IsInsideAsyncMember(chain.Root) || ImportsEntityFrameworkCore(chain.Root)
            ? configured
            : TrimAsyncSuffix(configured);
    }

    private static string TrimAsyncSuffix(string name)
        => name.Substring(0, name.Length - "Async".Length);

    private static bool IsInsideAsyncMember(SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case MethodDeclarationSyntax method:
                    return method.Modifiers.Any(SyntaxKind.AsyncKeyword);
                case LocalFunctionStatementSyntax localFunction:
                    return localFunction.Modifiers.Any(SyntaxKind.AsyncKeyword);
                case AnonymousFunctionExpressionSyntax lambda:
                    return lambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword);
                case AccessorDeclarationSyntax:
                case ConstructorDeclarationSyntax:
                    return false;
            }
        }

        // Top-level statements: the generated entry point is async-capable.
        return node.FirstAncestorOrSelf<GlobalStatementSyntax>() is not null;
    }

    private static bool ImportsEntityFrameworkCore(SyntaxNode node)
    {
        // Only the directives actually in scope are checked: a full descendant walk would cost more than the
        // rest of the analysis on a large document.
        foreach (var ancestor in node.Ancestors())
        {
            var usings = ancestor switch
            {
                BaseNamespaceDeclarationSyntax ns => ns.Usings,
                CompilationUnitSyntax unit => unit.Usings,
                _ => default,
            };

            foreach (var directive in usings)
            {
                if (directive.Name?.ToString() == EntityFrameworkNamespace)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void ReportChainPositionDiagnostics(QueryChain chain, ICollection<AnalysisDiagnostic> diagnostics)
    {
        foreach (var call in chain.Calls)
        {
            var name = QueryChainWalker.CallName(call);
            if (name is null)
            {
                continue;
            }

            if (name == "AsSplitQuery")
            {
                diagnostics.Add(AnalysisDiagnostic.Warning(
                    AnalysisDiagnosticIds.MayProduceMultipleCommands,
                    "AsSplitQuery makes EF Core issue several commands; the preview may show only the first.",
                    call.Span));
            }
            else if (name is "AsEnumerable" or "ToLookup" or "AsAsyncEnumerable")
            {
                diagnostics.Add(AnalysisDiagnostic.Warning(
                    AnalysisDiagnosticIds.ClientEvaluationBoundary,
                    $"'{name}' ends server translation; anything after it runs on the client and produces no SQL.",
                    call.Span));
            }
        }
    }

    private static bool HasOrderBy(QueryChain chain)
    {
        foreach (var call in chain.Calls)
        {
            var name = QueryChainWalker.CallName(call);
            if (name is "OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending" or "Order" or "OrderDescending")
            {
                return true;
            }
        }

        return chain.IsQuerySyntax
            && chain.Root.DescendantNodes().OfType<OrderByClauseSyntax>().Any();
    }

    private static ArgumentDescription DescribeArguments(InvocationExpressionSyntax invocation)
    {
        var arguments = invocation.ArgumentList.Arguments;
        var texts = new string[arguments.Count];
        var hasLambda = false;

        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            texts[i] = argument.ToString();
            if (argument.Expression is AnonymousFunctionExpressionSyntax)
            {
                hasLambda = true;
            }
        }

        return new ArgumentDescription(texts, hasLambda);
    }

    private readonly struct ArgumentDescription
    {
        public ArgumentDescription(IReadOnlyList<string> texts, bool hasLambda)
        {
            this.Texts = texts;
            this.HasLambda = hasLambda;
        }

        public IReadOnlyList<string> Texts { get; }

        public bool HasLambda { get; }
    }
}
