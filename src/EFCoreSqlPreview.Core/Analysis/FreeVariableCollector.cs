using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace EFCoreSqlPreview.Core.Analysis;

/// <summary>
/// Finds identifiers the query references but does not declare, and works out a value for each.
/// </summary>
/// <remarks>
/// The exclusion set matters more than the inclusion set. Type names on the left of a dot (<c>Kind.A</c>) look
/// exactly like locals syntactically, so an uppercase identifier that resolves to no declaration in the file is
/// skipped and reported as <see cref="AnalysisDiagnosticIds.SkippedProbableTypeName"/>.
/// </remarks>
public static class FreeVariableCollector
{
    /// <summary>Collects the free variables of a query and its replayed prologue.</summary>
    /// <param name="chain">The decomposed chain.</param>
    /// <param name="prologue">Statements that will be replayed before the query.</param>
    /// <param name="root">The syntax root of the document, used for declaration lookup.</param>
    /// <param name="options">Analysis options; user overrides win over inferred values.</param>
    /// <param name="diagnostics">Collector for EFSP0040-EFSP0043 diagnostics.</param>
    /// <returns>One entry per distinct name, in first-occurrence order.</returns>
    public static IReadOnlyList<FreeVariable> Collect(
        QueryChain chain,
        IEnumerable<StatementSyntax> prologue,
        SyntaxNode root,
        AnalyzerOptions options,
        ICollection<AnalysisDiagnostic> diagnostics)
        => Collect(chain, prologue, root, options, diagnostics, contextRootIdentifier: null);

    /// <summary>Collects the free variables of a query and its replayed prologue, excluding the context root.</summary>
    /// <param name="chain">The decomposed chain.</param>
    /// <param name="prologue">Statements that will be replayed before the query.</param>
    /// <param name="root">The syntax root of the document, used for declaration lookup.</param>
    /// <param name="options">Analysis options; user overrides win over inferred values.</param>
    /// <param name="diagnostics">Collector for EFSP0040-EFSP0043 diagnostics.</param>
    /// <param name="contextRootIdentifier">
    /// The DbContext identifier, which the worker supplies itself. Needed separately from the chain head
    /// because a query built through a local (<c>q = q.Where(...)</c>) mentions the context only in the prologue.
    /// </param>
    /// <returns>One entry per distinct name, in first-occurrence order.</returns>
    public static IReadOnlyList<FreeVariable> Collect(
        QueryChain chain,
        IEnumerable<StatementSyntax> prologue,
        SyntaxNode root,
        AnalyzerOptions options,
        ICollection<AnalysisDiagnostic> diagnostics,
        string? contextRootIdentifier)
    {
        if (chain is null)
        {
            return Array.Empty<FreeVariable>();
        }

        options ??= AnalyzerOptions.Default;
        diagnostics ??= new List<AnalysisDiagnostic>();

        var prologueStatements = prologue is null
            ? new List<StatementSyntax>()
            : prologue.Where(s => s is not null).ToList();

        // Anything the replayed prologue declares is not free: the worker gets it back verbatim.
        var declaredByPrologue = new HashSet<string>(StringComparer.Ordinal);
        foreach (var statement in prologueStatements)
        {
            foreach (var written in PrologueSlicer.Writes(statement))
            {
                declaredByPrologue.Add(written);
            }
        }

        var scopes = new List<SyntaxNode>(prologueStatements.Count + 1);
        scopes.AddRange(prologueStatements);
        scopes.Add(chain.Root);

        var candidates = new List<Candidate>();
        var byName = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var skippedTypeNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var scope in scopes)
        {
            var bound = CollectBoundNames(scope);

            foreach (var identifier in scope.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                var name = identifier.Identifier.ValueText;
                if (name.Length == 0
                    || name == "var"
                    || bound.Contains(name)
                    || declaredByPrologue.Contains(name)
                    || string.Equals(name, contextRootIdentifier, StringComparison.Ordinal)
                    || IsExcluded(identifier, chain))
                {
                    continue;
                }

                if (byName.TryGetValue(name, out var existing))
                {
                    existing.Usages.Add(identifier.Span);
                    continue;
                }

                var declaration = DeclarationLookup.Find(identifier, name);

                // E14: an uppercase identifier on the left of a dot that is declared nowhere in the file is a
                // type name, not a variable. Without this, enum types leak in as phantom free variables.
                if (declaration is null && IsProbableTypeName(identifier, name))
                {
                    if (skippedTypeNames.Add(name))
                    {
                        diagnostics.Add(AnalysisDiagnostic.Info(
                            AnalysisDiagnosticIds.SkippedProbableTypeName,
                            $"'{name}' was treated as a type name, not a variable, because nothing in this file declares it.",
                            identifier.Span));
                    }

                    continue;
                }

                var candidate = new Candidate(name, identifier, declaration);
                candidate.Usages.Add(identifier.Span);
                candidates.Add(candidate);
                byName[name] = candidate;
            }
        }

