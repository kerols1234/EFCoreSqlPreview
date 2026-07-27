using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace EFCoreSqlPreview.Core.Analysis;

/// <summary>
/// Determines what the query's elements will be, from the last <c>Select</c>/<c>SelectMany</c> in the chain.
/// </summary>
public static class ProjectionClassifier
{
    /// <summary>Classifies the projection of a chain.</summary>
    /// <param name="chain">The decomposed chain.</param>
    /// <param name="diagnostics">Collector for EFSP0020-EFSP0022 diagnostics.</param>
    /// <returns>The projection description; <see cref="ProjectionInfo.None"/> when there is no <c>Select</c>.</returns>
    public static ProjectionInfo Classify(QueryChain chain, ICollection<AnalysisDiagnostic> diagnostics)
    {
        if (chain is null)
        {
            return ProjectionInfo.None;
        }

        diagnostics ??= new List<AnalysisDiagnostic>();

        // ProjectTo wins over everything: the mapping lives in an AutoMapper profile that syntax cannot reach.
        var projectTo = FindCall(chain, "ProjectTo");
        if (projectTo is not null)
        {
            var typeArguments = QueryChainWalker.TypeArguments(projectTo);
            var typeName = typeArguments.Count > 0 ? typeArguments[0] : null;

            diagnostics.Add(AnalysisDiagnostic.Error(
                AnalysisDiagnosticIds.ProjectToRequiresMapper,
                "ProjectTo<T> needs an AutoMapper configuration that cannot be constructed automatically.",
                projectTo.Span));

            return new ProjectionInfo(
                ProjectionKind.Unresolvable,
                typeName is null ? ElementKind.Unknown : ElementKind.Dto,
                typeName,
                Array.Empty<string>(),
                ProjectionLambdaText: null,
                IsGroupedProjection: false,
                projectTo.Span);
        }

        var queryExpression = FindQueryExpression(chain);
        if (queryExpression is not null)
        {
            return ClassifyQuerySyntax(queryExpression, diagnostics);
        }

        var selectIndex = FindLastIndex(chain, "Select", "SelectMany");
        if (selectIndex < 0)
        {
            return WithoutSelect(chain);
        }

        var select = chain.Calls[selectIndex];
        var arguments = select.ArgumentList.Arguments;
        if (arguments.Count == 0)
        {
            return ProjectionInfo.None;
        }

        var projectionArgument = arguments[arguments.Count - 1].Expression;
        var isGrouped = FindLastIndex(chain, "GroupBy") >= 0 && FindLastIndex(chain, "GroupBy") < selectIndex;

        var body = BodyOf(projectionArgument);
        var info = ClassifyBody(body, projectionArgument.ToString(), select.Span, isGrouped, diagnostics);

        // Cast<T>/OfType<T> after the projection change the element type without being a Select.
        var castIndex = FindLastIndex(chain, "Cast", "OfType");
        if (castIndex > selectIndex)
        {
            var castArguments = QueryChainWalker.TypeArguments(chain.Calls[castIndex]);
            if (castArguments.Count > 0)
            {
                info = info with { TypeName = castArguments[0], ElementKind = ElementKind.Entity };
            }
        }

        return info;
    }

    private static ProjectionInfo WithoutSelect(QueryChain chain)
    {
        // No projection: whole entities come back. Cast<T>/OfType<T>/Set<T> still name the element type.
        var castIndex = FindLastIndex(chain, "Cast", "OfType", "Set");
        string? typeName = null;
        if (castIndex >= 0)
        {
            var typeArguments = QueryChainWalker.TypeArguments(chain.Calls[castIndex]);
            if (typeArguments.Count > 0)
            {
                typeName = typeArguments[0];
            }
        }

        var isGrouped = FindLastIndex(chain, "GroupBy") >= 0;

        return new ProjectionInfo(
            isGrouped ? ProjectionKind.Grouping : ProjectionKind.None,
            isGrouped ? ElementKind.Grouping : ElementKind.Entity,
            typeName,
            Array.Empty<string>(),
            ProjectionLambdaText: null,
            IsGroupedProjection: isGrouped,
            Span: default);
    }

