using Microsoft.CodeAnalysis.Text;

namespace EFCoreSqlPreview.Core.Analysis;

/// <summary>
/// Records that the selection contains a construct EF Core SQL Preview deliberately does not preview.
/// </summary>
/// <param name="Reason">Which construct was found.</param>
/// <param name="OffendingMethodName">The method name that triggered the detection.</param>
/// <param name="Message">A ready-to-display explanation.</param>
/// <param name="Span">Span of the offending call in the user's document.</param>
/// <param name="IsHardBlock">
/// Whether the VSIX must refuse to spawn the worker. <c>FromSql*</c> composed into a larger LINQ chain is
/// reported as a warning rather than a block, because EF still generates real SQL around it.
/// </param>
public sealed record OutOfScopeInfo(
    OutOfScopeReason Reason,
    string? OffendingMethodName,
    string Message,
    TextSpan Span,
    bool IsHardBlock)
{
    /// <summary>The value used when the selection is in scope.</summary>
    public static OutOfScopeInfo None { get; } = new(
        Reason: OutOfScopeReason.None,
        OffendingMethodName: null,
        Message: string.Empty,
        Span: default,
        IsHardBlock: false);

    /// <summary>Whether anything out of scope was detected at all.</summary>
    public bool IsOutOfScope => this.Reason != OutOfScopeReason.None;
}
