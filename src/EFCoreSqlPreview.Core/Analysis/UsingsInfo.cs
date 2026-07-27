namespace EFCoreSqlPreview.Core.Analysis;

/// <summary>
/// One <c>using</c> directive collected from the user's document.
/// </summary>
/// <param name="Name">The namespace or type name, e.g. <c>Microsoft.EntityFrameworkCore</c>.</param>
/// <param name="IsGlobal">Whether the directive was declared <c>global using</c>.</param>
/// <param name="IsStatic">Whether the directive was declared <c>using static</c>.</param>
/// <param name="Alias">The alias when the directive is <c>using X = Y;</c>; otherwise <see langword="null"/>.</param>
/// <param name="FullText">The directive's source text, normalised to end with a semicolon.</param>
public sealed record UsingDirectiveInfo(
    string Name,
    bool IsGlobal,
    bool IsStatic,
    string? Alias,
    string FullText);

/// <summary>
/// Every using directive in scope at the query, plus the namespace the query lives in.
/// </summary>
/// <param name="Directives">All collected directives, in source order.</param>
/// <param name="ContainingNamespace">
/// The fully qualified namespace enclosing the query, or <see langword="null"/> for the global namespace
/// (top-level statements).
/// </param>
/// <param name="NamespaceChain">
/// Every namespace prefix from outermost to innermost, e.g. <c>["Demo", "Demo.Services"]</c>. The worker emits a
/// <c>using</c> for each because namespace nesting lookup does not apply across assembly boundaries.
/// </param>
public sealed record UsingsInfo(
    IReadOnlyList<UsingDirectiveInfo> Directives,
    string? ContainingNamespace,
    IReadOnlyList<string> NamespaceChain)
{
    /// <summary>The value used when no usings could be collected.</summary>
    public static UsingsInfo Empty { get; } = new(
        Array.Empty<UsingDirectiveInfo>(),
        ContainingNamespace: null,
        NamespaceChain: Array.Empty<string>());

    /// <summary>
    /// The namespaces the .NET SDK adds implicitly to a file-based app, which the worker therefore need not re-emit.
    /// </summary>
    public static IReadOnlyCollection<string> ImplicitUsings { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "System",
        "System.Collections.Generic",
        "System.IO",
        "System.Linq",
        "System.Net.Http",
        "System.Threading",
        "System.Threading.Tasks",
    };

    /// <summary>
    /// Produces the emission-ready using lines for the generated worker: deduplicated, with the
    /// <c>global</c> modifier stripped (illegal in a top-level-statement file), aliases and
    /// <c>using static</c> preserved verbatim, and the containing namespace chain appended.
    /// </summary>
    /// <param name="subtractImplicitUsings">
    /// When <see langword="true"/>, plain directives already covered by <see cref="ImplicitUsings"/> are omitted
    /// to keep the generated program short.
    /// </param>
    /// <returns>Complete <c>using</c> lines, each terminated with a semicolon, in emission order.</returns>
    public IReadOnlyList<string> ToWorkerUsingLines(bool subtractImplicitUsings = true)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var aliased = new List<string>();
        var statics = new List<string>();
        var plain = new List<string>();

        foreach (var directive in this.Directives)
        {
            if (string.IsNullOrEmpty(directive.Name))
            {
                continue;
            }

            // 'global using X' and 'using X' are the same import once the global modifier is stripped, which it
            // must be: global usings are illegal in the worker's top-level-statement file.
            var key = (directive.IsStatic ? "s|" : "n|") + (directive.Alias ?? string.Empty) + "|" + directive.Name;
            if (!seen.Add(key))
            {
                continue;
            }

            if (directive.Alias is not null)
            {
                aliased.Add("using " + directive.Alias + " = " + directive.Name + ";");
            }
            else if (directive.IsStatic)
            {
                statics.Add("using static " + directive.Name + ";");
            }
            else if (!subtractImplicitUsings || !ImplicitUsings.Contains(directive.Name))
            {
                plain.Add("using " + directive.Name + ";");
            }
        }

        var lines = new List<string>(aliased.Count + statics.Count + plain.Count + this.NamespaceChain.Count);
        lines.AddRange(aliased);
        lines.AddRange(statics);
        lines.AddRange(plain);

        foreach (var segment in this.NamespaceChain)
        {
            if (string.IsNullOrEmpty(segment) || !seen.Add("n||" + segment))
            {
                continue;
            }

            lines.Add("using " + segment + ";");
        }

        return lines;
    }
}
