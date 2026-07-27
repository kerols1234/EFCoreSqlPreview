using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFCoreSqlPreview.Core.Analysis;

/// <summary>
/// Decomposes a resolved expression into its head receiver and the chain of calls applied to it.
/// </summary>
public static class QueryChainWalker
{
    /// <summary>Walks an expression into a <see cref="QueryChain"/>.</summary>
    /// <param name="expression">The resolved query expression, with or without a leading <c>await</c>.</param>
    /// <returns>The decomposed chain. Calls are ordered head-first.</returns>
    public static QueryChain Walk(ExpressionSyntax expression)
    {
        if (expression is null)
        {
            throw new ArgumentNullException(nameof(expression));
        }

        var isAwaited = expression is AwaitExpressionSyntax;
        var node = isAwaited ? ((AwaitExpressionSyntax)expression).Expression : expression;

        // Query syntax has no receiver chain to walk: the source is the first from clause.
        var query = FindQueryExpression(node);
        if (query is not null)
        {
            var queryCalls = new List<InvocationExpressionSyntax>();
            return new QueryChain(
                expression,
                query.FromClause.Expression,
                isAwaited,
                queryCalls,
                IsQuerySyntax: true);
        }

        var calls = new List<InvocationExpressionSyntax>();
        var head = WalkCore(node, calls);
        return new QueryChain(expression, head, isAwaited, calls, IsQuerySyntax: false);
    }

    /// <summary>Extracts the method name from an invocation.</summary>
    /// <param name="invocation">The invocation to inspect.</param>
    /// <returns>The name, or <see langword="null"/> when the callee is not a simple name.</returns>
    public static string? CallName(InvocationExpressionSyntax invocation)
        => NameOf(invocation)?.Identifier.ValueText;

    /// <summary>Extracts explicit type arguments from an invocation, e.g. <c>Product</c> from <c>Set&lt;Product&gt;()</c>.</summary>
    /// <param name="invocation">The invocation to inspect.</param>
    /// <returns>The type argument texts in order; empty when the call is not generic.</returns>
    public static IReadOnlyList<string> TypeArguments(InvocationExpressionSyntax invocation)
    {
        if (NameOf(invocation) is not GenericNameSyntax generic)
        {
            return Array.Empty<string>();
        }

        var arguments = generic.TypeArgumentList.Arguments;
        var result = new string[arguments.Count];
        for (var i = 0; i < arguments.Count; i++)
        {
            result[i] = arguments[i].ToString();
        }

        return result;
    }

    /// <summary>
    /// Whether a chain looks like a query: it has at least one call, and either a recognised operator name or a
    /// head that resolves to a DbContext-typed identifier.
    /// </summary>
    /// <param name="chain">The chain to test.</param>
    /// <returns><see langword="true"/> when the chain is worth previewing.</returns>
    public static bool IsQueryShaped(QueryChain chain)
    {
        if (chain is null)
        {
            return false;
        }

        if (chain.IsQuerySyntax)
        {
            return true;
        }

        foreach (var call in chain.Calls)
        {
            var name = CallName(call);
            if (name is null)
            {
                continue;
            }

            if (TerminalOperatorCatalog.Lookup(name) is not null || TerminalOperatorCatalog.IsDeferredOperator(name))
            {
                return true;
            }
        }

        // A chain of nothing but house extension methods (_db.Products.ActiveOnly()) is still a query when the
        // head is a context. A bare identifier with no calls at all never is.
        return chain.Calls.Count > 0 && HeadLooksLikeAContext(chain.Head);
    }

    /// <summary>Projects a chain's invocations into the serializable <see cref="ChainCallInfo"/> form.</summary>
    /// <param name="chain">The chain to describe.</param>
    /// <returns>One entry per call, head-first.</returns>
    public static IReadOnlyList<ChainCallInfo> Describe(QueryChain chain)
    {
        if (chain is null)
        {
            return Array.Empty<ChainCallInfo>();
        }

        var described = new List<ChainCallInfo>(chain.Calls.Count);
        foreach (var call in chain.Calls)
        {
            var name = CallName(call);
            if (name is null)
            {
                continue;
            }

            described.Add(new ChainCallInfo(
                name,
                call.ArgumentList.Arguments.Count,
                TypeArguments(call),
                call.Span));
        }

        return described;
    }

    private static ExpressionSyntax WalkCore(ExpressionSyntax node, List<InvocationExpressionSyntax> calls)
    {
        while (true)
        {
            switch (node)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    node = parenthesized.Expression;
                    continue;

                case AwaitExpressionSyntax awaited:
                    node = awaited.Expression;
                    continue;

                case InvocationExpressionSyntax invocation:
                    calls.Insert(0, invocation);
                    node = invocation.Expression;
                    continue;

                // 'this._db' and 'base._db' are the head, not a step on the way to one: the receiver carries
                // no information and dropping it would lose the identifier that names the context.
                case MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax or BaseExpressionSyntax }:
                    return node;

                case MemberAccessExpressionSyntax memberAccess:
                    node = memberAccess.Expression;
                    continue;

                case MemberBindingExpressionSyntax:
                    // Reached the '.' of a null-conditional access; the receiver lives on the parent.
                    return node;

                case ConditionalAccessExpressionSyntax conditional:
                    // The calls hang off WhenNotNull, so collect them before moving to the real receiver.
                    WalkCore(conditional.WhenNotNull, calls);
                    node = conditional.Expression;
                    continue;

                case ElementAccessExpressionSyntax elementAccess:
                    node = elementAccess.Expression;
                    continue;

                case PostfixUnaryExpressionSyntax postfix when postfix.OperatorToken.IsKind(SyntaxKind.ExclamationToken):
                    node = postfix.Operand;
                    continue;

                default:
                    return node;
            }
        }
    }

    private static QueryExpressionSyntax? FindQueryExpression(ExpressionSyntax node)
    {
        while (true)
        {
            switch (node)
            {
                case QueryExpressionSyntax query:
                    return query;
                case ParenthesizedExpressionSyntax parenthesized:
                    node = parenthesized.Expression;
                    continue;
                default:
                    return null;
            }
        }
    }

    private static SimpleNameSyntax? NameOf(InvocationExpressionSyntax invocation)
        => invocation?.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name,
            MemberBindingExpressionSyntax memberBinding => memberBinding.Name,
            SimpleNameSyntax simpleName => simpleName,
            _ => null,
        };

    private static bool HeadLooksLikeAContext(ExpressionSyntax head)
    {
        var name = head switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
            ThisExpressionSyntax => "this",
            _ => null,
        };

        if (name is null)
        {
            return false;
        }

        var declaration = DeclarationLookup.Find(head, name);
        var typeText = declaration?.TypeText;
        return typeText is not null && typeText.IndexOf("Context", StringComparison.Ordinal) >= 0;
    }
}
