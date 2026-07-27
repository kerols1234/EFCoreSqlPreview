using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFCoreSqlPreview.Core.Analysis;

/// <summary>
/// Decides whether an initializer expression can be pasted into the generated worker verbatim.
/// </summary>
/// <remarks>
/// This is a whitelist, and deliberately so: anything not explicitly allowed is treated as non-reproducible,
/// which sends the variable to the editable free-variables panel instead of producing a program that does not compile.
/// </remarks>
public static class InitializerReproducibility
{
    /// <summary>The deepest chain of local-to-local initializer inheritance that is followed.</summary>
    private const int DefaultTransitiveDepth = 3;

    /// <summary>Static members and calls that are always safe to reproduce, matched on rendered text.</summary>
    public static IReadOnlyCollection<string> WellKnownStatics { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "DateTime.Now", "DateTime.UtcNow", "DateTime.Today", "DateTime.MinValue", "DateTime.MaxValue",
        "DateTimeOffset.Now", "DateTimeOffset.UtcNow", "DateTimeOffset.MinValue", "DateTimeOffset.MaxValue",
        "DateOnly.MinValue", "DateOnly.MaxValue", "TimeOnly.MinValue", "TimeOnly.MaxValue",
        "Guid.Empty", "Guid.NewGuid",
        "string.Empty", "String.Empty",
        "int.MinValue", "int.MaxValue", "long.MinValue", "long.MaxValue",
        "decimal.Zero", "decimal.One",
        "double.NaN", "double.PositiveInfinity", "double.NegativeInfinity",
        "TimeSpan.Zero", "TimeSpan.FromDays", "TimeSpan.FromHours", "TimeSpan.FromMinutes",
        "TimeSpan.FromSeconds", "TimeSpan.FromMilliseconds",
        "Array.Empty", "Enumerable.Empty",
        "CancellationToken.None",
    };

    /// <summary>
    /// Instance members that may be chained onto an already-reproducible receiver, e.g. the
    /// <c>AddDays</c> in <c>DateTime.Now.AddDays(-30)</c>.
    /// </summary>
    public static IReadOnlyCollection<string> WellKnownContinuations { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "AddDays", "AddHours", "AddMinutes", "AddSeconds", "AddMonths", "AddYears",
        "AddMilliseconds", "AddTicks", "Add", "Subtract", "Date",
        "ToUniversalTime", "ToLocalTime", "ToString",
    };

    /// <summary>Whether an initializer can be reproduced verbatim in the worker.</summary>
    /// <param name="initializer">The initializer expression.</param>
    /// <returns><see langword="true"/> when the expression is on the whitelist, recursively.</returns>
    public static bool IsReproducible(ExpressionSyntax? initializer)
        => Classify(initializer) != ValueSource.SynthesizedDefault;

    /// <summary>Classifies how an initializer would be reproduced.</summary>
    /// <param name="initializer">The initializer expression.</param>
    /// <returns>
    /// The matching <see cref="ValueSource"/>, or <see cref="ValueSource.SynthesizedDefault"/> when the
    /// expression is not reproducible.
    /// </returns>
    public static ValueSource Classify(ExpressionSyntax? initializer)
        => Classify(initializer, DefaultTransitiveDepth);

    /// <summary>Classifies an initializer with an explicit cap on transitive local resolution.</summary>
    /// <param name="initializer">The initializer expression.</param>
    /// <param name="maxTransitiveDepth">How many local-to-local hops to follow.</param>
    /// <returns>The matching <see cref="ValueSource"/>.</returns>
    public static ValueSource Classify(ExpressionSyntax? initializer, int maxTransitiveDepth)
        => Classify(initializer, maxTransitiveDepth, followIdentifiers: true);

    /// <summary>
    /// Classifies an initializer without following identifiers to their declarations.
    /// </summary>
    /// <remarks>
    /// <see cref="DeclarationLookup"/> uses this form. The deep form calls back into the lookup to resolve
    /// transitive locals, so the lookup must not call the deep form or the two would recurse into each other.
    /// </remarks>
    /// <param name="initializer">The initializer expression.</param>
    /// <returns>The matching <see cref="ValueSource"/>.</returns>
    internal static ValueSource ClassifyShallow(ExpressionSyntax? initializer)
        => Classify(initializer, maxTransitiveDepth: 0, followIdentifiers: false);