    private static ProjectionInfo ClassifyQuerySyntax(
        QueryExpressionSyntax query,
        ICollection<AnalysisDiagnostic> diagnostics)
    {
        var selectOrGroup = LastSelectOrGroup(query);

        switch (selectOrGroup)
        {
            case GroupClauseSyntax group:
                return new ProjectionInfo(
                    ProjectionKind.Grouping,
                    ElementKind.Grouping,
                    TypeName: null,
                    Array.Empty<string>(),
                    group.ToString(),
                    IsGroupedProjection: true,
                    group.Span);

            case SelectClauseSyntax select:
                return ClassifyBody(
                    select.Expression,
                    select.Expression.ToString(),
                    select.Span,
                    isGrouped: false,
                    diagnostics);

            default:
                return ProjectionInfo.None;
        }
    }

    private static SelectOrGroupClauseSyntax? LastSelectOrGroup(QueryExpressionSyntax query)
    {
        var body = query.Body;
        while (body.Continuation is not null)
        {
            body = body.Continuation.Body;
        }

        return body.SelectOrGroup;
    }

    private static ProjectionInfo ClassifyBody(
        SyntaxNode? body,
        string lambdaText,
        TextSpan span,
        bool isGrouped,
        ICollection<AnalysisDiagnostic> diagnostics)
    {
        switch (body)
        {
            case null:
                return new ProjectionInfo(
                    ProjectionKind.Unresolvable, ElementKind.Unknown, null, Array.Empty<string>(),
                    lambdaText, isGrouped, span);

            case BlockSyntax:
                diagnostics.Add(AnalysisDiagnostic.Warning(
                    AnalysisDiagnosticIds.StatementLambdaProjection,
                    "A statement-lambda projection cannot be translated by EF Core and cannot be classified here.",
                    span));
                return new ProjectionInfo(
                    ProjectionKind.Unresolvable, ElementKind.Unknown, null, Array.Empty<string>(),
                    lambdaText, isGrouped, span);

            case ObjectCreationExpressionSyntax objectCreation:
                return new ProjectionInfo(
                    ProjectionKind.NamedType, ElementKind.Dto, objectCreation.Type.ToString(),
                    MemberNamesOf(objectCreation.Initializer), lambdaText, isGrouped, span);

            case ImplicitObjectCreationExpressionSyntax implicitCreation:
                diagnostics.Add(AnalysisDiagnostic.Warning(
                    AnalysisDiagnosticIds.TargetTypedNewNotResolvable,
                    "The projected type of a target-typed 'new(...)' is not recoverable from syntax alone.",
                    span));
                return new ProjectionInfo(
                    ProjectionKind.NamedType, ElementKind.Dto, null,
                    MemberNamesOf(implicitCreation.Initializer), lambdaText, isGrouped, span);

            case AnonymousObjectCreationExpressionSyntax anonymous:
                return new ProjectionInfo(
                    ProjectionKind.Anonymous, ElementKind.Anonymous, null,
                    AnonymousMemberNamesOf(anonymous), lambdaText, isGrouped, span);

            case TupleExpressionSyntax tuple:
                return new ProjectionInfo(
                    ProjectionKind.Tuple, ElementKind.Tuple, null,
                    TupleMemberNamesOf(tuple), lambdaText, isGrouped, span);

            case MemberAccessExpressionSyntax memberAccess:
                return new ProjectionInfo(
                    ProjectionKind.ScalarMember, ElementKind.Scalar, memberAccess.Name.Identifier.ValueText,
                    new[] { memberAccess.Name.Identifier.ValueText }, lambdaText, isGrouped, span);

            case IdentifierNameSyntax identifier:
                return new ProjectionInfo(
                    ProjectionKind.ScalarMember, ElementKind.Scalar, identifier.Identifier.ValueText,
                    Array.Empty<string>(), lambdaText, isGrouped, span);

            case CastExpressionSyntax cast:
                return new ProjectionInfo(
                    ProjectionKind.ScalarMember, ElementKind.Scalar, cast.Type.ToString(),
                    Array.Empty<string>(), lambdaText, isGrouped, span);

            case LiteralExpressionSyntax literal:
                return new ProjectionInfo(
                    ProjectionKind.ScalarMember, ElementKind.Scalar, LiteralTypeName(literal),
                    Array.Empty<string>(), lambdaText, isGrouped, span);

            default:
                return new ProjectionInfo(
                    ProjectionKind.Computed, ElementKind.Scalar, null, Array.Empty<string>(),
                    lambdaText, isGrouped, span);
        }
    }

