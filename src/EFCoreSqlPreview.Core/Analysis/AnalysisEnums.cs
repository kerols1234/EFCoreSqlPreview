namespace EFCoreSqlPreview.Core.Analysis;

/// <summary>
/// Overall outcome of analysing a text selection.
/// </summary>
public enum AnalysisStatus
{
    /// <summary>Everything the worker needs was resolved.</summary>
    Success,

    /// <summary>A query was found and can be run, but something is unresolved (context type, a free variable, ...).</summary>
    PartiallyResolved,

    /// <summary>The selection contains a construct this tool deliberately does not preview.</summary>
    OutOfScope,

    /// <summary>No expression could be located at or around the selection.</summary>
    NoQueryFound,

    /// <summary>An expression was located but it is not query shaped (string literal, comment, arithmetic, ...).</summary>
    NotAQuery,

    /// <summary>The document could not be parsed at all.</summary>
    ParseFailure,
}

/// <summary>
/// Severity of an <see cref="AnalysisDiagnostic"/>.
/// </summary>
public enum AnalysisDiagnosticSeverity
{
    /// <summary>Informational; the run proceeds unchanged.</summary>
    Info,

    /// <summary>The run proceeds but the result may be surprising.</summary>
    Warning,

    /// <summary>The run cannot proceed as-is.</summary>
    Error,
}

/// <summary>
/// The runtime shape produced by the query's terminal operator.
/// </summary>
/// <remarks>
/// <see cref="DeferredQueryable"/> is the "no terminal operator" case that the analyzer covers by
/// synthesising one; <see cref="Void"/> covers <c>ForEachAsync</c> / <c>Load</c>.
/// </remarks>
public enum ResultShape
{
    /// <summary>The shape could not be determined (typically a custom async extension method).</summary>
    Unknown,

    /// <summary>No terminal operator: the selection is still an <see cref="System.Linq.IQueryable"/>.</summary>
    DeferredQueryable,

    /// <summary>The chain ends in <c>AsEnumerable</c> or <c>ToLookup</c>: server translation stops here.</summary>
    DeferredEnumerable,

    /// <summary>The chain ends in <c>AsAsyncEnumerable</c>; no SQL is emitted until enumerated.</summary>
    AsyncEnumerable,

    /// <summary><c>ToList</c> / <c>ToListAsync</c>.</summary>
    List,

    /// <summary><c>ToArray</c> / <c>ToArrayAsync</c>.</summary>
    Array,

    /// <summary><c>ToDictionary</c> / <c>ToDictionaryAsync</c>.</summary>
    Dictionary,

    /// <summary><c>ToHashSet</c> / <c>ToHashSetAsync</c>.</summary>
    HashSet,

    /// <summary><c>ToLookup</c>.</summary>
    Lookup,

    /// <summary><c>First</c> / <c>FirstOrDefault</c> and their async forms.</summary>
    FirstElement,

    /// <summary><c>Single</c> / <c>SingleOrDefault</c> / <c>ElementAt</c> and their async forms.</summary>
    SingleElement,

    /// <summary><c>Last</c> / <c>LastOrDefault</c> and their async forms.</summary>
    LastElement,

    /// <summary><c>Count</c>, <c>Sum</c>, <c>Min</c>, <c>Max</c>, <c>Average</c>, <c>LongCount</c> and async forms.</summary>
    Scalar,

    /// <summary><c>Any</c>, <c>All</c>, <c>Contains</c> and async forms.</summary>
    Boolean,

    /// <summary><c>ForEachAsync</c>, <c>Load</c>, <c>LoadAsync</c>: nothing is returned.</summary>
    Void,
}

/// <summary>
/// How the terminal operator described by <see cref="TerminalOperatorInfo"/> was determined.
/// </summary>
public enum TerminalSource
{
    /// <summary>The selection has no invocation chain at all.</summary>
    None,

    /// <summary>The method name was found in <see cref="TerminalOperatorCatalog"/>.</summary>
    Catalog,

    /// <summary>The name is unknown but the expression is awaited, so it is treated as async.</summary>
    InferredFromAwait,

    /// <summary>The name ends in <c>Async</c> but is not in the catalog: a user-defined extension.</summary>
    CustomAsyncExtension,

    /// <summary>The selection had no terminal; the analyzer added one (see <see cref="TerminalOperatorInfo.SynthesizedTerminalText"/>).</summary>
    Synthesized,
}

/// <summary>
/// The syntactic form of the last <c>Select</c>/<c>SelectMany</c> projection in the chain.
/// </summary>
public enum ProjectionKind
{
    /// <summary>No <c>Select</c> in the chain: entities come back whole.</summary>
    None,

    /// <summary><c>new ProductDto { ... }</c> or <c>new ProductDto(...)</c>.</summary>
    NamedType,

    /// <summary><c>new { p.Id, p.Name }</c>.</summary>
    Anonymous,

    /// <summary><c>(p.Id, p.Name)</c>.</summary>
    Tuple,

    /// <summary><c>p =&gt; p.Name</c>, a cast, or a literal.</summary>
    ScalarMember,

    /// <summary>An expression with no single member behind it, e.g. <c>p =&gt; p.A + p.B</c>.</summary>
    Computed,

    /// <summary>The projection comes from a <c>GroupBy</c> grouping.</summary>
    Grouping,

    /// <summary>A statement lambda, a method group, or <c>ProjectTo&lt;T&gt;</c>.</summary>
    Unresolvable,
}

/// <summary>
/// What the query's element type actually is, as far as it can be told syntactically.
/// </summary>
public enum ElementKind
{
    /// <summary>An EF Core entity type.</summary>
    Entity,