    private static ValueSource Classify(ExpressionSyntax? initializer, int maxTransitiveDepth, bool followIdentifiers)
    {
        if (initializer is null)
        {
            return ValueSource.SynthesizedDefault;
        }

        var context = new Context(maxTransitiveDepth, followIdentifiers);
        var family = Evaluate(initializer, context);
        if (family == Family.NotReproducible)
        {
            return ValueSource.SynthesizedDefault;
        }

        // An interpolated string is reported as a literal even when it interpolates another local: the user
        // reads "$"%{term}%"" as a literal, and calling it transitive would be surprising in the UI.
        if (context.UsedTransitiveLocal && initializer is not InterpolatedStringExpressionSyntax)
        {
            return ValueSource.TransitiveLocal;
        }

        return family switch
        {
            Family.Literal => ValueSource.LiteralInitializer,
            Family.Constructible => ValueSource.ConstructibleInitializer,
            Family.WellKnownStatic => ValueSource.WellKnownStatic,
            _ => ValueSource.SynthesizedDefault,
        };
    }

    private enum Family
    {
        NotReproducible,
        Literal,
        Constructible,
        WellKnownStatic,
    }

    private sealed class Context
    {
        private readonly HashSet<string> visiting = new(StringComparer.Ordinal);

        public Context(int maxTransitiveDepth, bool followIdentifiers)
        {
            this.RemainingDepth = maxTransitiveDepth;
            this.FollowIdentifiers = followIdentifiers;
        }

        public int RemainingDepth { get; private set; }

        public bool FollowIdentifiers { get; }

        public bool UsedTransitiveLocal { get; private set; }

        public bool TryEnter(string name)
        {
            if (this.RemainingDepth <= 0 || !this.visiting.Add(name))
            {
                return false;
            }

            this.RemainingDepth--;
            this.UsedTransitiveLocal = true;
            return true;
        }

        public void Exit(string name)
        {
            this.visiting.Remove(name);
            this.RemainingDepth++;
        }
    }

