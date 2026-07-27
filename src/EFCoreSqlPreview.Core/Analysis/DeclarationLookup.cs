using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace EFCoreSqlPreview.Core.Analysis;

/// <summary>
/// Where an identifier's declaration was found, and what it says.
/// </summary>
/// <param name="Kind">The declaration form.</param>
/// <param name="TypeText">The declared type text, e.g. <c>decimal</c> or <c>var</c>.</param>
/// <param name="Initializer">The initializer expression when the declaration has one.</param>
/// <param name="Span">Span of the declaration in the user's document.</param>
/// <param name="IsConst">Whether the declaration is <c>const</c>, which makes it unconditionally reproducible.</param>
public sealed record DeclarationLookupResult(
    FreeVariableKind Kind,
    string? TypeText,
    ExpressionSyntax? Initializer,
    TextSpan Span,
    bool IsConst);

/// <summary>
/// Resolves an identifier to its declaration using syntax alone, searching only the current file.
/// </summary>
/// <remarks>
/// Search order, innermost first: locals declared before the use site, pattern and designation locals,
/// enclosing lambda parameters, method/local-function/constructor/accessor parameters, primary-constructor
/// parameters, fields, then properties. Anything living in a base class or another partial is unreachable
/// and reported as unresolved.
/// </remarks>
public static class DeclarationLookup
{
    /// <summary>Finds the declaration of a name as seen from a node.</summary>
    /// <param name="fromNode">The node the identifier is used at; determines the scopes to search.</param>
    /// <param name="name">The identifier text.</param>
    /// <returns>The declaration, or <see langword="null"/> when it is not declared in this file.</returns>
    public static DeclarationLookupResult? Find(SyntaxNode fromNode, string name)
    {
        if (fromNode is null || string.IsNullOrEmpty(name))
        {
            return null;
        }

        var position = fromNode.SpanStart;

        foreach (var ancestor in fromNode.AncestorsAndSelf())
        {
            var found = FindInScope(ancestor, name, position);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static DeclarationLookupResult? FindInScope(SyntaxNode scope, string name, int position)
    {
        switch (scope)
        {
            case BlockSyntax block:
                return FindInStatements(block.Statements, name, position);

            case SwitchSectionSyntax section:
                return FindInStatements(section.Statements, name, position);

            case CompilationUnitSyntax unit:
                return FindInStatements(
                    unit.Members.OfType<GlobalStatementSyntax>().Select(g => g.Statement).ToList(),
                    name,
                    position);

            case ForEachStatementSyntax forEach when forEach.Identifier.ValueText == name:
                return new DeclarationLookupResult(
                    FreeVariableKind.OpaqueLocal, forEach.Type.ToString(), null, forEach.Identifier.Span, IsConst: false);

            case CatchClauseSyntax catchClause when catchClause.Declaration?.Identifier.ValueText == name:
                return new DeclarationLookupResult(
                    FreeVariableKind.OpaqueLocal,
                    catchClause.Declaration.Type.ToString(),
                    null,
                    catchClause.Declaration.Span,
                    IsConst: false);

            case ForStatementSyntax forStatement when forStatement.Declaration is not null:
                return FindInVariableDeclaration(forStatement.Declaration, name, isConst: false, FreeVariableKind.OpaqueLocal);

            case UsingStatementSyntax usingStatement when usingStatement.Declaration is not null:
                return FindInVariableDeclaration(usingStatement.Declaration, name, isConst: false, FreeVariableKind.OpaqueLocal);

            case SimpleLambdaExpressionSyntax lambda when lambda.Parameter.Identifier.ValueText == name:
                return FromParameter(lambda.Parameter, FreeVariableKind.Parameter);

            case ParenthesizedLambdaExpressionSyntax lambda:
                return FindInParameters(lambda.ParameterList, name, FreeVariableKind.Parameter);

            case AnonymousMethodExpressionSyntax anonymous when anonymous.ParameterList is not null:
                return FindInParameters(anonymous.ParameterList, name, FreeVariableKind.Parameter);

            case MethodDeclarationSyntax method:
                return FindInParameters(method.ParameterList, name, FreeVariableKind.Parameter);

            case LocalFunctionStatementSyntax localFunction:
                return FindInParameters(localFunction.ParameterList, name, FreeVariableKind.Parameter);

            case ConstructorDeclarationSyntax constructor:
                return FindInParameters(constructor.ParameterList, name, FreeVariableKind.Parameter);

            case OperatorDeclarationSyntax op:
                return FindInParameters(op.ParameterList, name, FreeVariableKind.Parameter);

            case AccessorDeclarationSyntax when name == "value":
                // The implicit setter parameter: real, but with no syntactically knowable type.
                return new DeclarationLookupResult(FreeVariableKind.Parameter, null, null, default, IsConst: false);

            case TypeDeclarationSyntax type:
                return FindInTypeDeclaration(type, name);

            default:
                return null;
        }
    }

    private static DeclarationLookupResult? FindInStatements(
        IReadOnlyList<StatementSyntax> statements,
        string name,
        int position)
    {
        // Declaration-before-use: C# forbids referring to a local declared later in the same block, so anything
        // starting at or after the use site is not a candidate. An enclosing statement (an `if` holding a
        // pattern designation) starts before the use site and is therefore included.
        DeclarationLookupResult? best = null;

        foreach (var statement in statements)
        {
            if (statement.SpanStart >= position)
            {
                break;
            }

            var found = FindInStatement(statement, name, position);
            if (found is not null)
            {
                best = found;
            }
        }

        return best;
    }

    private static DeclarationLookupResult? FindInStatement(StatementSyntax statement, string name, int position)
    {
        if (statement is LocalDeclarationStatementSyntax local)
        {
            var isConst = local.Modifiers.Any(SyntaxKind.ConstKeyword);
            var found = FindInVariableDeclaration(
                local.Declaration,
                name,
                isConst,
                isConst ? FreeVariableKind.Constant : FreeVariableKind.ReproducibleLocal);
            if (found is not null)
            {
                return found;
            }
        }

        if (statement is ForEachStatementSyntax forEach && forEach.Identifier.ValueText == name)
        {
            return new DeclarationLookupResult(
                FreeVariableKind.OpaqueLocal, forEach.Type.ToString(), null, forEach.Identifier.Span, IsConst: false);
        }

        // Pattern and out-variable designations can introduce a local anywhere inside a statement.
        foreach (var node in statement.DescendantNodes())
        {
            if (node.SpanStart >= position)
            {
                // DescendantNodes yields in source order, so nothing further can precede the use site.
                break;
            }

            if (node is SingleVariableDesignationSyntax designation && designation.Identifier.ValueText == name)
            {
                return new DeclarationLookupResult(
                    FreeVariableKind.OpaqueLocal,
                    TypeTextOfDesignation(designation),
                    null,
                    designation.Span,
                    IsConst: false);
            }
        }

        return null;
    }

    private static string? TypeTextOfDesignation(SingleVariableDesignationSyntax designation)
        => designation.Parent switch
        {
            DeclarationPatternSyntax pattern => pattern.Type.ToString(),
            DeclarationExpressionSyntax declaration => declaration.Type.ToString(),
            _ => null,
        };

    private static DeclarationLookupResult? FindInVariableDeclaration(
        VariableDeclarationSyntax declaration,
        string name,
        bool isConst,
        FreeVariableKind kind)
    {
        foreach (var declarator in declaration.Variables)
        {
            if (declarator.Identifier.ValueText != name)
            {
                continue;
            }

            var initializer = declarator.Initializer?.Value;
            var resolvedKind = kind;
            if (kind == FreeVariableKind.ReproducibleLocal
                && InitializerReproducibility.ClassifyShallow(initializer) == ValueSource.SynthesizedDefault)
            {
                // Shallow on purpose: the deep classifier resolves identifiers through this very lookup.
                // FreeVariableCollector re-classifies with the transitive rules once the name is known to matter.
                resolvedKind = FreeVariableKind.OpaqueLocal;
            }

            return new DeclarationLookupResult(
                resolvedKind,
                declaration.Type.ToString(),
                initializer,
                declarator.Span,
                isConst);
        }

        return null;
    }

    private static DeclarationLookupResult? FindInParameters(
        ParameterListSyntax? parameterList,
        string name,
        FreeVariableKind kind)
    {
        if (parameterList is null)
        {
            return null;
        }

        foreach (var parameter in parameterList.Parameters)
        {
            if (parameter.Identifier.ValueText == name)
            {
                return FromParameter(parameter, kind);
            }
        }

        return null;
    }

    private static DeclarationLookupResult FromParameter(ParameterSyntax parameter, FreeVariableKind kind)
        => new(kind, parameter.Type?.ToString(), parameter.Default?.Value, parameter.Span, IsConst: false);

    private static DeclarationLookupResult? FindInTypeDeclaration(TypeDeclarationSyntax type, string name)
    {
        var fromPrimaryConstructor = FindInParameters(
            type.ParameterList, name, FreeVariableKind.PrimaryConstructorParameter);
        if (fromPrimaryConstructor is not null)
        {
            return fromPrimaryConstructor;
        }

        foreach (var member in type.Members)
        {
            if (member is FieldDeclarationSyntax field)
            {
                var isConst = field.Modifiers.Any(SyntaxKind.ConstKeyword);
                var found = FindInVariableDeclaration(
                    field.Declaration,
                    name,
                    isConst,
                    isConst ? FreeVariableKind.Constant : FreeVariableKind.Field);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        foreach (var member in type.Members)
        {
            if (member is PropertyDeclarationSyntax property && property.Identifier.ValueText == name)
            {
                return new DeclarationLookupResult(
                    FreeVariableKind.Property,
                    property.Type.ToString(),
                    property.Initializer?.Value,
                    property.Span,
                    IsConst: false);
            }
        }

        return null;
    }
}
