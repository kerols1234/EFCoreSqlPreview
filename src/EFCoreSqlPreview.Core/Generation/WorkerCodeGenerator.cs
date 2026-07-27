using System.Security.Cryptography;
using System.Text;
using EFCoreSqlPreview.Core.Analysis;
using EFCoreSqlPreview.Core.Infrastructure;
using EFCoreSqlPreview.Core.Projects;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFCoreSqlPreview.Core.Generation;

/// <summary>
/// Identifiers the generated worker reserves. All are prefixed <c>__efsp</c> so a user alias can never collide
/// with them; if one somehow did, the worker identifier is renamed, never the user's code.
/// </summary>
public static class WorkerIdentifiers
{
    /// <summary>The DbContext instance the rewritten query runs against.</summary>
    public const string Context = "__efspCtx";

    /// <summary>The capture interceptor instance.</summary>
    public const string Interceptor = "__efspInterceptor";

    /// <summary>The observed query result.</summary>
    public const string Result = "__efspResult";

    /// <summary>The list of captured commands.</summary>
    public const string Commands = "__efspCommands";

    /// <summary>The probe the generated body calls <c>Observe*</c> on.</summary>
    public const string Probe = "__efspProbe";
}

/// <summary>
/// Inputs to worker generation that come from outside the analysis.
/// </summary>
/// <param name="Project">The project the worker will reference.</param>
/// <param name="Provider">The provider to force. Normally <c>Project.Provider</c>, or the picker's choice.</param>
/// <param name="DbContextTypeName">
/// The context type to activate. When <see langword="null"/> the worker discovers it by scanning the
/// referenced assemblies for a single public non-abstract <c>DbContext</c> subclass.
/// </param>
/// <param name="ScratchDirectory">
/// Where to write <c>worker.cs</c>. Must be stable for a given (document, selection) pair: the SDK keys its
/// build artifact cache on the full file path, and a stable path is what makes a re-run take about 2.6 s
/// instead of about 4 s.
/// </param>
public sealed record WorkerGenerationOptions(
    ProjectContext Project,
    EfProvider Provider,
    string? DbContextTypeName,
    string ScratchDirectory)
{
    /// <summary>
    /// Whether to emit a subclass that attaches the interceptor from <c>OnConfiguring</c>.
    /// </summary>
    /// <remarks>
    /// A context with no <c>DbContextOptions</c> constructor cannot have its options rebuilt, so without the
    /// subclass the interceptor is never attached and EF really dials the database. The subclass only compiles
    /// when the context is unsealed and has an accessible parameterless constructor, so it is a second attempt
    /// rather than the default.
    /// </remarks>
    public bool UseDerivedContext { get; init; }

    /// <summary>Values the user typed into the free-variables panel, keyed by variable name.</summary>
    public IReadOnlyDictionary<string, string> FreeVariableOverrides { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Whether the caller forced the provider through the dialect picker.</summary>
    public bool IsDialectOverride { get; init; }
}

/// <summary>
/// The generated worker program and the metadata needed to interpret its failures.
/// </summary>
/// <param name="SourcePath">Absolute path of the written <c>.cs</c> file, ready for <c>dotnet run --file</c>.</param>
/// <param name="SourceText">The generated program text, shown verbatim in the "Generated program" tab.</param>
/// <param name="LineMap">
/// Maps 1-based line numbers in the generated program to offsets in the user's document, so a compiler error
/// on a generated line can be pointed back at the selection that caused it.
/// </param>
/// <param name="Warnings">Notes produced while generating, e.g. a free variable that had to be defaulted.</param>
public sealed record GeneratedWorker(
    string SourcePath,
    string SourceText,
    IReadOnlyDictionary<int, int> LineMap,
    IReadOnlyList<string> Warnings)
{
    /// <summary>The context type the program activates, or <see langword="null"/> for a discovery run.</summary>
    public string? ContextTypeName { get; init; }

    /// <summary>Whether this program only enumerates candidate <c>DbContext</c> types instead of running the query.</summary>
    public bool IsContextDiscovery { get; init; }

    /// <summary>The provider the program configures.</summary>
    public EfProvider Provider { get; init; } = EfProvider.Unknown;
}

/// <summary>
/// Emits the .NET 10 file-based app that runs the user's query against a neutralised DbContext.
/// </summary>
public interface IWorkerCodeGenerator
{
    /// <summary>Generates the worker program for an analysed query.</summary>
    /// <param name="analysis">The analysis result. Must satisfy <see cref="QueryAnalysisResult.CanRun"/>.</param>
    /// <param name="options">Project, provider and output location.</param>
    /// <returns>The generated program, already written to disk.</returns>
    GeneratedWorker Generate(QueryAnalysisResult analysis, WorkerGenerationOptions options);

    /// <summary>
    /// Computes the stable scratch directory for a document and selection, under
    /// <c>%LOCALAPPDATA%\EFCoreSqlPreview\scratch</c>.
    /// </summary>
    /// <param name="documentPath">Absolute path to the user's document.</param>
    /// <param name="selectionStart">Start offset of the normalised selection.</param>
    /// <param name="selectionLength">Length of the normalised selection.</param>
    /// <returns>An absolute directory path; the directory may not exist yet.</returns>
    string GetScratchDirectory(string documentPath, int selectionStart, int selectionLength);
}

/// <summary>
/// The default <see cref="IWorkerCodeGenerator"/>.
/// </summary>
/// <remarks>
/// Several constraints on the emitted text are non-negotiable: no double quotes and no <c>#:</c> sequence on
/// directive lines, top-level statements before any type declaration, never an identifier named <c>args</c>,
/// no local functions referenced from inside the query (they are illegal in expression trees), and free
/// variables declared inside the same lambda as the query so EF names the SQL parameters after them.
/// </remarks>
public sealed class WorkerCodeGenerator : IWorkerCodeGenerator
{
    /// <summary>The indentation of the generated body, matching the lambda in the template.</summary>
    private const string BodyIndent = "        ";

    /// <summary>Namespaces the worker template already imports, so the user's copies are redundant.</summary>
    private static readonly HashSet<string> TemplateUsings = new(StringComparer.Ordinal)
    {
        "System.Collections",
        "System.Data.Common",
        "System.Globalization",
        "System.Reflection",
        "System.Text",
        "System.Text.Json",
        "Microsoft.EntityFrameworkCore",
        "Microsoft.EntityFrameworkCore.Design",
        "Microsoft.EntityFrameworkCore.Diagnostics",
        "Microsoft.EntityFrameworkCore.Infrastructure",
        "Microsoft.EntityFrameworkCore.Metadata",
    };

    /// <summary>Creates a generator over the real file system.</summary>
    public WorkerCodeGenerator()
        : this(Infrastructure.FileSystem.Default)
    {
    }

    /// <summary>Creates a generator over a specific file system.</summary>
    /// <param name="fileSystem">Used to write the generated program.</param>
    public WorkerCodeGenerator(IFileSystem fileSystem)
        => this.FileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    /// <summary>The file system the generated program is written through.</summary>
    public IFileSystem FileSystem { get; }

    /// <inheritdoc />
    public string GetScratchDirectory(string documentPath, int selectionStart, int selectionLength)
    {
        var key = (documentPath ?? string.Empty).ToLowerInvariant() + "|" + selectionStart + "|" + selectionLength;
        var name = SanitizeForPath(System.IO.Path.GetFileNameWithoutExtension(documentPath ?? "query"));
        return System.IO.Path.Combine(
            this.FileSystem.LocalApplicationDataPath,
            "EFCoreSqlPreview",
            "scratch",
            name + "-" + ShortHash(key));
    }

    /// <inheritdoc />
    public GeneratedWorker Generate(QueryAnalysisResult analysis, WorkerGenerationOptions options)
    {
        if (analysis is null)
        {
            throw new ArgumentNullException(nameof(analysis));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var warnings = new List<string>();
        var provider = ResolveProvider(options, warnings);
        var descriptor = EfProviders.Describe(provider) ?? EfProviders.Describe(EfProvider.SqlServer)!;
        var contextTypeName = ResolveContextTypeName(analysis, options);

        return contextTypeName is null
            ? this.GenerateDiscovery(options, descriptor, warnings)
            : this.GenerateQueryWorker(analysis, options, descriptor, contextTypeName, warnings);
    }

    private GeneratedWorker GenerateQueryWorker(
        QueryAnalysisResult analysis,
        WorkerGenerationOptions options,
        EfProviderDescriptor descriptor,
        string contextTypeName,
        List<string> warnings)
    {
        var body = this.BuildBody(analysis, options, warnings);
        var derivedTypeName = "PreviewDerived_" + SimpleName(contextTypeName);

        var text = WorkerTemplate.Source
            .Replace(WorkerTemplate.ProjectDirectivesPlaceholder, BuildProjectDirectives(options.Project))
            .Replace(WorkerTemplate.PackageDirectivesPlaceholder, BuildPackageDirectives(options, descriptor, warnings))
            .Replace(WorkerTemplate.PropertyDirectivesPlaceholder, WorkerTemplate.PropertyDirectives)
            .Replace(WorkerTemplate.UserUsingsPlaceholder, BuildUsings(analysis.Usings))
            .Replace(WorkerTemplate.ContextCastTypePlaceholder, contextTypeName)
            .Replace(WorkerTemplate.ContextTypePlaceholder, options.UseDerivedContext ? derivedTypeName : contextTypeName);

        var lineMap = new Dictionary<int, int>();

        var freeVariableLine = LineNumberOf(text, WorkerTemplate.FreeVariablesPlaceholder);
        text = text.Replace(WorkerTemplate.FreeVariablesPlaceholder, string.Join("\n", body.Lines));
        for (var i = 0; i < body.LineOffsets.Count; i++)
        {
            lineMap[freeVariableLine + i] = body.LineOffsets[i];
        }

        var observeLine = LineNumberOf(text, WorkerTemplate.ObserveCallPlaceholder);
        text = text.Replace(WorkerTemplate.ObserveCallPlaceholder, body.ObserveCall);
        var observeLineCount = CountLines(body.ObserveCall);
        for (var i = 0; i < observeLineCount; i++)
        {
            lineMap[observeLine + i] = analysis.QuerySpan.Start;
        }

        text = text
            .Replace(WorkerTemplate.DerivedContextPlaceholder, options.UseDerivedContext ? BuildDerivedContext(contextTypeName, derivedTypeName) : string.Empty)
            .Replace(WorkerTemplate.DialectPlaceholder, options.IsDialectOverride ? Literal(descriptor.Provider.ToString()) : "null")
            .Replace(WorkerTemplate.ConnectionStringPlaceholder, Literal(descriptor.ConnectionString))
            .Replace(WorkerTemplate.ProviderConfigPlaceholder, "b." + string.Format(descriptor.ConfigureCall, "Neutral"));

        text = NormalizeLineEndings(text);

        // One file name per variant. The SDK keys its artifact cache on the full path, so giving the dialect
        // and derived-context variants their own names keeps each of them warm instead of evicting the others.
        var fileName = "worker"
            + (options.IsDialectOverride ? "-" + descriptor.Provider : string.Empty)
            + (options.UseDerivedContext ? "-derived" : string.Empty)
            + ".cs";
        var path = System.IO.Path.Combine(options.ScratchDirectory, fileName);
        this.FileSystem.WriteAllText(path, text);

        warnings.AddRange(body.Warnings);

        return new GeneratedWorker(path, text, lineMap, warnings)
        {
            ContextTypeName = contextTypeName,
            Provider = descriptor.Provider,
        };
    }

    private GeneratedWorker GenerateDiscovery(
        WorkerGenerationOptions options,
        EfProviderDescriptor descriptor,
        List<string> warnings)
    {
        var text = NormalizeLineEndings(WorkerTemplate.DiscoverySource
            .Replace(WorkerTemplate.ProjectDirectivesPlaceholder, BuildProjectDirectives(options.Project))
            .Replace(WorkerTemplate.PackageDirectivesPlaceholder, BuildPackageDirectives(options, descriptor, warnings))
            .Replace(WorkerTemplate.PropertyDirectivesPlaceholder, WorkerTemplate.PropertyDirectives));

        var path = System.IO.Path.Combine(options.ScratchDirectory, "discover.cs");
        this.FileSystem.WriteAllText(path, text);

        return new GeneratedWorker(path, text, new Dictionary<int, int>(), warnings)
        {
            IsContextDiscovery = true,
            Provider = descriptor.Provider,
        };
    }

    /// <summary>
    /// The generated body: free-variable declarations, replayed statements and the <c>Observe*</c> call.
    /// </summary>
    private sealed class GeneratedBody
    {
        public List<string> Lines { get; } = new();

        public List<int> LineOffsets { get; } = new();

        public List<string> Warnings { get; } = new();

        public string ObserveCall { get; set; } = string.Empty;
    }

    private GeneratedBody BuildBody(QueryAnalysisResult analysis, WorkerGenerationOptions options, List<string> warnings)
    {
        var body = new GeneratedBody();

        var prologue = RewritePrologue(analysis, body.Warnings);
        var prologueNames = CollectDeclaredNames(prologue);

        foreach (var declaration in ValueSynthesizer.SynthesizeAll(analysis.FreeVariables, options.FreeVariableOverrides, prologueNames))
        {
            if (ValueSynthesizer.IsReservedName(declaration.Name))
            {
                // Top-level statements already declare `args`, so redeclaring it inside the body lambda is
                // CS0136 rather than a shadow. Nothing can be emitted for it.
                body.Warnings.Add($"The free variable '{declaration.Name}' collides with an identifier the generated program owns; "
                    + "the program will not compile until it is renamed in the source.");
                continue;
            }

            var offset = FindOffset(analysis, declaration.Name);
            AppendUnit(body, declaration.Declaration, offset);
            if (declaration.Warning is not null)
            {
                body.Warnings.Add(declaration.Warning);
            }
        }

        if (prologue.Count > 0)
        {
            AppendUnit(body, string.Empty, analysis.QuerySpan.Start);
            foreach (var statement in prologue)
            {
                AppendUnit(body, statement, analysis.QuerySpan.Start);
            }
        }

        body.ObserveCall = BuildObserveCall(analysis, body.Warnings);
        warnings.AddRange(body.Warnings);
        body.Warnings.Clear();
        return body;
    }

    private static void AppendUnit(GeneratedBody body, string text, int offset)
    {
        foreach (var line in NormalizeLineEndings(text).Split('\n'))
        {
            body.Lines.Add(line.Length == 0 ? string.Empty : BodyIndent + line);
            body.LineOffsets.Add(offset);
        }
    }

    /// <summary>
    /// Chooses the <c>Observe*</c> overload for the classified terminal and strips the user's <c>await</c>,
    /// which the harness reapplies itself.
    /// </summary>
    private static string BuildObserveCall(QueryAnalysisResult analysis, List<string> warnings)
    {
        var query = ReindentContinuations(StripLeadingAwait(analysis.NormalizedQueryText));
        var terminal = analysis.TerminalOperator;
        var probe = WorkerIdentifiers.Probe;

        if (terminal.Source == TerminalSource.Synthesized)
        {
            warnings.Add("The selection has no terminal operator, so it was enumerated to force EF Core to build the SQL.");
            return $"{probe}.ObserveDeferred(() => {query})";
        }

        switch (terminal.Shape)
        {
            case ResultShape.AsyncEnumerable:
                return $"await {probe}.ObserveAsyncEnumerable(() => {query})";

            case ResultShape.Void:
                return terminal.IsAsync || terminal.IsAwaited
                    ? $"await {probe}.ObserveVoidAsync(() => {query})"
                    : $"{probe}.ObserveVoid(() => {query})";

            case ResultShape.DeferredQueryable:
                return $"{probe}.ObserveDeferred(() => {query})";

            case ResultShape.DeferredEnumerable:
                return $"{probe}.ObserveEnumerable(() => {query})";

            default:
                if (terminal.Source == TerminalSource.CustomAsyncExtension)
                {
                    warnings.Add($"'{terminal.Name}' is not a known terminal operator. It is run exactly as written and the "
                        + "result shape is reported from its runtime type.");
                }

                return terminal.IsAsync || terminal.IsAwaited
                    ? $"await {probe}.ObserveAsync(() => {query})"
                    : $"{probe}.Observe(() => {query})";
        }
    }

    /// <summary>
    /// Rewrites the DbContext root in the replayed statements, which the analyzer leaves verbatim.
    /// </summary>
    private static IReadOnlyList<string> RewritePrologue(QueryAnalysisResult analysis, List<string> warnings)
    {
        if (analysis.PrecedingStatements.Count == 0)
        {
            return Array.Empty<string>();
        }

        var root = analysis.ContextRoot.Expression;
        var results = new List<string>(analysis.PrecedingStatements.Count);

        foreach (var statement in analysis.PrecedingStatements)
        {
            results.Add(string.IsNullOrEmpty(root)
                ? statement
                : ReplaceIdentifierPath(statement, root, WorkerIdentifiers.Context));
        }

        warnings.Add($"{analysis.PrecedingStatements.Count} statement(s) before the query were replayed verbatim so the "
            + "query is built exactly as it is at run time.");

        return results;
    }

    /// <summary>
    /// Replaces an identifier or dotted path with another, respecting identifier boundaries so <c>db</c> does
    /// not match inside <c>dbContext</c> or the tail of <c>this.db</c>.
    /// </summary>
    /// <param name="text">The text to rewrite.</param>
    /// <param name="path">The expression to replace, e.g. <c>this._db</c>.</param>
    /// <param name="replacement">The identifier to put in its place.</param>
    /// <returns>The rewritten text.</returns>
    internal static string ReplaceIdentifierPath(string text, string path, string replacement)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(path))
        {
            return text;
        }

        var pattern = "(?<![\\w.])" + System.Text.RegularExpressions.Regex.Escape(path) + "(?![\\w])";
        return System.Text.RegularExpressions.Regex.Replace(text, pattern, replacement);
    }

    private static ISet<string> CollectDeclaredNames(IReadOnlyList<string> statements)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var statement in statements)
        {
            var parsed = SyntaxFactory.ParseStatement(statement);
            foreach (var declarator in parsed.DescendantNodesAndSelf().OfType<VariableDeclaratorSyntax>())
            {
                names.Add(declarator.Identifier.Text);
            }

            foreach (var loop in parsed.DescendantNodesAndSelf().OfType<ForEachStatementSyntax>())
            {
                names.Add(loop.Identifier.Text);
            }
        }

        return names;
    }

    private static int FindOffset(QueryAnalysisResult analysis, string name)
    {
        foreach (var variable in analysis.FreeVariables)
        {
            if (string.Equals(variable.Name, name, StringComparison.Ordinal))
            {
                return variable.DeclarationSpan.Length > 0 || variable.DeclarationSpan.Start > 0
                    ? variable.DeclarationSpan.Start
                    : analysis.QuerySpan.Start;
            }
        }

        return analysis.QuerySpan.Start;
    }

    private static EfProvider ResolveProvider(WorkerGenerationOptions options, List<string> warnings)
    {
        if (options.Provider != EfProvider.Unknown)
        {
            return options.Provider;
        }

        if (options.Project.Provider != EfProvider.Unknown)
        {
            return options.Project.Provider;
        }

        warnings.Add("No EF Core provider package was found in the project or its references, so SQL Server was assumed. "
            + "Use the dialect picker to choose a different one.");
        return EfProvider.SqlServer;
    }

    private static string? ResolveContextTypeName(QueryAnalysisResult analysis, WorkerGenerationOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.DbContextTypeName))
        {
            return options.DbContextTypeName!.Trim();
        }

        var resolved = analysis.ContextRoot.ResolvedTypeName;
        if (string.IsNullOrWhiteSpace(resolved) || analysis.ContextRoot.Confidence == ContextTypeConfidence.Unknown)
        {
            return null;
        }

        // An interface cannot be activated, and typeof(IAppDbContext) would fail the DbContext constraint.
        return analysis.ContextRoot.Confidence == ContextTypeConfidence.DeclaredInterface ? null : resolved!.Trim();
    }

    private static string BuildProjectDirectives(ProjectContext project)
    {
        var lines = new List<string>();
        foreach (var path in project.AllProjectPaths)
        {
            // Directive lines may not contain double quotes; unquoted absolute paths with spaces are accepted.
            lines.Add("#:project " + path);
        }

        return string.Join("\n", lines);
    }

    private static string BuildPackageDirectives(
        WorkerGenerationOptions options,
        EfProviderDescriptor descriptor,
        List<string> warnings)
    {
        if (!options.IsDialectOverride || descriptor.Provider == options.Project.Provider)
        {
            // The provider arrives transitively through #:project, which makes restore a no-op.
            return string.Empty;
        }

        var major = options.Project.EfCoreMajorVersion;
        if (major is null)
        {
            warnings.Add($"The project's EF Core version could not be determined, so {descriptor.DisplayName} is restored at "
                + "its latest version. A major-version mismatch shows up as a restore or MissingMethodException failure.");
            return "#:package " + descriptor.PackageId;
        }

        if (major >= 10 && descriptor.Provider is EfProvider.MySql or EfProvider.Oracle)
        {
            warnings.Add($"{descriptor.DisplayName} has no build for EF Core {major}, so this dialect is expected to fail. "
                + "It is available for projects on EF Core 9.");
        }

        return "#:package " + descriptor.PackageId + "@" + major + ".*";
    }

    private static string BuildDerivedContext(string contextTypeName, string derivedTypeName)
        => $$"""
public sealed class {{derivedTypeName}} : {{contextTypeName}}
{
    public static CaptureInterceptor Pending;

    protected override void OnConfiguring(DbContextOptionsBuilder builder)
    {
        // The user's own provider choice wins; the interceptor then neutralises the connection.
        base.OnConfiguring(builder);
        builder.AddInterceptors(Pending);
        builder.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
    }
}
""";

    /// <summary>
    /// Produces the worker's using block: deduplicated, <c>global</c> stripped (illegal in a top-level-statement
    /// file), aliases and <c>using static</c> preserved, and the containing namespace chain appended.
    /// </summary>
    /// <param name="usings">The directives collected from the user's document.</param>
    /// <returns>The using lines, newline separated.</returns>
    /// <remarks>
    /// Every directive is re-emitted, <c>global using</c> included: global usings are per-compilation and do not
    /// cross an assembly reference into the worker.
    /// </remarks>
    internal static string BuildUsings(UsingsInfo usings)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var aliased = new List<string>();
        var statics = new List<string>();
        var plain = new List<string>();

        foreach (var directive in usings.Directives)
        {
            if (string.IsNullOrWhiteSpace(directive.Name))
            {
                continue;
            }

            var name = directive.Name.Trim();

            if (!string.IsNullOrEmpty(directive.Alias))
            {
                Add(aliased, $"using {directive.Alias} = {name};");
            }
            else if (directive.IsStatic)
            {
                Add(statics, $"using static {name};");
            }
            else if (!UsingsInfo.ImplicitUsings.Contains(name) && !TemplateUsings.Contains(name))
            {
                Add(plain, $"using {name};");
            }
        }

        // Namespace nesting lookup does not apply across assemblies, so each segment needs its own using.
        foreach (var segment in usings.NamespaceChain)
        {
            if (!string.IsNullOrWhiteSpace(segment))
            {
                Add(plain, $"using {segment.Trim()};");
            }
        }

        if (!string.IsNullOrWhiteSpace(usings.ContainingNamespace))
        {
            Add(plain, $"using {usings.ContainingNamespace!.Trim()};");
        }

        return string.Join("\n", aliased.Concat(statics).Concat(plain));

        void Add(List<string> target, string line)
        {
            if (seen.Add(line))
            {
                target.Add(line);
            }
        }
    }

    private static string StripLeadingAwait(string query)
    {
        var trimmed = query.TrimStart();
        if (!trimmed.StartsWith("await", StringComparison.Ordinal))
        {
            return trimmed.TrimEnd();
        }

        var rest = trimmed.Substring("await".Length);
        return rest.Length > 0 && (char.IsWhiteSpace(rest[0]) || rest[0] == '(')
            ? rest.TrimStart().TrimEnd()
            : trimmed.TrimEnd();
    }

    /// <summary>Re-indents the continuation lines of a multi-line query so the generated file stays readable.</summary>
    private static string ReindentContinuations(string query)
    {
        var lines = NormalizeLineEndings(query).Split('\n');
        if (lines.Length == 1)
        {
            return query;
        }

        var builder = new StringBuilder(lines[0]);
        for (var i = 1; i < lines.Length; i++)
        {
            builder.Append('\n').Append(BodyIndent).Append("    ").Append(lines[i].TrimStart());
        }

        return builder.ToString();
    }

    private static int LineNumberOf(string text, string marker)
    {
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return 1;
        }

        var line = 1;
        for (var i = 0; i < index; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static int CountLines(string text)
    {
        var count = 1;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private static string NormalizeLineEndings(string text)
        => text.Replace("\r\n", "\n").Replace("\r", "\n");

    private static string Literal(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string SimpleName(string typeName)
    {
        var lastDot = typeName.LastIndexOf('.');
        var name = lastDot < 0 ? typeName : typeName.Substring(lastDot + 1);
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        }

        return builder.Length == 0 ? "Context" : builder.ToString();
    }

    private static string SanitizeForPath(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
        }

        return builder.Length == 0 ? "query" : builder.ToString();
    }

    private static string ShortHash(string value)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
        var builder = new StringBuilder(16);
        for (var i = 0; i < 8; i++)
        {
            builder.Append(bytes[i].ToString("x2"));
        }

        return builder.ToString();
    }
}