    private static Family Evaluate(ExpressionSyntax? expression, Context context)
    {
        switch (expression)
        {
            case null:
                return Family.NotReproducible;

            case LiteralExpressionSyntax:
                return Family.Literal;

            case InterpolatedStringExpressionSyntax interpolated:
                foreach (var content in interpolated.Contents)
                {
                    if (content is InterpolationSyntax interpolation
                        && Evaluate(interpolation.Expression, context) == Family.NotReproducible)
                    {
                        return Family.NotReproducible;
                    }
                }

                return Family.Literal;

            case ParenthesizedExpressionSyntax parenthesized:
                return Evaluate(parenthesized.Expression, context);

            case PrefixUnaryExpressionSyntax prefix when IsReproduciblePrefix(prefix):
                return Downgrade(Evaluate(prefix.Operand, context), Family.Literal);

            case CastExpressionSyntax cast:
                return Downgrade(Evaluate(cast.Expression, context), Family.Literal);

            case BinaryExpressionSyntax binary when IsReproducibleBinary(binary):
                {
                    var left = Evaluate(binary.Left, context);
                    var right = Evaluate(binary.Right, context);
                    return left == Family.NotReproducible || right == Family.NotReproducible
                        ? Family.NotReproducible
                        : Family.Literal;
                }

            case ConditionalExpressionSyntax conditional:
                {
                    var condition = Evaluate(conditional.Condition, context);
                    var whenTrue = Evaluate(conditional.WhenTrue, context);
                    var whenFalse = Evaluate(conditional.WhenFalse, context);
                    return condition == Family.NotReproducible
                        || whenTrue == Family.NotReproducible
                        || whenFalse == Family.NotReproducible
                        ? Family.NotReproducible
                        : Family.Literal;
                }

            case DefaultExpressionSyntax:
            case TypeOfExpressionSyntax:
                return Family.Literal;

            case ImplicitArrayCreationExpressionSyntax implicitArray:
                return EvaluateInitializer(implicitArray.Initializer, context);

            case ArrayCreationExpressionSyntax array:
                return array.Initializer is null ? Family.Constructible : EvaluateInitializer(array.Initializer, context);

            case InitializerExpressionSyntax initializerExpression:
                return EvaluateInitializer(initializerExpression, context);

            case ObjectCreationExpressionSyntax objectCreation:
                return EvaluateConstruction(objectCreation.ArgumentList, objectCreation.Initializer, context);

            case ImplicitObjectCreationExpressionSyntax implicitCreation:
                return EvaluateConstruction(implicitCreation.ArgumentList, implicitCreation.Initializer, context);

            case TupleExpressionSyntax tuple:
                {
                    foreach (var argument in tuple.Arguments)
                    {
                        if (Evaluate(argument.Expression, context) == Family.NotReproducible)
                        {
                            return Family.NotReproducible;
                        }
                    }

                    return Family.Constructible;
                }

            case MemberAccessExpressionSyntax memberAccess:
                return EvaluateMemberAccess(memberAccess, context);

            case InvocationExpressionSyntax invocation:
                return EvaluateInvocation(invocation, context);

            case IdentifierNameSyntax identifier:
                return EvaluateIdentifier(identifier, context);

            case PredefinedTypeSyntax:
                // Reached only as the receiver of something like `string.Empty`, which the caller validates.
                return Family.Literal;

            default:
                // Collection expressions ([1, 2, 3]) only exist in newer Roslyn; match them by kind so the
                // library still compiles against an older Microsoft.CodeAnalysis.
                return expression.IsKind(SyntaxKind.CollectionExpression)
                    ? EvaluateCollectionExpression(expression, context)
                    : Family.NotReproducible;
        }
    }

    private static Family EvaluateCollectionExpression(ExpressionSyntax expression, Context context)
    {
        foreach (var child in expression.ChildNodes())
        {
            foreach (var element in child.ChildNodes())
            {
                if (element is ExpressionSyntax elementExpression
                    && Evaluate(elementExpression, context) == Family.NotReproducible)
                {
                    return Family.NotReproducible;
                }
            }
        }

        return Family.Constructible;
    }

    private static Family EvaluateInitializer(InitializerExpressionSyntax? initializer, Context context)
    {
        if (initializer is null)
        {
            return Family.Constructible;
        }

        foreach (var expression in initializer.Expressions)
        {
            // Object-initializer members are assignments; only the value side needs to be reproducible.
            var value = expression is AssignmentExpressionSyntax assignment ? assignment.Right : expression;
            if (Evaluate(value, context) == Family.NotReproducible)
            {
                return Family.NotReproducible;
            }
        }

        return Family.Constructible;
    }

    private static Family EvaluateConstruction(
        ArgumentListSyntax? arguments,
        InitializerExpressionSyntax? initializer,
        Context context)
    {
        if (arguments is not null)
        {
            foreach (var argument in arguments.Arguments)
            {
                if (Evaluate(argument.Expression, context) == Family.NotReproducible)
                {
                    return Family.NotReproducible;
                }
            }
        }

        return EvaluateInitializer(initializer, context);
    }

    private static Family EvaluateMemberAccess(MemberAccessExpressionSyntax memberAccess, Context context)
    {
        if (WellKnownStatics.Contains(RenderMemberPath(memberAccess)))
        {
            return Family.WellKnownStatic;
        }

        var memberName = memberAccess.Name.Identifier.ValueText;
        if (WellKnownContinuations.Contains(memberName)
            && Evaluate(memberAccess.Expression, context) != Family.NotReproducible)
        {
            return Family.WellKnownStatic;
        }

        // Enum member access: an uppercase receiver that is declared nowhere in the file is a type name, and
        // an uppercase member on a type name is a constant worth reproducing.
        if (memberAccess.Expression is IdentifierNameSyntax receiver
            && StartsUpper(receiver.Identifier.ValueText)
            && StartsUpper(memberName)
            && (!context.FollowIdentifiers || DeclarationLookup.Find(memberAccess, receiver.Identifier.ValueText) is null))
        {
            return Family.WellKnownStatic;
        }

        return Family.NotReproducible;
    }

