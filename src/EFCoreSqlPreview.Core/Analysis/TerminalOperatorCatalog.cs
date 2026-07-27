namespace EFCoreSqlPreview.Core.Analysis;

/// <summary>
/// The definitive table of LINQ / EF Core terminal operators the analyzer recognises.
/// </summary>
/// <remarks>
/// A name that is absent from <see cref="All"/> means the chain has no terminal in the SQL sense:
/// either it is still deferred (see <see cref="IsDeferredOperator"/>) or it is a user-defined extension.
/// </remarks>
public static class TerminalOperatorCatalog
{
    private static readonly Dictionary<string, TerminalOperatorDescriptor> Catalog = Build();

    private static readonly HashSet<string> Deferred = new(StringComparer.Ordinal)
    {
        "Where", "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending",
        "Select", "SelectMany", "Include", "ThenInclude", "Join", "GroupJoin", "GroupBy",
        "Distinct", "Skip", "Take", "SkipWhile", "TakeWhile", "Union", "Concat", "Intersect",
        "Except", "Reverse", "DefaultIfEmpty", "AsQueryable", "AsNoTracking",
        "AsNoTrackingWithIdentityResolution", "AsTracking", "AsSplitQuery", "AsSingleQuery",
        "IgnoreQueryFilters", "IgnoreAutoIncludes", "TagWith", "TagWithCallSite", "OfType",
        "Cast", "Set", "Entry", "Local", "Zip", "Chunk", "Order", "OrderDescending",
    };

    /// <summary>Every catalogued terminal operator, keyed by method name (ordinal comparison).</summary>
    public static IReadOnlyDictionary<string, TerminalOperatorDescriptor> All => Catalog;

    /// <summary>
    /// Every deferred (non-terminal) query operator name the analyzer recognises. Used to decide
    /// whether a chain is query shaped even when it has no terminal.
    /// </summary>
    public static IReadOnlyCollection<string> DeferredOperators => Deferred;

    /// <summary>Looks up a terminal operator by method name.</summary>
    /// <param name="methodName">The method name as written in source.</param>
    /// <returns>The descriptor, or <see langword="null"/> when the name is not a catalogued terminal.</returns>
    public static TerminalOperatorDescriptor? Lookup(string? methodName)
        => methodName is not null && Catalog.TryGetValue(methodName, out var descriptor) ? descriptor : null;

    /// <summary>Whether the name is a known deferred (non-terminal) query operator.</summary>
    /// <param name="methodName">The method name as written in source.</param>
    /// <returns><see langword="true"/> when the operator does not end server translation.</returns>
    public static bool IsDeferredOperator(string? methodName)
        => methodName is not null && Deferred.Contains(methodName);

