namespace EFCoreSqlPreview.Core.Analysis;

/// <summary>
/// Knobs that let the tool window feed user choices back into a re-analysis.
/// </summary>
public sealed record AnalyzerOptions
{
    /// <summary>The options used when the caller does not care.</summary>
    public static AnalyzerOptions Default { get; } = new();

    /// <summary>
    /// Overrides syntactic DbContext type resolution. Persisted per project after the user picks from the
    /// context-type picker.
    /// </summary>
    public string? DbContextTypeOverride { get; init; }

    /// <summary>
    /// User-supplied C# value expressions for free variables, keyed by variable name. A match here wins over
    /// anything the analyzer would infer and sets <see cref="ValueSource.UserSupplied"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> FreeVariableOverrides { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// The terminal to add when the selection has none. Defaults to <c>ToListAsync</c>; the analyzer falls back
    /// to <c>ToList</c> when the enclosing method is not async and EF Core is not imported.
    /// </summary>
    public string SynthesizedTerminal { get; init; } = "ToListAsync";

    /// <summary>How many hops of local-to-local initializer resolution to follow before giving up.</summary>
    public int MaxTransitiveInitializerDepth { get; init; } = 3;

    /// <summary>Extra method names to treat as out of scope, e.g. a house bulk-update extension.</summary>
    public IReadOnlyList<string> AdditionalOutOfScopeMethods { get; init; } = Array.Empty<string>();
}