    private static SyntaxNode? BodyOf(ExpressionSyntax projectionArgument)
        => projectionArgument switch
        {
            SimpleLambdaExpressionSyntax simple => simple.ExpressionBody ?? (SyntaxNode?)simple.Block,
            ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.ExpressionBody ?? (SyntaxNode?)parenthesized.Block,
            _ => null,
        };

    private static IReadOnlyList<string> MemberNamesOf(InitializerExpressionSyntax? initializer)
    {
        if (initializer is null)
        {
            return Array.Empty<string>();
        }

        var names = new List<string>();
        foreach (var expression in initializer.Expressions)
        {
            if (expression is AssignmentExpressionSyntax { Left: IdentifierNameSyntax member })
            {
                names.Add(member.Identifier.ValueText);
            }
        }

        return names;
    }

    private static IReadOnlyList<string> AnonymousMemberNamesOf(AnonymousObjectCreationExpressionSyntax anonymous)
    {
        var names = new List<string>();
        foreach (var declarator in anonymous.Initializers)
        {
            if (declarator.NameEquals is not null)
            {
                names.Add(declarator.NameEquals.Name.Identifier.ValueText);
            }
            else if (declarator.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                names.Add(memberAccess.Name.Identifier.ValueText);
            }
            else if (declarator.Expression is IdentifierNameSyntax identifier)
            {
                names.Add(identifier.Identifier.ValueText);
            }
        }

        return names;
    }

    private static IReadOnlyList<string> TupleMemberNamesOf(TupleExpressionSyntax tuple)
    {
        var names = new List<string>();
        foreach (var argument in tuple.Arguments)
        {
            if (argument.NameColon is not null)
            {
                names.Add(argument.NameColon.Name.Identifier.ValueText);
            }
            else if (argument.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                names.Add(memberAccess.Name.Identifier.ValueText);
            }
        }

        return names;
    }

    private static string? LiteralTypeName(LiteralExpressionSyntax literal)
        => literal.Kind() switch
        {
            SyntaxKind.StringLiteralExpression => "string",
            SyntaxKind.CharacterLiteralExpression => "char",
            SyntaxKind.TrueLiteralExpression => "bool",
            SyntaxKind.FalseLiteralExpression => "bool",
            SyntaxKind.NullLiteralExpression => null,
            SyntaxKind.NumericLiteralExpression => NumericLiteralTypeName(literal.Token.Text),
            _ => null,
        };

    private static string NumericLiteralTypeName(string text)
    {
        if (text.Length == 0)
        {
            return "int";
        }

        var last = char.ToLowerInvariant(text[text.Length - 1]);
        return last switch
        {
            'm' => "decimal",
            'd' => "double",
            'f' => "float",
            'l' => "long",
            'u' => "uint",
            _ => text.IndexOf('.') >= 0 ? "double" : "int",
        };
    }

    private static QueryExpressionSyntax? FindQueryExpression(QueryChain chain)
    {
        if (chain.IsQuerySyntax)
        {
            var node = chain.Root is AwaitExpressionSyntax awaited ? awaited.Expression : chain.Root;
            while (node is ParenthesizedExpressionSyntax parenthesized)
            {
                node = parenthesized.Expression;
            }

            return node as QueryExpressionSyntax;
        }

        // '(from p in db.P select p).ToListAsync()' walks as a normal chain whose head is the query expression.
        return chain.Head as QueryExpressionSyntax;
    }

    private static InvocationExpressionSyntax? FindCall(QueryChain chain, string name)
    {
        foreach (var call in chain.Calls)
        {
            if (QueryChainWalker.CallName(call) == name)
            {
                return call;
            }
        }

        return null;
    }

    private static int FindLastIndex(QueryChain chain, params string[] names)
    {
        for (var i = chain.Calls.Count - 1; i >= 0; i--)
        {
            var name = QueryChainWalker.CallName(chain.Calls[i]);
            if (name is null)
            {
                continue;
            }

            foreach (var candidate in names)
            {
                if (name == candidate)
                {
                    return i;
                }
            }
        }

        return -1;
    }
}
