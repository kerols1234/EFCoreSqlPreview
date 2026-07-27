using Microsoft.CodeAnalysis.Text;

namespace EFCoreSqlPreview.Core.Analysis;

/// <summary>
/// The DbContext the selected query hangs off, as far as syntax can tell.
/// </summary>
/// <param name="Kind">The syntactic shape of the head of the chain.</param>
/// <param name="Expression">Verbatim source of the head expression, e.g. <c>_context</c> or <c>this._db</c>.</param>
/// <param name="IdentifierName">The rightmost identifier of the head, e.g. <c>_db</c>; <see langword="null"/> when unresolved.</param>
/// <param name="ResolvedTypeName">The DbContext type name, or <see langword="null"/> when it could not be determined.</param>
/// <param name="Resolution">Where the declaration of <paramref name="IdentifierName"/> was found.</param>
/// <param name="Confidence">How much to trust <paramref name="ResolvedTypeName"/>.</param>
/// <param name="SourceSetName">
/// The DbSet property name the query starts from, e.g. <c>Products</c>; <see langword="null"/> when
/// <c>Set&lt;T&gt;()</c> was used instead.
/// </param>
/// <param name="SourceSetTypeArgument">
/// The entity type argument from <c>Set&lt;Product&gt;()</c>, or <see langword="null"/> when a DbSet property was used.
/// </param>
/// <param name="Span">Span of the head expression in the user's document.</param>
public sealed record ContextRootInfo(
    ContextRootKind Kind,
    string Expression,
    string? IdentifierName,
    string? ResolvedTypeName,
    ContextResolution Resolution,
    ContextTypeConfidence Confidence,
    string? SourceSetName,
    string? SourceSetTypeArgument,
    TextSpan Span)
{
    /// <summary>The value used when no context root could be identified at all.</summary>
    public static ContextRootInfo Unresolved { get; } = new(
        Kind: ContextRootKind.Unresolved,
        Expression: string.Empty,
        IdentifierName: null,
        ResolvedTypeName: null,
        Resolution: ContextResolution.Unresolved,
        Confidence: ContextTypeConfidence.Unknown,
        SourceSetName: null,
        SourceSetTypeArgument: null,
        Span: default);

    /// <summary>Alias for <see cref="Expression"/>, matching the wording used in the design notes.</summary>
    public string RootExpressionText => this.Expression;

    /// <summary>Alias for <see cref="IdentifierName"/>, matching the wording used in the design notes.</summary>
    public string? RootIdentifier => this.IdentifierName;

    /// <summary>Alias for <see cref="ResolvedTypeName"/>, matching the wording used in the design notes.</summary>
    public string? DbContextTypeName => this.ResolvedTypeName;
}
