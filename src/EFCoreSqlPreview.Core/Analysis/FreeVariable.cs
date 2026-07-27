using Microsoft.CodeAnalysis.Text;

namespace EFCoreSqlPreview.Core.Analysis;

/// <summary>
/// An identifier the query references but does not declare, together with the value the worker will use for it.
/// </summary>
/// <param name="Name">The identifier as written in source.</param>
/// <param name="Kind">Where the identifier is declared and whether its value is reproducible.</param>
/// <param name="DeclaredTypeName">
/// The declared type text (<c>decimal</c>, <c>int[]</c>, <c>var</c>), or <see langword="null"/> when no
/// declaration was found in this file.
/// </param>
/// <param name="InitializerExpression">Verbatim initializer source when there is one; otherwise <see langword="null"/>.</param>
/// <param name="ValueSource">How <paramref name="SuggestedValueExpression"/> was arrived at.</param>
/// <param name="SuggestedValueExpression">A compilable C# expression for the worker to assign to this variable.</param>
/// <param name="SuggestedDeclaration">
/// A ready-to-paste declaration line such as <c>decimal min = 100m;</c>, or <see langword="null"/> when the
/// type is unknown and the user must supply one.
/// </param>
/// <param name="RequiresUserValue">Whether the tool window must prompt before the preview is meaningful.</param>
/// <param name="DeclarationSpan">Span of the declaration in the user's document; default when unresolved.</param>
/// <param name="UsageSpans">Spans inside the query where the identifier is referenced.</param>
public sealed record FreeVariable(
    string Name,
    FreeVariableKind Kind,
    string? DeclaredTypeName,
    string? InitializerExpression,
    ValueSource ValueSource,
    string SuggestedValueExpression,
    string? SuggestedDeclaration,
    bool RequiresUserValue,
    TextSpan DeclarationSpan,
    IReadOnlyList<TextSpan> UsageSpans)
{
    /// <summary>
    /// Whether the analyzer produced a value without guessing, so a run needs no user input for this variable.
    /// </summary>
    public bool IsReproducible => this.ValueSource
        is ValueSource.LiteralInitializer
        or ValueSource.ConstructibleInitializer
        or ValueSource.WellKnownStatic
        or ValueSource.TransitiveLocal
        or ValueSource.UserSupplied;
}
