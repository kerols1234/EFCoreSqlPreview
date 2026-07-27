using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFCoreSqlPreview.Core.Analysis;

/// <summary>
/// Identifies the DbContext behind a query chain, and the DbSet the query starts from.
/// </summary>
public static class ContextRootResolver
{
    private static readonly HashSet<string> SetAccessors = new(StringComparer.Ordinal)
    {
        "Set", "Query", "Entry", "Local",
    };

    /// <summary>Resolves the context root of a chain.</summary>
    /// <param name="chain">The decomposed chain.</param>
    /// <param name="root">The syntax root of the document, used for declaration lookup.</param>
    /// <param name="options">Analysis options; <see cref="AnalyzerOptions.DbContextTypeOverride"/> wins over inference.</param>
    /// <param name="diagnostics">Collector for EFSP0030-EFSP0033 diagnostics.</param>
    /// <returns>The context root; <see cref="ContextRootInfo.Unresolved"/> when the head cannot be classified.</returns>
    public static ContextRootInfo Resolve(
        QueryChain chain,
        SyntaxNode root,
        AnalyzerOptions options,
        ICollection<AnalysisDiagnostic> diagnostics)
    {
        if (chain is null)
        {
            return ContextRootInfo.Unresolved;
        }

        options ??= AnalyzerOptions.Default;
        diagnostics ??= new List<AnalysisDiagnostic>();

        return Resolve(chain, options, diagnostics, depth: 0);
    }

    /// <summary>
    /// Rewrites the context root inside a query expression to the reserved worker identifier, by span surgery
    /// on the original text rather than string search.
    /// </summary>
    /// <remarks>
    /// Every reference is rewritten, not just the chain head. A query-syntax join or a correlated subquery
    /// mentions the context more than once (<c>from o in this.db.Orders join c in this.db.Customers …</c>);
    /// leaving the later ones alone emits <c>this</c> into the worker, where there is no enclosing instance.
    /// </remarks>
    /// <param name="chain">The decomposed chain.</param>
    /// <param name="contextRoot">The resolved context root.</param>
    /// <param name="replacementIdentifier">The identifier to substitute, normally <c>__efspCtx</c>.</param>
    /// <returns>The query text with the root replaced.</returns>
    public static string NormalizeQueryText(QueryChain chain, ContextRootInfo contextRoot, string replacementIdentifier)
    {
        if (chain is null)
        {
            return string.Empty;
        }

        var text = chain.Root.ToString();
        if (contextRoot is null || string.IsNullOrEmpty(replacementIdentifier))
        {
            return text;
        }

        // A query built through a local (q = q.Where(...)) mentions the context only in the replayed prologue,
        // so the query text itself has nothing to rewrite.
        if (contextRoot.Kind is ContextRootKind.Unresolved or ContextRootKind.LocalQuery)
        {
            return text;
        }

        var rootSpan = chain.Root.Span;
        var seed = rootSpan.Contains(contextRoot.Span) && contextRoot.Span.Length > 0
            ? contextRoot.Span
            : (Microsoft.CodeAnalysis.Text.TextSpan?)null;

        var spans = CollectRootSpans(chain.Root, contextRoot.Expression, seed);
        return ApplyReplacements(text, rootSpan.Start, spans, replacementIdentifier);
    }

    /// <summary>
    /// Rewrites every reference to the context root inside a replayed statement to the reserved worker
    /// identifier.
    /// </summary>
    /// <remarks>
    /// Replayed statements are otherwise copied verbatim, but a statement such as
    /// <c>var q = this.db.Products.AsQueryable();</c> cannot compile in the worker: there is no <c>this</c>
    /// there. Only receiver positions are rewritten, so a member that merely shares the name is untouched.
    /// </remarks>
    /// <param name="statement">The statement to rewrite.</param>
    /// <param name="contextRoot">The resolved context root.</param>
    /// <param name="replacementIdentifier">The identifier to substitute, normally <c>__efspCtx</c>.</param>
    /// <returns>The statement's source text with the root replaced.</returns>
    public static string NormalizeStatementText(
        SyntaxNode statement,
        ContextRootInfo contextRoot,
        string replacementIdentifier)
    {
        if (statement is null)
        {
            return string.Empty;
        }

        var text = statement.ToString();
        var rootText = contextRoot?.Expression;
        if (string.IsNullOrEmpty(rootText)
            || string.IsNullOrEmpty(replacementIdentifier)
            || contextRoot!.Kind == ContextRootKind.Unresolved)
        {
            return text;
        }

        var spans = CollectRootSpans(statement, rootText, seed: null);
        return ApplyReplacements(text, statement.SpanStart, spans, replacementIdentifier);
    }

