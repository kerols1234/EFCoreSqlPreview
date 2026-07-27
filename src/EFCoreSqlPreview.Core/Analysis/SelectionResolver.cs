using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace EFCoreSqlPreview.Core.Analysis;

/// <summary>
/// The outcome of turning a normalised span into a syntax node.
/// </summary>
/// <param name="Expression">The resolved expression, or <see langword="null"/> when none was found.</param>
/// <param name="Block">The enclosing block when the selection spans several statements; otherwise <see langword="null"/>.</param>
/// <param name="Anchor">The statement the query lives in, used as the cut point for the prologue slice.</param>
/// <param name="Status">Why resolution succeeded or failed.</param>
public sealed record SelectionResolution(
    ExpressionSyntax? Expression,
    BlockSyntax? Block,
    StatementSyntax? Anchor,
    AnalysisStatus Status);

/// <summary>
/// Locates the query expression a selection refers to: descend from a statement, ascend out of a lambda,
/// then peel wrappers.
/// </summary>
public static class SelectionResolver
{
    /// <summary>Resolves a normalised span to a query expression.</summary>
    /// <param name="root">The syntax root of the document.</param>
    /// <param name="normalizedSpan">A span already passed through <see cref="SelectionNormalizer"/>.</param>
    /// <returns>The resolution; never throws.</returns>
    public static SelectionResolution Resolve(SyntaxNode root, TextSpan normalizedSpan)
    {
        if (root is null)
        {
            return new SelectionResolution(null, null, null, AnalysisStatus.NoQueryFound);
        }

        var node = FindNodeSafely(root, normalizedSpan);
        if (node is null)
        {
            return new SelectionResolution(null, null, null, AnalysisStatus.NoQueryFound);
        }

        // A GlobalStatement is a pure wrapper around a top-level statement; unwrap before anything else.
        if (node is GlobalStatementSyntax globalStatement)
        {
            node = globalStatement.Statement;
        }

        // Multi-statement selections land on the container rather than on any one statement.
        var containedStatements = GetContainedStatements(node);
        if (containedStatements is not null)
        {
            return ResolveMultiStatement(node, containedStatements, normalizedSpan);
        }

        var expression = node as ExpressionSyntax ?? DescendFromStatement(node, normalizedSpan);
        if (expression is null)
        {
            return new SelectionResolution(
                null,
                null,
                node.FirstAncestorOrSelf<StatementSyntax>(),
                AnalysisStatus.NoQueryFound);
        }

        return Finish(expression, block: null);
    }

    /// <summary>
    /// Descends a statement or declaration node to the expression it contains, e.g. a local declaration to
    /// its single initializer value. Runs exactly once, before any ascent.
    /// </summary>
    /// <param name="node">The node <c>FindNode</c> returned.</param>
    /// <param name="selection">The normalised selection, used to pick between multiple declarators.</param>
    /// <returns>The contained expression, or <see langword="null"/> when the node holds none.</returns>
    public static ExpressionSyntax? DescendFromStatement(SyntaxNode node, TextSpan selection)
    {
        switch (node)
        {
            case null:
                return null;

            case ExpressionSyntax expression:
                return expression;

            case LocalDeclarationStatementSyntax local:
                return DescendFromDeclaration(local.Declaration, selection);

            case FieldDeclarationSyntax field:
                return DescendFromDeclaration(field.Declaration, selection);

            case VariableDeclarationSyntax declaration:
                return DescendFromDeclaration(declaration, selection);

            case VariableDeclaratorSyntax declarator:
                return declarator.Initializer?.Value;

            case ExpressionStatementSyntax expressionStatement:
                return expressionStatement.Expression;

            case ReturnStatementSyntax returnStatement:
                return returnStatement.Expression;

            case ForEachStatementSyntax forEach:
                return forEach.Expression;

            case ForEachVariableStatementSyntax forEachVariable:
                return forEachVariable.Expression;

            case ArrowExpressionClauseSyntax arrow:
                return arrow.Expression;

            case EqualsValueClauseSyntax equalsValue:
                return equalsValue.Value;

            case YieldStatementSyntax yield:
                return yield.Expression;

            case ArgumentSyntax argument:
                return argument.Expression;

            case AttributeArgumentSyntax attributeArgument:
                return attributeArgument.Expression;

            case UsingStatementSyntax usingStatement:
                return usingStatement.Expression
                    ?? (usingStatement.Declaration is null ? null : DescendFromDeclaration(usingStatement.Declaration, selection));

            case AnonymousObjectMemberDeclaratorSyntax memberDeclarator:
                return memberDeclarator.Expression;

            // A selection landing on part of a query expression means the whole query expression.
            case QueryClauseSyntax:
            case SelectOrGroupClauseSyntax:
            case QueryBodySyntax:
            case QueryContinuationSyntax:
            case OrderingSyntax:
            case JoinIntoClauseSyntax:
                return node.FirstAncestorOrSelf<QueryExpressionSyntax>();

            case PropertyDeclarationSyntax property:
                return property.Initializer?.Value ?? property.ExpressionBody?.Expression;

            case MethodDeclarationSyntax method:
                return method.ExpressionBody?.Expression;

            default:
                return null;
        }
    }