        // A reproducible initializer may itself mention other locals; those must be declared in the worker too,
        // and before the variable that uses them.
        ExpandDependencies(candidates, byName, options);

        var results = new List<FreeVariable>(candidates.Count);
        foreach (var candidate in candidates)
        {
            results.Add(Describe(candidate, options, diagnostics));
        }

        return results;
    }

    /// <summary>
    /// Collects every name bound within a scope: lambda parameters, query-expression range variables and
    /// pattern designations. These are never free variables.
    /// </summary>
    /// <param name="scope">The node to search.</param>
    /// <returns>The set of bound names.</returns>
    public static ISet<string> CollectBoundNames(SyntaxNode scope)
    {
        var bound = new HashSet<string>(StringComparer.Ordinal);
        if (scope is null)
        {
            return bound;
        }

        foreach (var node in scope.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case SimpleLambdaExpressionSyntax lambda:
                    bound.Add(lambda.Parameter.Identifier.ValueText);
                    break;

                case ParenthesizedLambdaExpressionSyntax lambda:
                    AddParameters(lambda.ParameterList, bound);
                    break;

                case AnonymousMethodExpressionSyntax anonymous:
                    AddParameters(anonymous.ParameterList, bound);
                    break;

                case FromClauseSyntax from:
                    bound.Add(from.Identifier.ValueText);
                    break;

                case LetClauseSyntax let:
                    bound.Add(let.Identifier.ValueText);
                    break;

                case JoinClauseSyntax join:
                    bound.Add(join.Identifier.ValueText);
                    break;

                case JoinIntoClauseSyntax joinInto:
                    bound.Add(joinInto.Identifier.ValueText);
                    break;

                case QueryContinuationSyntax continuation:
                    bound.Add(continuation.Identifier.ValueText);
                    break;

                case SingleVariableDesignationSyntax designation:
                    bound.Add(designation.Identifier.ValueText);
                    break;
            }
        }

        return bound;
    }

    private sealed class Candidate
    {
        public Candidate(string name, SyntaxNode usage, DeclarationLookupResult? declaration)
        {
            this.Name = name;
            this.Usage = usage;
            this.Declaration = declaration;
        }

        public string Name { get; }

        public SyntaxNode Usage { get; }

        public DeclarationLookupResult? Declaration { get; }

        public List<TextSpan> Usages { get; } = new();
    }

    private static void AddParameters(ParameterListSyntax? parameters, ISet<string> bound)
    {
        if (parameters is null)
        {
            return;
        }

        foreach (var parameter in parameters.Parameters)
        {
            bound.Add(parameter.Identifier.ValueText);
        }
    }

    private static void ExpandDependencies(
        List<Candidate> candidates,
        Dictionary<string, Candidate> byName,
        AnalyzerOptions options)
    {
        var index = 0;
        while (index < candidates.Count)
        {
            var candidate = candidates[index];
            var initializer = candidate.Declaration?.Initializer;
            if (initializer is null
                || InitializerReproducibility.Classify(initializer, options.MaxTransitiveInitializerDepth)
                    == ValueSource.SynthesizedDefault)
            {
                index++;
                continue;
            }

            var bound = CollectBoundNames(initializer);
            var startIndex = index;

            foreach (var identifier in initializer.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                var name = identifier.Identifier.ValueText;
                if (name.Length == 0
                    || name == "var"
                    || bound.Contains(name)
                    || byName.ContainsKey(name)
                    || IsExcludedFromInitializer(identifier))
                {
                    continue;
                }

                var declaration = DeclarationLookup.Find(identifier, name);
                if (declaration is null)
                {
                    continue;
                }

                var dependency = new Candidate(name, identifier, declaration);
                dependency.Usages.Add(identifier.Span);
                byName[name] = dependency;

                // Insert before the dependent so the emitted declarations compile in order.
                candidates.Insert(index, dependency);
                index++;
            }

            if (index != startIndex)
            {
                // Re-run from the first dependency just inserted, so its own dependencies are expanded too.
                index = startIndex;
                continue;
            }

            index++;
        }
    }

    private static FreeVariable Describe(
        Candidate candidate,
        AnalyzerOptions options,
        ICollection<AnalysisDiagnostic> diagnostics)
    {
        var declaration = candidate.Declaration;
        var declarationSpan = declaration?.Span ?? default;
        var initializer = declaration?.Initializer;
        var initializerText = initializer?.ToString();
        var usages = candidate.Usages.ToArray();
        var kind = declaration?.Kind ?? FreeVariableKind.Unresolved;
        var declaredType = declaration?.TypeText;

        if (options.FreeVariableOverrides is not null
            && options.FreeVariableOverrides.TryGetValue(candidate.Name, out var supplied)
            && !string.IsNullOrWhiteSpace(supplied))
        {
            return new FreeVariable(
                candidate.Name,
                kind,
                declaredType,
                initializerText,
                ValueSource.UserSupplied,
                supplied,
                BuildDeclaration(declaredType, candidate.Name, supplied),
                RequiresUserValue: false,
                declarationSpan,
                usages);
        }

        // Parameters carry no initializer at all, so their value is always a guess no matter how well typed.
        var isParameter = kind is FreeVariableKind.Parameter or FreeVariableKind.PrimaryConstructorParameter;

        var valueSource = isParameter || initializer is null
            ? ValueSource.SynthesizedDefault
            : InitializerReproducibility.Classify(initializer, options.MaxTransitiveInitializerDepth);

        if (valueSource != ValueSource.SynthesizedDefault)
        {
            return new FreeVariable(
                candidate.Name,
                kind,
                declaredType,
                initializerText,
                valueSource,
                initializerText!,
                BuildDeclaration(declaredType, candidate.Name, initializerText!),
                RequiresUserValue: false,
                declarationSpan,
                usages);
        }

        // `var` only carries type information when the initializer can be reproduced; here it cannot, so the
        // declared type is genuinely unknown and the user must supply one.
        var knownType = declaredType is null || declaredType == "var" ? null : declaredType;

        var recognised = DefaultValueSynthesizer.TryFor(knownType, out var value);

        diagnostics.Add(AnalysisDiagnostic.Warning(
            AnalysisDiagnosticIds.FreeVariableNeedsValue,
            $"'{candidate.Name}' has no value the analyzer can reproduce; the preview uses {value} until you supply one.",
            usages.Length > 0 ? usages[0] : declarationSpan));

        if (knownType is null)
        {
            diagnostics.Add(AnalysisDiagnostic.Warning(
                AnalysisDiagnosticIds.FreeVariableTypeUnknown,
                $"The type of '{candidate.Name}' could not be determined from this file; supply a type and a value.",
                usages.Length > 0 ? usages[0] : declarationSpan));
        }
        else if (!recognised && DefaultValueSynthesizer.LooksLikeEnum(knownType))
        {
            diagnostics.Add(AnalysisDiagnostic.Info(
                AnalysisDiagnosticIds.EnumDefaultGuess,
                $"'{candidate.Name}' looks like an enum; the preview uses its default member.",
                usages.Length > 0 ? usages[0] : declarationSpan));
        }

        return new FreeVariable(
            candidate.Name,
            kind,
            knownType,
            initializerText,
            ValueSource.SynthesizedDefault,
            value,
            knownType is null ? null : BuildDeclaration(knownType, candidate.Name, value),
            RequiresUserValue: true,
            declarationSpan,
            usages);
    }

    private static string? BuildDeclaration(string? declaredType, string name, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var type = string.IsNullOrEmpty(declaredType) ? "var" : declaredType!;
        return type + " " + name + " = " + value + ";";
    }

    private static bool IsExcluded(IdentifierNameSyntax identifier, QueryChain chain)
    {
        // E12: the context root is supplied by the worker, not declared as a free variable.
        if (chain.Head.Span.Contains(identifier.Span))
        {
            return true;
        }

        return IsExcludedFromInitializer(identifier);
    }

    private static bool IsExcludedFromInitializer(IdentifierNameSyntax identifier)
    {
        var parent = identifier.Parent;

        switch (parent)
        {
            // E3: the right-hand side of a dot is a member name.
            case MemberAccessExpressionSyntax memberAccess when memberAccess.Name == identifier:
            // E4: '?.Foo'.
            case MemberBindingExpressionSyntax:
            // E5: the type of 'new X()'.
            case ObjectCreationExpressionSyntax creation when creation.Type == identifier:
            // E7: a cast target type.
            case CastExpressionSyntax cast when cast.Type == identifier:
            // E9: a segment of a qualified name.
            case QualifiedNameSyntax:
            case AliasQualifiedNameSyntax:
            // E10: a named argument or an initializer member name.
            case NameColonSyntax:
            case NameEqualsSyntax:
            // E11: a bare method-call target.
            case InvocationExpressionSyntax invocation when invocation.Expression == identifier:
            // Type positions of every other shape.
            case TypeOfExpressionSyntax:
            case DefaultExpressionSyntax:
            case ArrayTypeSyntax:
            case NullableTypeSyntax:
            case DeclarationPatternSyntax:
            case TypePatternSyntax:
            case DeclarationExpressionSyntax:
            case BinaryPatternSyntax:
            case ParameterSyntax:
            case VariableDeclarationSyntax:
                return true;
        }

        // E6: anywhere inside a type-argument list.
        if (identifier.FirstAncestorOrSelf<TypeArgumentListSyntax>() is not null)
        {
            return true;
        }

        // E10 (initializer form): 'Id = p.Id' inside an object initializer assigns a member, not a variable.
        if (parent is AssignmentExpressionSyntax assignment
            && assignment.Left == identifier
            && assignment.Parent is InitializerExpressionSyntax)
        {
            return true;
        }

        return false;
    }

    private static bool IsProbableTypeName(IdentifierNameSyntax identifier, string name)
        => identifier.Parent is MemberAccessExpressionSyntax memberAccess
            && memberAccess.Expression == identifier
            && char.IsUpper(name[0]);
}