    /// <summary>
    /// Finds every span inside <paramref name="scope"/> that names the context root in receiver position,
    /// plus <paramref name="seed"/> when supplied, ordered and free of overlaps.
    /// </summary>
    private static List<Microsoft.CodeAnalysis.Text.TextSpan> CollectRootSpans(
        SyntaxNode scope,
        string? rootText,
        Microsoft.CodeAnalysis.Text.TextSpan? seed)
    {
        var found = new List<Microsoft.CodeAnalysis.Text.TextSpan>();
        if (seed is { } seedSpan)
        {
            found.Add(seedSpan);
        }

        if (!string.IsNullOrEmpty(rootText))
        {
            foreach (var node in scope.DescendantNodesAndSelf())
            {
                if (node is not (IdentifierNameSyntax or MemberAccessExpressionSyntax or ThisExpressionSyntax))
                {
                    continue;
                }

                if (IsReceiver(node) && node.ToString() == rootText)
                {
                    found.Add(node.Span);
                }
            }
        }

        // The seed and the syntactic sweep normally both find the chain head, and a dotted root such as
        // `_uow.Context` also matches its own outer node; keeping only the widest span at each start, and
        // dropping anything nested inside an accepted span, makes the rewrite idempotent.
        found.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.Length.CompareTo(a.Length));

        var accepted = new List<Microsoft.CodeAnalysis.Text.TextSpan>(found.Count);
        var consumedTo = -1;
        foreach (var span in found)
        {
            if (span.Start < consumedTo)
            {
                continue;
            }

            accepted.Add(span);
            consumedTo = span.End;
        }