    private static Family EvaluateInvocation(InvocationExpressionSyntax invocation, Context context)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return Family.NotReproducible;
        }

        var allowed = WellKnownStatics.Contains(RenderMemberPath(memberAccess));
        if (!allowed
            && WellKnownContinuations.Contains(memberAccess.Name.Identifier.ValueText)
            && Evaluate(memberAccess.Expression, context) != Family.NotReproducible)
        {
            allowed = true;
        }

        if (!allowed)
        {
            return Family.NotReproducible;
        }

        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            if (Evaluate(argument.Expression, context) == Family.NotReproducible)
            {
                return Family.NotReproducible;
            }
        }

        return Family.WellKnownStatic;
    }

    private static Family EvaluateIdentifier(IdentifierNameSyntax identifier, Context context)
    {
        if (!context.FollowIdentifiers)
        {
            return Family.NotReproducible;
        }

        var name = identifier.Identifier.ValueText;

        var declaration = DeclarationLookup.Find(identifier, name);
        if (declaration?.Initializer is null)
        {
            return Family.NotReproducible;
        }

        if (declaration.Kind is not (FreeVariableKind.ReproducibleLocal
            or FreeVariableKind.OpaqueLocal
            or FreeVariableKind.Constant
            or FreeVariableKind.Field
            or FreeVariableKind.Property))
        {
            return Family.NotReproducible;
        }

        if (!context.TryEnter(name))
        {
            return Family.NotReproducible;
        }

        try
        {
            return Evaluate(declaration.Initializer, context);
        }
        finally
        {
            context.Exit(name);
        }
    }

    private static Family Downgrade(Family family, Family ceiling)
        => family == Family.NotReproducible ? Family.NotReproducible : ceiling;

    private static bool IsReproduciblePrefix(PrefixUnaryExpressionSyntax prefix)
        => prefix.OperatorToken.IsKind(SyntaxKind.MinusToken)
            || prefix.OperatorToken.IsKind(SyntaxKind.PlusToken)
            || prefix.OperatorToken.IsKind(SyntaxKind.ExclamationToken)
            || prefix.OperatorToken.IsKind(SyntaxKind.TildeToken);

    private static bool IsReproducibleBinary(BinaryExpressionSyntax binary)
    {
        switch (binary.Kind())
        {
            case SyntaxKind.AddExpression:
            case SyntaxKind.SubtractExpression:
            case SyntaxKind.MultiplyExpression:
            case SyntaxKind.DivideExpression:
            case SyntaxKind.ModuloExpression:
            case SyntaxKind.BitwiseAndExpression:
            case SyntaxKind.BitwiseOrExpression:
            case SyntaxKind.ExclusiveOrExpression:
            case SyntaxKind.LeftShiftExpression:
            case SyntaxKind.RightShiftExpression:
            case SyntaxKind.LogicalAndExpression:
            case SyntaxKind.LogicalOrExpression:
            case SyntaxKind.EqualsExpression:
            case SyntaxKind.NotEqualsExpression:
            case SyntaxKind.LessThanExpression:
            case SyntaxKind.LessThanOrEqualExpression:
            case SyntaxKind.GreaterThanExpression:
            case SyntaxKind.GreaterThanOrEqualExpression:
            case SyntaxKind.CoalesceExpression:
                return true;
            default:
                return false;
        }
    }

    private static string RenderMemberPath(MemberAccessExpressionSyntax memberAccess)
    {
        var receiver = memberAccess.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            PredefinedTypeSyntax predefined => predefined.Keyword.ValueText,
            MemberAccessExpressionSyntax nested => nested.ToString(),
            _ => null,
        };

        return receiver is null ? string.Empty : receiver + "." + memberAccess.Name.Identifier.ValueText;
    }

    private static bool StartsUpper(string value)
        => value.Length > 0 && char.IsUpper(value[0]);
}