    /// <summary>A named non-entity type projected into by a <c>Select</c>.</summary>
    Dto,

    /// <summary>A compiler-generated anonymous type.</summary>
    Anonymous,

    /// <summary>A value tuple.</summary>
    Tuple,

    /// <summary>A primitive or otherwise non-composite value.</summary>
    Scalar,

    /// <summary>An <c>IGrouping&lt;,&gt;</c>.</summary>
    Grouping,

    /// <summary>Not determinable syntactically.</summary>
    Unknown,
}

/// <summary>
/// The syntactic shape of the expression at the head of the query chain.
/// </summary>
public enum ContextRootKind
{
    /// <summary>A bare identifier, e.g. <c>_context</c>.</summary>
    Identifier,

    /// <summary><c>this._db</c> or <c>base._db</c>.</summary>
    MemberAccessOnThis,

    /// <summary>A dotted path, e.g. <c>_uow.Context</c>.</summary>
    QualifiedField,

    /// <summary>The head is a local holding an already-built query, e.g. <c>q</c> in <c>q.ToListAsync()</c>.</summary>
    LocalQuery,

    /// <summary>The head is something the analyzer cannot classify (a method call, an indexer, ...).</summary>
    Unresolved,
}

/// <summary>
/// Where the declaration of the context root identifier was found in the document.
/// </summary>
public enum ContextResolution
{
    /// <summary>A field of the enclosing type.</summary>
    Field,

    /// <summary>A property of the enclosing type.</summary>
    Property,

    /// <summary>A method, constructor, local-function or lambda parameter.</summary>
    Parameter,

    /// <summary>A primary-constructor parameter of the enclosing type.</summary>
    PrimaryConstructorParameter,

    /// <summary>A local variable in an enclosing block.</summary>
    Local,

    /// <summary>A local variable holding a query rather than the context itself.</summary>
    LocalQuery,

    /// <summary>No declaration for the identifier exists in this file.</summary>
    Unresolved,
}

/// <summary>
/// How confident the analyzer is in <see cref="ContextRootInfo.ResolvedTypeName"/>.
/// </summary>
public enum ContextTypeConfidence
{
    /// <summary>An explicit type name was on the declaration.</summary>
    Declared,

    /// <summary>An explicit type name was on the declaration, but it looks like an interface.</summary>
    DeclaredInterface,

    /// <summary>The declaration was <c>var</c> and the initializer was <c>new SomeContext(...)</c>.</summary>
    InferredFromInitializer,

    /// <summary>The type could not be determined; the worker must discover it at runtime.</summary>
    Unknown,
}

/// <summary>
/// Where a free variable comes from, and whether its value can be reproduced from source.
/// </summary>
public enum FreeVariableKind
{
    /// <summary>A local whose initializer can be pasted into the worker verbatim.</summary>
    ReproducibleLocal,

    /// <summary>A local whose initializer cannot be reproduced (a method call, an await, ...).</summary>
    OpaqueLocal,

    /// <summary>A method / constructor / local-function parameter: it has no initializer at all.</summary>
    Parameter,

    /// <summary>A primary-constructor parameter of the enclosing type.</summary>
    PrimaryConstructorParameter,

    /// <summary>A field of the enclosing type.</summary>
    Field,

    /// <summary>A property of the enclosing type.</summary>
    Property,

    /// <summary>A <c>const</c> local or field: always reproducible.</summary>
    Constant,

    /// <summary>A query-expression range variable that escaped the bound-name filter.</summary>
    RangeVariable,

    /// <summary>No declaration was found in this file.</summary>
    Unresolved,
}

/// <summary>
/// How <see cref="FreeVariable.SuggestedValueExpression"/> was produced.
/// </summary>
public enum ValueSource
{
    /// <summary>Copied from a literal (or literal-composed) initializer.</summary>
    LiteralInitializer,

    /// <summary>Copied from an initializer that constructs a value, e.g. <c>new List&lt;string&gt; { "a" }</c>.</summary>
    ConstructibleInitializer,

    /// <summary>Copied from an allow-listed static, e.g. <c>DateTime.Now.AddDays(-30)</c> or <c>Status.Active</c>.</summary>
    WellKnownStatic,

    /// <summary>Inherited from another local's reproducible initializer.</summary>
    TransitiveLocal,

    /// <summary>Fabricated by <see cref="DefaultValueSynthesizer"/> because nothing usable was found.</summary>
    SynthesizedDefault,

    /// <summary>Supplied by the user through the tool window and fed back via <see cref="AnalyzerOptions.FreeVariableOverrides"/>.</summary>
    UserSupplied,
}

/// <summary>
/// Why a selection is refused. EF Core SQL Preview covers SELECT (read) queries only.
/// </summary>
public enum OutOfScopeReason
{
    /// <summary>The selection is in scope.</summary>
    None,

    /// <summary><c>ExecuteUpdate</c> / <c>ExecuteUpdateAsync</c>.</summary>
    ExecuteUpdate,

    /// <summary><c>ExecuteDelete</c> / <c>ExecuteDeleteAsync</c>.</summary>
    ExecuteDelete,

    /// <summary><c>SaveChanges</c> / <c>SaveChangesAsync</c>.</summary>
    SaveChanges,

    /// <summary><c>FromSql*</c> / <c>ExecuteSql*</c>: the SQL is hand-written, not generated.</summary>
    RawSql,

    /// <summary><c>Add</c> / <c>Update</c> / <c>Remove</c> / <c>Attach</c> and their range forms.</summary>
    Mutation,

    /// <summary>A third-party bulk extension such as <c>BulkInsert</c> or <c>BatchDelete</c>.</summary>
    ThirdPartyBulk,
}