    /// <summary>
    /// Climbs while the parent is transparent, so a selection inside a lambda body or an object initializer
    /// escapes outward to the whole query chain.
    /// </summary>
    /// <param name="node">The expression to climb from.</param>
    /// <returns>The outermost expression of the chain.</returns>
    public static ExpressionSyntax AscendToOutermostExpression(ExpressionSyntax node)
    {
        if (node is null)
        {
            throw new ArgumentNullException(nameof(node));
        }

        // The cursor may pass through nodes that are transparent but not expressions (Argument, ArgumentList),
        // so the last expression seen is tracked separately from the cursor itself.
        SyntaxNode cursor = node;
        var outermost = node;

        while (cursor.Parent is SyntaxNode parent && IsTransparent(parent))
        {
            cursor = parent;
            if (cursor is ExpressionSyntax expression)
            {
                outermost = expression;
            }
        }

        return outermost;
    }

    /// <summary>
    /// Removes parentheses, assignment left-hand sides and null-suppression operators, in a fixed-point loop.
    /// Must run after <see cref="AscendToOutermostExpression"/>: peeling first lets the climb walk straight back up.
    /// </summary>
    /// <param name="node">The expression to peel.</param>
    /// <returns>The peeled expression. <c>await</c> is deliberately left in place.</returns>
    public static ExpressionSyntax PeelWrappers(ExpressionSyntax node)
    {
        if (node is null)
        {
            throw new ArgumentNullException(nameof(node));
        }

        var changed = true;
        while (changed)
        {
            changed = false;

            switch (node)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    node = parenthesized.Expression;
                    changed = true;
                    break;

                case AssignmentExpressionSyntax assignment:
                    node = assignment.Right;
                    changed = true;
                    break;

                case PostfixUnaryExpressionSyntax postfix when postfix.OperatorToken.IsKind(SyntaxKind.ExclamationToken):
                    node = postfix.Operand;
                    changed = true;
                    break;
            }
        }

