using EFCoreSqlPreview.Core.Analysis;

namespace EFCoreSqlPreview.Core.Generation;

/// <summary>
/// One free variable rendered as a C# declaration the worker can compile.
/// </summary>
/// <param name="Name">The variable's name, unchanged from the user's source.</param>
/// <param name="Declaration">The complete statement, e.g. <c>decimal minPrice = 100m;</c>.</param>
/// <param name="Warning">Something the user should know about this value, or <see langword="null"/>.</param>
public sealed record SynthesizedDeclaration(
    string Name,
    string Declaration,
    string? Warning);

/// <summary>
/// Turns an analyzed free variable into the declaration statement the generated worker emits for it.
/// </summary>
/// <remarks>
/// These declarations go inside the same lambda as the query, which is what makes EF name the SQL parameters
/// after the user's own identifiers (<c>@search_contains</c>) instead of after a hoisted closure field
/// (<c>@_8__locals1_search_contains</c>).
/// </remarks>
public static class ValueSynthesizer
{
    /// <summary>Type texts that carry no information and cannot be used in a declaration.</summary>
    private static readonly HashSet<string> UninformativeTypes = new(StringComparer.Ordinal)
    {
        "var",
        "dynamic",
        "",
    };

    /// <summary>Value expressions that are not self-typing, so <c>var</c> cannot infer from them.</summary>
    private static readonly HashSet<string> NonInferrableValues = new(StringComparer.Ordinal)
    {
        "null",
        "default",
        "default!",
        "default(var)",
    };

    /// <summary>
    /// Renders a declaration for one free variable.
    /// </summary>
    /// <param name="variable">The analyzed variable.</param>
    /// <param name="overrideValue">
    /// A value the user typed into the free-variables panel. It wins over the analyzer's suggestion, because
    /// the SQL genuinely depends on the value: an empty collection collapses <c>Contains</c> to <c>WHERE 0 = 1</c>.
    /// </param>
    /// <returns>The declaration, plus a warning when the value had to be guessed or the type coerced.</returns>
    public static SynthesizedDeclaration Synthesize(FreeVariable variable, string? overrideValue = null)
    {
        if (variable is null)
        {
            throw new ArgumentNullException(nameof(variable));
        }

        var value = Clean(overrideValue) ?? Clean(variable.SuggestedValueExpression) ?? "default!";
        var declaredType = Normalize(variable.DeclaredTypeName);
        string? warning = null;

        string typeText;
        if (declaredType is not null)
        {
            typeText = declaredType;
        }
        else if (NonInferrableValues.Contains(value))
        {
            // `var x = null;` and `var x = default;` do not compile, so the variable has to be typed as object
            // and the query is likely to fail to bind. Saying so is better than emitting a broken program.
            typeText = "object";
            warning = $"The type of '{variable.Name}' could not be determined, so it is declared as object with the value {value}. "
                + "Supply a type and value in the free-variables panel if the query does not compile.";
        }
        else
        {
            typeText = "var";
        }

        if (warning is null && overrideValue is null && variable.RequiresUserValue)
        {
            warning = $"'{variable.Name}' has no reproducible value in the source, so {value} was substituted. "
                + "The generated SQL can depend on this value, so set it in the free-variables panel to be sure.";
        }

        return new SynthesizedDeclaration(variable.Name, $"{typeText} {variable.Name} = {value};", warning);
    }

    /// <summary>
    /// Renders declarations for a whole set of free variables, applying the user's overrides and skipping
    /// names the replayed statements already declare.
    /// </summary>
    /// <param name="variables">The analyzed free variables, in the order they should be declared.</param>
    /// <param name="overrides">User-supplied values keyed by variable name.</param>
    /// <param name="skipNames">Names already declared by the replayed statements; declaring them twice is CS0128.</param>
    /// <returns>The declarations to emit, in order.</returns>
    public static IReadOnlyList<SynthesizedDeclaration> SynthesizeAll(
        IEnumerable<FreeVariable> variables,
        IReadOnlyDictionary<string, string>? overrides,
        ISet<string>? skipNames)
    {
        if (variables is null)
        {
            throw new ArgumentNullException(nameof(variables));
        }

        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<SynthesizedDeclaration>();

        foreach (var variable in variables)
        {
            if (string.IsNullOrEmpty(variable.Name)
                || (skipNames is not null && skipNames.Contains(variable.Name))
                || !emitted.Add(variable.Name))
            {
                continue;
            }

            string? overrideValue = null;
            overrides?.TryGetValue(variable.Name, out overrideValue);
            results.Add(Synthesize(variable, overrideValue));
        }

        return results;
    }

    /// <summary>
    /// Reports whether a name would collide with an identifier the generated program already owns.
    /// </summary>
    /// <param name="name">A free-variable name.</param>
    /// <returns><see langword="true"/> when the generated program cannot declare this name.</returns>
    /// <remarks>
    /// Top-level statements implicitly declare <c>args</c>, and redeclaring it inside the nested body lambda
    /// is CS0136 rather than a shadow.
    /// </remarks>
    public static bool IsReservedName(string? name)
        => string.Equals(name, "args", StringComparison.Ordinal)
        || string.Equals(name, WorkerIdentifiers.Context, StringComparison.Ordinal)
        || string.Equals(name, WorkerIdentifiers.Interceptor, StringComparison.Ordinal)
        || string.Equals(name, WorkerIdentifiers.Result, StringComparison.Ordinal)
        || string.Equals(name, WorkerIdentifiers.Commands, StringComparison.Ordinal);

    private static string? Clean(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return null;
        }

        var trimmed = expression!.Trim();
        while (trimmed.EndsWith(";", StringComparison.Ordinal))
        {
            trimmed = trimmed.Substring(0, trimmed.Length - 1).TrimEnd();
        }

        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string? Normalize(string? declaredType)
    {
        if (string.IsNullOrWhiteSpace(declaredType))
        {
            return null;
        }

        var trimmed = declaredType!.Trim();
        return UninformativeTypes.Contains(trimmed) ? null : trimmed;
    }
}
