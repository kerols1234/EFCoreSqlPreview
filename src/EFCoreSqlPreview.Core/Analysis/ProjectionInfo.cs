using Microsoft.CodeAnalysis.Text;

namespace EFCoreSqlPreview.Core.Analysis;

/// <summary>
/// What the last <c>Select</c>/<c>SelectMany</c> in the chain projects to.
/// </summary>
/// <param name="Kind">The syntactic form of the projection.</param>
/// <param name="ElementKind">What the resulting element actually is.</param>
/// <param name="TypeName">
/// The projected type name when it is syntactically recoverable (<c>ProductDto</c>), the member name for a
/// scalar member projection (<c>Name</c>), or <see langword="null"/> for anonymous and target-typed projections.
/// </param>
/// <param name="MemberNames">Member names assigned in the projection, in source order; empty when not applicable.</param>
/// <param name="ProjectionLambdaText">Verbatim source of the projection lambda, or <see langword="null"/>.</param>
/// <param name="IsGroupedProjection">Whether the projection reads from a <c>GroupBy</c> grouping.</param>
/// <param name="Span">Span of the projection in the user's document.</param>
public sealed record ProjectionInfo(
    ProjectionKind Kind,
    ElementKind ElementKind,
    string? TypeName,
    IReadOnlyList<string> MemberNames,
    string? ProjectionLambdaText,
    bool IsGroupedProjection,
    TextSpan Span)
{
    /// <summary>The default "no projection, entities come back whole" value.</summary>
    public static ProjectionInfo None { get; } = new(
        Kind: ProjectionKind.None,
        ElementKind: ElementKind.Entity,
        TypeName: null,
        MemberNames: Array.Empty<string>(),
        ProjectionLambdaText: null,
        IsGroupedProjection: false,
        Span: default);

    /// <summary>Alias for <see cref="TypeName"/>, matching the wording used in the design notes.</summary>
    public string? ProjectedTypeName => this.TypeName;
}
