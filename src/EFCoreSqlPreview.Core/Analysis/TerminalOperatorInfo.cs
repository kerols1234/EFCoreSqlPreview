using Microsoft.CodeAnalysis.Text;

namespace EFCoreSqlPreview.Core.Analysis;

/// <summary>
/// Static facts about one terminal operator name, as catalogued in <see cref="TerminalOperatorCatalog"/>.
/// </summary>
/// <param name="Name">The method name, e.g. <c>ToListAsync</c>.</param>
/// <param name="IsAsync">Whether the method returns a task.</param>
/// <param name="Shape">The shape the method produces.</param>
/// <param name="TakesPredicate">Whether the method accepts an optional predicate or selector lambda.</param>
/// <param name="TakesRequiredLambda">Whether a lambda argument is mandatory (<c>All</c>, <c>ForEachAsync</c>).</param>
/// <param name="TakesValueArgument">Whether the argument is a value rather than a lambda (<c>Contains</c>, <c>ElementAt</c>).</param>
/// <param name="TakesKeySelectors">Whether the method takes key/element selectors (<c>ToDictionary</c>, <c>ToLookup</c>).</param>
/// <param name="ThrowsOnEmptyReader">
/// Whether materialization throws when the capture reader yields no rows. The worker harness swallows a
/// post-capture exception for these and still reports success.
/// </param>
/// <param name="Note">Optional guidance surfaced in the tool window.</param>
public sealed record TerminalOperatorDescriptor(
    string Name,
    bool IsAsync,
    ResultShape Shape,
    bool TakesPredicate,
    bool TakesRequiredLambda,
    bool TakesValueArgument,
    bool TakesKeySelectors,
    bool ThrowsOnEmptyReader,
    string? Note);

/// <summary>
/// What the analyzer determined about the terminal operator of the selected chain.
/// </summary>
/// <param name="Name">The terminal method name, or <see langword="null"/> when the chain has none.</param>
/// <param name="Source">How this information was arrived at.</param>
/// <param name="IsAsync">Whether the terminal is asynchronous. Independent of <paramref name="IsAwaited"/>.</param>
/// <param name="IsAwaited">Whether the user's source actually awaits the expression.</param>
/// <param name="Shape">The runtime shape the terminal produces.</param>
/// <param name="HasPredicateArgument">Whether the user passed a predicate/selector lambda to the terminal.</param>
/// <param name="ArgumentCount">Number of arguments at the terminal call site.</param>
/// <param name="TypeArguments">Explicit type arguments at the terminal call site, in order.</param>
/// <param name="ArgumentTexts">Verbatim source text of each terminal argument, in order.</param>
/// <param name="SynthesizedTerminalText">
/// The terminal the analyzer added, e.g. <c>".ToListAsync()"</c>, when <paramref name="Source"/> is
/// <see cref="TerminalSource.Synthesized"/>; otherwise <see langword="null"/>.
/// </param>
/// <param name="Span">Span of the terminal invocation in the user's document.</param>
/// <param name="Descriptor">The catalog entry backing this terminal, when there is one.</param>
public sealed record TerminalOperatorInfo(
    string? Name,
    TerminalSource Source,
    bool IsAsync,
    bool IsAwaited,
    ResultShape Shape,
    bool HasPredicateArgument,
    int ArgumentCount,
    IReadOnlyList<string> TypeArguments,
    IReadOnlyList<string> ArgumentTexts,
    string? SynthesizedTerminalText,
    TextSpan Span,
    TerminalOperatorDescriptor? Descriptor)
{
    /// <summary>
    /// An empty descriptor for selections with no invocation chain at all.
    /// </summary>
    public static TerminalOperatorInfo None { get; } = new(
        Name: null,
        Source: TerminalSource.None,
        IsAsync: false,
        IsAwaited: false,
        Shape: ResultShape.Unknown,
        HasPredicateArgument: false,
        ArgumentCount: 0,
        TypeArguments: Array.Empty<string>(),
        ArgumentTexts: Array.Empty<string>(),
        SynthesizedTerminalText: null,
        Span: default,
        Descriptor: null);

    /// <summary>
    /// Whether materialization is expected to throw against the capture reader, so the worker
    /// must treat a post-capture exception as success.
    /// </summary>
    public bool ThrowsOnEmptyReader => this.Descriptor?.ThrowsOnEmptyReader ?? false;

    /// <summary>The first terminal argument's verbatim text, used as the dictionary key selector.</summary>
    public string? DictionaryKeySelectorText
        => this.Shape == ResultShape.Dictionary && this.ArgumentTexts.Count > 0 ? this.ArgumentTexts[0] : null;

    /// <summary>The second terminal argument's verbatim text, used as the dictionary element selector.</summary>
    public string? DictionaryValueSelectorText
        => this.Shape == ResultShape.Dictionary && this.ArgumentTexts.Count > 1 ? this.ArgumentTexts[1] : null;
}