    private static Dictionary<string, TerminalOperatorDescriptor> Build()
    {
        var map = new Dictionary<string, TerminalOperatorDescriptor>(StringComparer.Ordinal);

        void Add(
            string name,
            bool isAsync,
            ResultShape shape,
            bool takesPredicate = false,
            bool takesRequiredLambda = false,
            bool takesValueArgument = false,
            bool takesKeySelectors = false,
            bool throwsOnEmptyReader = false,
            string? note = null)
        {
            map[name] = new TerminalOperatorDescriptor(
                name, isAsync, shape, takesPredicate, takesRequiredLambda,
                takesValueArgument, takesKeySelectors, throwsOnEmptyReader, note);
        }

        Add("ToList", false, ResultShape.List);
        Add("ToListAsync", true, ResultShape.List);
        Add("ToArray", false, ResultShape.Array);
        Add("ToArrayAsync", true, ResultShape.Array);
        Add("ToDictionary", false, ResultShape.Dictionary, takesKeySelectors: true);
        Add("ToDictionaryAsync", true, ResultShape.Dictionary, takesKeySelectors: true);
        Add("ToHashSet", false, ResultShape.HashSet);
        Add("ToHashSetAsync", true, ResultShape.HashSet);
        Add("ToLookup", false, ResultShape.Lookup, takesKeySelectors: true,
            note: "ToLookup is LINQ-to-Objects only and forces client evaluation of everything upstream.");

        Add("AsEnumerable", false, ResultShape.DeferredEnumerable,
            note: "AsEnumerable ends server translation; the SQL shown covers only the part before it.");
        Add("AsAsyncEnumerable", true, ResultShape.AsyncEnumerable,
            note: "AsAsyncEnumerable is deferred; the worker enumerates it to force SQL generation.");

        Add("First", false, ResultShape.FirstElement, takesPredicate: true);
        Add("FirstAsync", true, ResultShape.FirstElement, takesPredicate: true);
        Add("FirstOrDefault", false, ResultShape.FirstElement, takesPredicate: true);
        Add("FirstOrDefaultAsync", true, ResultShape.FirstElement, takesPredicate: true);

        Add("Single", false, ResultShape.SingleElement, takesPredicate: true);
        Add("SingleAsync", true, ResultShape.SingleElement, takesPredicate: true);
        Add("SingleOrDefault", false, ResultShape.SingleElement, takesPredicate: true);
        Add("SingleOrDefaultAsync", true, ResultShape.SingleElement, takesPredicate: true);

        const string lastNote = "EF Core requires an OrderBy upstream of Last/LastOrDefault.";
        Add("Last", false, ResultShape.LastElement, takesPredicate: true, note: lastNote);
        Add("LastAsync", true, ResultShape.LastElement, takesPredicate: true, note: lastNote);
        Add("LastOrDefault", false, ResultShape.LastElement, takesPredicate: true, note: lastNote);
        Add("LastOrDefaultAsync", true, ResultShape.LastElement, takesPredicate: true, note: lastNote);

        Add("ElementAt", false, ResultShape.SingleElement, takesValueArgument: true);
        Add("ElementAtAsync", true, ResultShape.SingleElement, takesValueArgument: true);
        Add("ElementAtOrDefault", false, ResultShape.SingleElement, takesValueArgument: true);
        Add("ElementAtOrDefaultAsync", true, ResultShape.SingleElement, takesValueArgument: true);

        Add("Count", false, ResultShape.Scalar, takesPredicate: true);
        Add("CountAsync", true, ResultShape.Scalar, takesPredicate: true, throwsOnEmptyReader: true);
        Add("LongCount", false, ResultShape.Scalar, takesPredicate: true);
        Add("LongCountAsync", true, ResultShape.Scalar, takesPredicate: true, throwsOnEmptyReader: true);

        Add("Any", false, ResultShape.Boolean, takesPredicate: true);
        Add("AnyAsync", true, ResultShape.Boolean, takesPredicate: true, throwsOnEmptyReader: true);
        Add("All", false, ResultShape.Boolean, takesRequiredLambda: true);
        Add("AllAsync", true, ResultShape.Boolean, takesRequiredLambda: true, throwsOnEmptyReader: true);

        Add("Sum", false, ResultShape.Scalar, takesPredicate: true);
        Add("SumAsync", true, ResultShape.Scalar, takesPredicate: true, throwsOnEmptyReader: true);
        Add("Average", false, ResultShape.Scalar, takesPredicate: true);
        Add("AverageAsync", true, ResultShape.Scalar, takesPredicate: true, throwsOnEmptyReader: true);
        Add("Min", false, ResultShape.Scalar, takesPredicate: true);
        Add("MinAsync", true, ResultShape.Scalar, takesPredicate: true, throwsOnEmptyReader: true);
        Add("Max", false, ResultShape.Scalar, takesPredicate: true);
        Add("MaxAsync", true, ResultShape.Scalar, takesPredicate: true, throwsOnEmptyReader: true);

        Add("Contains", false, ResultShape.Boolean, takesValueArgument: true);
        Add("ContainsAsync", true, ResultShape.Boolean, takesValueArgument: true, throwsOnEmptyReader: true);

        Add("ForEachAsync", true, ResultShape.Void, takesRequiredLambda: true,
            note: "The SQL is captured but the body never runs, because the capture reader yields no user rows.");
        Add("Load", false, ResultShape.Void);
        Add("LoadAsync", true, ResultShape.Void);

        return map;
    }
}