        return accepted;
    }

    /// <summary>Substitutes <paramref name="replacement"/> at each span, back to front so offsets hold.</summary>
    private static string ApplyReplacements(
        string text,
        int offset,
        List<Microsoft.CodeAnalysis.Text.TextSpan> spans,
        string replacement)
    {
        for (var i = spans.Count - 1; i >= 0; i--)
        {
            var start = spans[i].Start - offset;
            if (start < 0 || start + spans[i].Length > text.Length)
            {
                continue;
            }

            text = text.Substring(0, start) + replacement + text.Substring(start + spans[i].Length);
        }

        return text;
    }

    private static bool IsReceiver(SyntaxNode node)
        => node.Parent switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Expression == node,
            ConditionalAccessExpressionSyntax conditional => conditional.Expression == node,
            _ => false,
        };

    private static ContextRootInfo Resolve(
        QueryChain chain,
        AnalyzerOptions options,
        ICollection<AnalysisDiagnostic> diagnostics,
        int depth)
    {
        var head = chain.Head;

        // A bare query expression's head is its from-clause source, which already includes the DbSet member.
        var sourceSetFromHead = (string?)null;
        var isQuerySource = chain.IsQuerySyntax;
        if (head is QueryExpressionSyntax queryExpression)
        {
            // '(from p in db.P select p).ToListAsync()' walks as a method chain whose head is the query itself.
            head = queryExpression.FromClause.Expression;
            isQuerySource = true;
        }

        if (isQuerySource && head is MemberAccessExpressionSyntax fromSource
            && fromSource.Expression is not ThisExpressionSyntax
            && fromSource.Expression is not BaseExpressionSyntax)
        {
            sourceSetFromHead = fromSource.Name.Identifier.ValueText;
            head = fromSource.Expression;
        }

        // 'this.Products.ToList()' inside the DbContext itself: the member is the DbSet, so 'this' is the root.
        if (head is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax or BaseExpressionSyntax } onThis
            && NamesADbSet(onThis))
        {
            sourceSetFromHead = onThis.Name.Identifier.ValueText;
            head = onThis.Expression;
        }

        if (sourceSetFromHead is null)
        {
            // The chain walker stops at the leftmost receiver, but only the last member access before the first
            // call is the DbSet: in '_uow.Context.Products.ToList()' the context is '_uow.Context', not '_uow'.
            head = ExtendHeadThroughQualifiers(head);
        }

        if (head.Parent is ConditionalAccessExpressionSyntax conditional && conditional.Expression == head)
        {
            diagnostics.Add(AnalysisDiagnostic.Info(
                AnalysisDiagnosticIds.ConditionalAccessOnContext,
                "The context is reached through '?.'; the preview drops the null-conditional because the context is never null.",
                head.Span));
        }

        var (kind, identifier) = ClassifyHead(head);
        if (kind == ContextRootKind.Unresolved)
        {
            diagnostics.Add(AnalysisDiagnostic.Warning(
                AnalysisDiagnosticIds.ContextRootUnresolved,
                $"The head of the chain ('{Truncate(head.ToString())}') is not an identifier the analyzer can resolve to a DbContext.",
                head.Span));

            return ContextRootInfo.Unresolved with
            {
                Expression = head.ToString(),
                Span = head.Span,
            };
        }

        var (sourceSetName, sourceSetTypeArgument) = ResolveSource(chain, head, sourceSetFromHead);

        if (!string.IsNullOrEmpty(options.DbContextTypeOverride))
        {
            return new ContextRootInfo(
                kind,
                head.ToString(),
                identifier,
                options.DbContextTypeOverride,
                ContextResolution.Unresolved,
                ContextTypeConfidence.Declared,
                sourceSetName,
                sourceSetTypeArgument,
                head.Span);
        }

        var declaration = identifier is null ? null : DeclarationLookup.Find(head, identifier);

        // The head may be a local holding an already-built query rather than the context itself.
        if (depth == 0 && declaration is not null && IsQueryBuilderLocal(declaration))
        {
            var real = ResolveThroughLocalQuery(declaration, options, diagnostics);
            if (real is not null)
            {
                return real with { Kind = ContextRootKind.LocalQuery, Resolution = ContextResolution.LocalQuery };
            }
        }

        var resolution = ToResolution(declaration?.Kind);
        var (typeName, confidence) = ResolveTypeName(declaration, head, diagnostics);

        if (confidence == ContextTypeConfidence.DeclaredInterface)
        {
            diagnostics.Add(AnalysisDiagnostic.Info(
                AnalysisDiagnosticIds.ContextMayBeInterface,
                $"'{typeName}' looks like an interface; the worker resolves the concrete DbContext at runtime.",
                head.Span));
        }

        return new ContextRootInfo(
            kind,
            head.ToString(),
            identifier,
            typeName,
            resolution,
            confidence,
            sourceSetName,
            sourceSetTypeArgument,
            head.Span);
    }

    private static ContextRootInfo? ResolveThroughLocalQuery(
        DeclarationLookupResult declaration,
        AnalyzerOptions options,
        ICollection<AnalysisDiagnostic> diagnostics)
    {
        if (declaration.Initializer is null)
        {
            return null;
        }

        var innerChain = QueryChainWalker.Walk(declaration.Initializer);
        if (innerChain.Head == declaration.Initializer && innerChain.Calls.Count == 0)
        {
            return null;
        }

        var resolved = Resolve(innerChain, options, diagnostics, depth: 1);
        return resolved.Kind == ContextRootKind.Unresolved ? null : resolved;
    }

    private static bool IsQueryBuilderLocal(DeclarationLookupResult declaration)
    {
        if (declaration.Kind is not (FreeVariableKind.ReproducibleLocal or FreeVariableKind.OpaqueLocal))
        {
            return false;
        }

        var typeText = declaration.TypeText;
        if (typeText is not null
            && typeText != "var"
            && typeText.IndexOf("Queryable", StringComparison.Ordinal) < 0
            && typeText.IndexOf("IEnumerable", StringComparison.Ordinal) < 0)
        {
            return false;
        }

        // 'var q = _db.Products.AsQueryable();' - the initializer is itself query shaped.
        return declaration.Initializer is not null
            && QueryChainWalker.IsQueryShaped(QueryChainWalker.Walk(declaration.Initializer));
    }

    /// <summary>
    /// Walks a head outward through the qualifying member accesses that precede the DbSet.
    /// </summary>
    /// <remarks>
    /// Everything up to but excluding the last member access before the first invocation belongs to the context
    /// expression. For '_db.Products.ToList()' that leaves the head at '_db'; for '_uow.Context.Products.ToList()'
    /// it moves the head to '_uow.Context', which is what the worker must substitute for.
    /// </remarks>
    private static ExpressionSyntax ExtendHeadThroughQualifiers(ExpressionSyntax head)
    {
        var extended = head;
        var current = head;

        while (current.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Expression == current)
        {
            if (IsCallTarget(memberAccess))
            {
                break;
            }

            extended = current;
            current = memberAccess;
        }

        return extended;
    }

    private static bool IsCallTarget(ExpressionSyntax expression)
        => expression.Parent is InvocationExpressionSyntax invocation && invocation.Expression == expression;

    private static (ContextRootKind Kind, string? Identifier) ClassifyHead(ExpressionSyntax head)
        => head switch
        {
            IdentifierNameSyntax identifier => (ContextRootKind.Identifier, identifier.Identifier.ValueText),
            ThisExpressionSyntax => (ContextRootKind.Identifier, "this"),
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } thisMember
                => (ContextRootKind.MemberAccessOnThis, thisMember.Name.Identifier.ValueText),
            MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax } baseMember
                => (ContextRootKind.MemberAccessOnThis, baseMember.Name.Identifier.ValueText),
            MemberAccessExpressionSyntax member
                => (ContextRootKind.QualifiedField, member.Name.Identifier.ValueText),
            _ => (ContextRootKind.Unresolved, null),
        };

    private static (string? SourceSetName, string? SourceSetTypeArgument) ResolveSource(
        QueryChain chain,
        ExpressionSyntax head,
        string? sourceSetFromHead)
    {
        if (sourceSetFromHead is not null)
        {
            return (sourceSetFromHead, null);
        }

        // Set<Product>() and friends replace the DbSet property entirely.
        if (chain.Calls.Count > 0)
        {
            var first = chain.Calls[0];
            var name = QueryChainWalker.CallName(first);
            if (name is not null && SetAccessors.Contains(name))
            {
                var typeArguments = QueryChainWalker.TypeArguments(first);
                return (null, typeArguments.Count > 0 ? typeArguments[0] : null);
            }
        }

        // Otherwise the DbSet is the member access immediately after the head.
        if (head.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Expression == head)
        {
            return (memberAccess.Name.Identifier.ValueText, null);
        }

        if (head.Parent is ConditionalAccessExpressionSyntax conditional
            && conditional.Expression == head
            && FirstBindingName(conditional.WhenNotNull) is string bound)
        {
            return (bound, null);
        }

        return (null, null);
    }

    private static string? FirstBindingName(ExpressionSyntax whenNotNull)
    {
        foreach (var node in whenNotNull.DescendantNodesAndSelf())
        {
            if (node is MemberBindingExpressionSyntax binding)
            {
                return binding.Name.Identifier.ValueText;
            }
        }

        return null;
    }

    private static (string? TypeName, ContextTypeConfidence Confidence) ResolveTypeName(
        DeclarationLookupResult? declaration,
        ExpressionSyntax head,
        ICollection<AnalysisDiagnostic> diagnostics)
    {
        if (declaration is null)
        {
            diagnostics.Add(AnalysisDiagnostic.Warning(
                AnalysisDiagnosticIds.ContextTypeNotResolvableSyntactically,
                "The DbContext type is not declared in this file; the worker discovers it at runtime.",
                head.Span));
            return (null, ContextTypeConfidence.Unknown);
        }

        var typeText = declaration.TypeText;

        if (!string.IsNullOrEmpty(typeText) && typeText != "var")
        {
            return (typeText, LooksLikeInterface(typeText!)
                ? ContextTypeConfidence.DeclaredInterface
                : ContextTypeConfidence.Declared);
        }

        if (declaration.Initializer is ObjectCreationExpressionSyntax creation)
        {
            return (creation.Type.ToString(), ContextTypeConfidence.InferredFromInitializer);
        }

        diagnostics.Add(AnalysisDiagnostic.Warning(
            AnalysisDiagnosticIds.ContextTypeNotResolvableSyntactically,
            "The DbContext type could not be inferred from the declaration; the worker discovers it at runtime.",
            head.Span));
        return (null, ContextTypeConfidence.Unknown);
    }

    private static bool NamesADbSet(MemberAccessExpressionSyntax memberAccess)
    {
        var declaration = DeclarationLookup.Find(memberAccess, memberAccess.Name.Identifier.ValueText);
        var typeText = declaration?.TypeText;
        return typeText is not null
            && (typeText.StartsWith("DbSet", StringComparison.Ordinal)
                || typeText.StartsWith("IQueryable", StringComparison.Ordinal));
    }

    private static bool LooksLikeInterface(string typeText)
        => typeText.Length > 1 && typeText[0] == 'I' && char.IsUpper(typeText[1]);

    private static ContextResolution ToResolution(FreeVariableKind? kind)
        => kind switch
        {
            FreeVariableKind.Field => ContextResolution.Field,
            FreeVariableKind.Property => ContextResolution.Property,
            FreeVariableKind.Parameter => ContextResolution.Parameter,
            FreeVariableKind.PrimaryConstructorParameter => ContextResolution.PrimaryConstructorParameter,
            FreeVariableKind.ReproducibleLocal => ContextResolution.Local,
            FreeVariableKind.OpaqueLocal => ContextResolution.Local,
            FreeVariableKind.Constant => ContextResolution.Field,
            _ => ContextResolution.Unresolved,
        };

    private static string Truncate(string text)
        => text.Length <= 60 ? text : text.Substring(0, 57) + "...";
}