        return node;
    }

    /// <summary>
    /// Whether a node may be climbed through during ascent.
    /// </summary>
    /// <param name="node">The candidate parent node.</param>
    /// <returns><see langword="true"/> for expressions, arguments, argument lists and initializer members.</returns>
    public static bool IsTransparent(SyntaxNode node)
        => node is ExpressionSyntax
            or ArgumentSyntax
            or ArgumentListSyntax
            or BracketedArgumentListSyntax
            or AnonymousObjectMemberDeclaratorSyntax
            or InitializerExpressionSyntax
            // Query clauses are transparent too, so a selection inside 'where p.IsActive' escapes to the
            // whole 'from ... select ...' expression exactly as one inside a lambda escapes to its chain.
            or QueryClauseSyntax
            or SelectOrGroupClauseSyntax
            or QueryBodySyntax
            or QueryContinuationSyntax
            or OrderingSyntax
            or JoinIntoClauseSyntax;

    private static SelectionResolution ResolveMultiStatement(
        SyntaxNode container,
        IReadOnlyList<StatementSyntax> statements,
        TextSpan selection)
    {
        var candidates = new List<StatementSyntax>();
        foreach (var statement in statements)
        {
            if (statement.Span.IntersectsWith(selection))
            {
                candidates.Add(statement);
            }
        }

        if (candidates.Count == 0)
        {
            return new SelectionResolution(null, container as BlockSyntax, null, AnalysisStatus.NoQueryFound);
        }

        // The anchor is the last query-shaped candidate: query building runs forward, so the terminal
        // statement is the one worth previewing.
        ExpressionSyntax? bestExpression = null;
        StatementSyntax? bestAnchor = null;

        for (var i = candidates.Count - 1; i >= 0; i--)
        {
            var candidate = candidates[i];
            var descended = DescendFromStatement(candidate, selection);
            if (descended is null)
            {
                continue;
            }

            var expression = PeelWrappers(AscendToOutermostExpression(descended));

            bestExpression ??= expression;
            bestAnchor ??= candidate;

            if (QueryChainWalker.IsQueryShaped(QueryChainWalker.Walk(expression)))
            {
                bestExpression = expression;
                bestAnchor = candidate;
                break;
            }
        }

        if (bestExpression is null)
        {
            return new SelectionResolution(null, container as BlockSyntax, null, AnalysisStatus.NoQueryFound);
        }

        var block = container as BlockSyntax ?? bestAnchor?.Parent as BlockSyntax;
        return new SelectionResolution(bestExpression, block, bestAnchor, AnalysisStatus.Success);
    }

    private static SelectionResolution Finish(ExpressionSyntax expression, BlockSyntax? block)
    {
        var resolved = PeelWrappers(AscendToOutermostExpression(expression));
        var anchor = resolved.FirstAncestorOrSelf<StatementSyntax>();

        // Block stays null unless the selection genuinely spanned several statements: it is the signal that
        // earlier statements must be replayed, and setting it here would drag in every neighbouring declaration.
        return new SelectionResolution(resolved, block, anchor, AnalysisStatus.Success);
    }

    private static ExpressionSyntax? DescendFromDeclaration(VariableDeclarationSyntax declaration, TextSpan selection)
    {
        if (declaration.Variables.Count == 0)
        {
            return null;
        }

        // With several declarators the selection decides; the last intersecting one wins because that is the
        // one the user was looking at when they dragged to the end of the line.
        VariableDeclaratorSyntax? chosen = null;
        foreach (var declarator in declaration.Variables)
        {
            if (declarator.Initializer is not null && declarator.Span.IntersectsWith(selection))
            {
                chosen = declarator;
            }
        }

        chosen ??= declaration.Variables[declaration.Variables.Count - 1];
        return chosen.Initializer?.Value;
    }

    private static IReadOnlyList<StatementSyntax>? GetContainedStatements(SyntaxNode node)
        => node switch
        {
            BlockSyntax block => block.Statements,
            SwitchSectionSyntax section => section.Statements,
            CompilationUnitSyntax unit => unit.Members.OfType<GlobalStatementSyntax>().Select(g => g.Statement).ToList(),
            _ => null,
        };

    private static SyntaxNode? FindNodeSafely(SyntaxNode root, TextSpan span)
    {
        try
        {
            var full = root.FullSpan;
            var start = span.Start < full.Start ? full.Start : span.Start;
            var end = span.End > full.End ? full.End : span.End;
            if (end < start)
            {
                end = start;
            }

            return root.FindNode(TextSpan.FromBounds(start, end), findInsideTrivia: false, getInnermostNodeForTie: true);
        }
        catch (ArgumentOutOfRangeException)
        {
            // A span the caller mangled beyond what normalisation could repair: report "nothing found".
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
