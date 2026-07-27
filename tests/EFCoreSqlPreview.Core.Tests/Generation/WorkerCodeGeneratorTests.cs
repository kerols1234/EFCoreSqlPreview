using EFCoreSqlPreview.Core.Analysis;
using EFCoreSqlPreview.Core.Execution;
using EFCoreSqlPreview.Core.Generation;
using EFCoreSqlPreview.Core.Projects;
using EFCoreSqlPreview.Core.Tests.Fakes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace EFCoreSqlPreview.Core.Tests.Generation;

/// <summary>
/// Tests for <see cref="WorkerCodeGenerator"/>. The strongest of these is not a string match but
/// <see cref="The_generated_program_always_parses_cleanly"/>: the generated text is fed straight back into
/// Roslyn, which catches every malformed splice a substring assertion would miss.
/// </summary>
public class WorkerCodeGeneratorTests
{
    private const string ScratchDirectory = @"C:\scratch\worker";

    /// <summary>A realistic service class covering usings, an alias, a namespace, free variables and a DTO.</summary>
    private const string ServiceDocument = """
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Demo.Data;
using Demo.Data;
using Other = Demo.Elsewhere;
using static System.Math;

namespace Demo.Services;

public class ProductService
{
    private readonly AppDbContext _db;

    public async Task<List<ProductDto>> SearchAsync(string term)
    {
        decimal minPrice = 100m;
        return await _db.Products
            .Where(p => p.Price > minPrice && p.Name.Contains(term))
            .Select(p => new ProductDto { Id = p.Id, Name = p.Name })
            .ToListAsync();
    }
}
""";

    /// <summary>The query-builder shape: statements before the query that must be replayed.</summary>
    private const string BuilderDocument = """
using Microsoft.EntityFrameworkCore;

namespace Demo;

public class Svc
{
    private readonly AppDbContext db;

    public async Task<List<Product>> Go(bool onlyActive)
    {
        var query = this.db.Products.AsQueryable();
        if (onlyActive) { query = query.Where(p => p.IsActive); }
        return await query.OrderBy(p => p.Name).ToListAsync();
    }
}
""";

    [Fact]
    public void The_project_and_every_additional_project_get_a_directive()
    {
        var worker = Generate(ServiceDocument, "_db.Products", ".ToListAsync()");

        var directives = DirectiveLines(worker, "#:project ");

        directives.ShouldBe(new[]
        {
            @"#:project C:\repo\App\App.csproj",
            @"#:project C:\repo\Data\Data.csproj",
        });
    }

    [Fact]
    public void Directive_lines_never_contain_double_quotes()
    {
        var project = new ProjectContext(
            @"C:\repo\Sample App\Sample App.csproj",
            "Sample App",
            EfProvider.SqlServer,
            "10.0.10",
            "10.0.10",
            "net10.0",
            Array.Empty<string>());

        var worker = Generate(ServiceDocument, "_db.Products", ".ToListAsync()", project: project);

        foreach (var line in worker.SourceText.Split('\n').Where(l => l.StartsWith("#:", StringComparison.Ordinal)))
        {
            // The SDK's directive parser rejects a double quote outright, so a path with spaces must go unquoted.
            line.ShouldNotContain("\"");
        }

        worker.SourceText.ShouldContain(@"#:project C:\repo\Sample App\Sample App.csproj");
    }

    [Fact]
    public void The_required_MSBuild_properties_are_emitted()
    {
        var worker = Generate(ServiceDocument, "_db.Products", ".ToListAsync()");

        // PublishAot=false is load-bearing: EF refuses to build a model under NativeAOT publishing.
        worker.SourceText.ShouldContain("#:property PublishAot=false");
        worker.SourceText.ShouldContain("#:property Nullable=disable");
        worker.SourceText.ShouldContain("#:property TreatWarningsAsErrors=false");
        worker.SourceText.ShouldContain("#:property NoWarn=$(NoWarn);");
    }

    [Fact]
    public void The_auto_detected_provider_emits_no_package_directive_so_restore_is_a_no_op()
    {
        var worker = Generate(ServiceDocument, "_db.Products", ".ToListAsync()");

        DirectiveLines(worker, "#:package").ShouldBeEmpty();
        worker.SourceText.ShouldContain("b.UseSqlServer(Neutral)");
    }

    [Theory]
    [InlineData(EfProvider.SqlServer, "b.UseSqlServer(Neutral)", "Server=.;Database=EFCoreSqlPreview;Trusted_Connection=True;TrustServerCertificate=True")]
    [InlineData(EfProvider.PostgreSql, "b.UseNpgsql(Neutral)", "Host=localhost;Database=EFCoreSqlPreview;Username=preview;Password=preview")]
    [InlineData(EfProvider.Sqlite, "b.UseSqlite(Neutral)", "Data Source=:memory:")]
    [InlineData(EfProvider.MySql, "b.UseMySql(Neutral,", "Server=localhost;Database=EFCoreSqlPreview;User=preview;Password=preview")]
    [InlineData(EfProvider.Oracle, "b.UseOracle(Neutral)", "User Id=preview;Password=preview;Data Source=localhost:1521/XE")]
    public void Each_dialect_configures_its_own_provider_and_connection_string(
        EfProvider provider,
        string expectedCall,
        string expectedConnectionString)
    {
        var worker = Generate(
            ServiceDocument,
            "_db.Products",
            ".ToListAsync()",
            options => options with { Provider = provider, IsDialectOverride = true });

        worker.Provider.ShouldBe(provider);
        worker.SourceText.ShouldContain(expectedCall);
        worker.SourceText.ShouldContain($"public const string Neutral = \"{expectedConnectionString}\";");
        worker.SourceText.ShouldContain($"public static readonly string Dialect = \"{provider}\";");
        AssertParsesCleanly(worker);
    }

    [Fact]
    public void A_forced_dialect_pins_the_package_to_the_projects_EF_Core_major_version()
    {
        var worker = Generate(
            ServiceDocument,
            "_db.Products",
            ".ToListAsync()",
            options => options with { Provider = EfProvider.Sqlite, IsDialectOverride = true });

        DirectiveLines(worker, "#:package").ShouldHaveSingleItem()
            .ShouldBe("#:package Microsoft.EntityFrameworkCore.Sqlite@10.*");
    }

    [Fact]
    public void A_forced_dialect_with_no_build_for_the_projects_EF_Core_major_version_warns_up_front()
    {
        var worker = Generate(
            ServiceDocument,
            "_db.Products",
            ".ToListAsync()",
            options => options with { Provider = EfProvider.MySql, IsDialectOverride = true });

        worker.Warnings.ShouldContain(w => w.Contains("no build for EF Core 10"));
    }

    [Fact]
    public void An_unknown_EF_Core_version_restores_the_forced_dialect_unpinned_and_says_so()
    {
        var project = Project() with { EfCoreVersion = null, ProviderPackageVersion = null };

        var worker = Generate(
            ServiceDocument,
            "_db.Products",
            ".ToListAsync()",
            options => options with { Provider = EfProvider.Sqlite, IsDialectOverride = true },
            project);

        DirectiveLines(worker, "#:package").ShouldHaveSingleItem().ShouldBe("#:package Microsoft.EntityFrameworkCore.Sqlite");
        worker.Warnings.ShouldContain(w => w.Contains("could not be determined"));
    }

    [Fact]
    public void An_undetectable_provider_falls_back_to_SqlServer_with_a_warning()
    {
        var project = Project() with { Provider = EfProvider.Unknown };

        var worker = Generate(
            ServiceDocument,
            "_db.Products",
            ".ToListAsync()",
            options => options with { Provider = EfProvider.Unknown },
            project);

        worker.Provider.ShouldBe(EfProvider.SqlServer);
        worker.Warnings.ShouldContain(w => w.Contains("No EF Core provider package was found"));
    }

    [Fact]
    public void The_users_usings_are_re_emitted_deduplicated_and_without_the_implicit_ones()
    {
        var worker = Generate(ServiceDocument, "_db.Products", ".ToListAsync()");

        var usings = worker.SourceText.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("using ", StringComparison.Ordinal))
            .ToList();

        usings.Count(u => u == "using Demo.Data;").ShouldBe(1);
        usings.ShouldContain("using Other = Demo.Elsewhere;");
        usings.ShouldContain("using static System.Math;");

        // Implicit usings and the ones the template already imports are redundant.
        usings.ShouldNotContain("using System;");
        usings.ShouldNotContain("using System.Linq;");
        usings.ShouldNotContain("using System.Collections.Generic;");
        usings.Count(u => u == "using Microsoft.EntityFrameworkCore;").ShouldBe(1);
        usings.Count(u => u == "using System.Text.Json;").ShouldBe(1);
    }

    [Fact]
    public void The_containing_namespace_and_every_prefix_get_a_using()
    {
        var worker = Generate(ServiceDocument, "_db.Products", ".ToListAsync()");

        // Namespace nesting lookup does not cross an assembly reference, so each segment needs its own using.
        worker.SourceText.ShouldContain("using Demo;");
        worker.SourceText.ShouldContain("using Demo.Services;");
    }

    [Fact]
    public void Global_usings_lose_the_global_modifier()
    {
        const string document = """
global using Contoso.Shared;
using Microsoft.EntityFrameworkCore;

namespace Demo;

public class Svc
{
    private readonly AppDbContext db;

    public Task<int> Go() => this.db.Products.CountAsync();
}
""";

        var worker = Generate(document, "this.db.Products", ".CountAsync()");

        worker.SourceText.ShouldContain("using Contoso.Shared;");
        worker.SourceText.ShouldNotContain("global using");
    }

    [Fact]
    public void Free_variables_are_declared_inside_the_body_lambda()
    {
        var worker = Generate(ServiceDocument, "_db.Products", ".ToListAsync()");

        // Declaring them in an outer scope makes EF name the parameters after a hoisted closure field
        // (@_8__locals1_term) instead of after the user's own identifier (@term).
        worker.SourceText.ShouldContain("        decimal minPrice = 100m;");
        worker.SourceText.ShouldContain("        string term = \"\";");

        var lambdaStart = worker.SourceText.IndexOf("body: async (__efspRawContext, __efspProbe) =>", StringComparison.Ordinal);
        var declaration = worker.SourceText.IndexOf("decimal minPrice = 100m;", StringComparison.Ordinal);
        var observe = worker.SourceText.IndexOf("__efspProbe.ObserveAsync", StringComparison.Ordinal);

        lambdaStart.ShouldBeLessThan(declaration);
        declaration.ShouldBeLessThan(observe);
    }

    [Fact]
    public void A_free_variable_with_no_reproducible_value_produces_a_warning()
    {
        var worker = Generate(ServiceDocument, "_db.Products", ".ToListAsync()");

        worker.Warnings.ShouldContain(w => w.Contains("'term'") && w.Contains("free-variables panel"));
    }

    [Fact]
    public void A_user_override_replaces_the_synthesized_free_variable_value()
    {
        var worker = Generate(
            ServiceDocument,
            "_db.Products",
            ".ToListAsync()",
            options => options with
            {
                FreeVariableOverrides = new Dictionary<string, string>(StringComparer.Ordinal) { ["term"] = "\"widget\"" },
            });

        worker.SourceText.ShouldContain("string term = \"widget\";");
        worker.SourceText.ShouldNotContain("string term = \"\";");
        AssertParsesCleanly(worker);
    }

    [Fact]
    public void The_users_query_is_emitted_verbatim_apart_from_the_context_root_and_the_await()
    {
        var worker = Generate(ServiceDocument, "_db.Products", ".ToListAsync()");

        worker.SourceText.ShouldContain("return await __efspProbe.ObserveAsync(() => __efspCtx.Products");
        worker.SourceText.ShouldContain(".Where(p => p.Price > minPrice && p.Name.Contains(term))");
        worker.SourceText.ShouldContain(".Select(p => new ProductDto { Id = p.Id, Name = p.Name })");
        worker.SourceText.ShouldContain(".ToListAsync());");

        // The harness applies its own await; leaving the user's would double it.
        worker.SourceText.ShouldNotContain("ObserveAsync(() => await");
    }

    [Fact]
    public void A_synchronous_terminal_uses_the_synchronous_probe()
    {
        const string document = """
using Microsoft.EntityFrameworkCore;

namespace Demo;

public class Svc
{
    private readonly AppDbContext db;

    public List<Product> Go() => this.db.Products.Where(p => p.IsActive).ToList();
}
""";

        var worker = Generate(document, "this.db.Products", ".ToList()");

        // The harness declares every Observe* overload; only the call the generator picked matters here.
        worker.SourceText.ShouldContain("return __efspProbe.Observe(() => __efspCtx.Products");
        worker.SourceText.ShouldNotContain("return await __efspProbe.");
    }

    [Fact]
    public void A_selection_with_no_terminal_operator_is_enumerated_instead()
    {
        const string document = """
using Microsoft.EntityFrameworkCore;

namespace Demo;

public class Svc
{
    private readonly AppDbContext db;

    public IQueryable<Product> Go() => this.db.Products.Where(p => p.IsActive);
}
""";

        var worker = Generate(document, "this.db.Products", ".Where(p => p.IsActive)");

        worker.SourceText.ShouldContain("__efspProbe.ObserveDeferred(() => __efspCtx.Products");
        worker.Warnings.ShouldContain(w => w.Contains("no terminal operator"));
    }

    [Fact]
    public void Statements_before_the_query_are_replayed_with_the_context_root_rewritten()
    {
        var worker = Generate(BuilderDocument, "var query =", "ToListAsync()");

        worker.SourceText.ShouldContain("        var query = __efspCtx.Products.AsQueryable();");
        worker.SourceText.ShouldContain("        if (onlyActive) { query = query.Where(p => p.IsActive); }");
        worker.SourceText.ShouldNotContain("this.db.Products");
        worker.Warnings.ShouldContain(w => w.Contains("statement(s) before the query were replayed"));
        AssertParsesCleanly(worker);
    }

    [Fact]
    public void A_name_the_replayed_statements_declare_is_not_declared_a_second_time()
    {
        var worker = Generate(BuilderDocument, "var query =", "ToListAsync()");

        // Declaring `query` again from the free-variable list would be CS0128.
        CountOccurrences(worker.SourceText, "var query =").ShouldBe(1);
    }

    [Fact]
    public void The_sentinels_the_extension_looks_for_are_present()
    {
        var worker = Generate(ServiceDocument, "_db.Products", ".ToListAsync()");

        worker.SourceText.ShouldContain(PreviewResponse.PayloadBeginSentinel);
        worker.SourceText.ShouldContain(PreviewResponse.PayloadEndSentinel);
        worker.SourceText.ShouldContain("Console.Out.Write(Begin);");
        worker.SourceText.ShouldContain("Console.Out.Write(End);");
    }

    /// <summary>
    /// Guards CS8803. Everything before the first type declaration must be a top-level statement, and every
    /// type declaration must follow the last of them.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllWorkerVariants))]
    public void All_type_declarations_come_after_the_top_level_statements(string variant)
    {
        var root = ParseWorker(GenerateVariant(variant));
        var members = root.Members;

        var lastStatement = members.Select((m, i) => (m, i)).Where(x => x.m is GlobalStatementSyntax).Select(x => x.i).DefaultIfEmpty(-1).Max();
        var firstType = members.Select((m, i) => (m, i)).Where(x => x.m is BaseTypeDeclarationSyntax).Select(x => x.i).DefaultIfEmpty(int.MaxValue).Min();

        lastStatement.ShouldBeGreaterThanOrEqualTo(0, "the worker must have top-level statements");
        firstType.ShouldBeLessThan(int.MaxValue, "the worker must declare the harness types");
        lastStatement.ShouldBeLessThan(firstType, "a type declaration before a top-level statement is CS8803");
    }

    /// <summary>
    /// The single most valuable assertion in this file: Roslyn re-parses what the generator produced, so a
    /// mis-spliced placeholder fails here rather than three seconds later inside <c>dotnet run</c>.
    /// </summary>
    /// <param name="variant">Which generation variant to check.</param>
    [Theory]
    [MemberData(nameof(AllWorkerVariants))]
    public void The_generated_program_always_parses_cleanly(string variant)
        => AssertParsesCleanly(GenerateVariant(variant));

    [Theory]
    [MemberData(nameof(AllWorkerVariants))]
    public void The_generated_program_never_declares_an_identifier_named_args(string variant)
    {
        var root = ParseWorker(GenerateVariant(variant));

        // Top-level statements implicitly declare `args`; redeclaring it inside the body lambda is CS0136.
        root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .ShouldNotContain(d => d.Identifier.Text == "args");
        root.DescendantNodes().OfType<ParameterSyntax>()
            .ShouldNotContain(p => p.Identifier.Text == "args");
    }

    [Theory]
    [MemberData(nameof(AllWorkerVariants))]
    public void The_generated_program_declares_no_local_function(string variant)
    {
        var root = ParseWorker(GenerateVariant(variant));

        // A local function referenced from inside the query would be CS8110 in an expression tree.
        root.DescendantNodes().OfType<LocalFunctionStatementSyntax>().ShouldBeEmpty();
    }

    [Fact]
    public void A_free_variable_that_collides_with_a_reserved_identifier_is_reported_instead_of_emitted()
    {
        const string document = """
using Microsoft.EntityFrameworkCore;

namespace Demo;

public class Svc
{
    private readonly AppDbContext db;

    public Task<List<Product>> Go(string[] args) => this.db.Products.Where(p => args.Contains(p.Sku)).ToListAsync();
}
""";

        var worker = Generate(document, "this.db.Products", ".ToListAsync()");

        worker.Warnings.ShouldContain(w => w.Contains("'args'") && w.Contains("collides"));
        worker.SourceText.ShouldNotContain("string[] args =");
        AssertParsesCleanly(worker);
    }

    [Fact]
    public void An_explicit_context_type_override_wins_over_the_analyzers_resolution()
    {
        var worker = Generate(
            ServiceDocument,
            "_db.Products",
            ".ToListAsync()",
            options => options with { DbContextTypeName = "Contoso.Data.OtherContext" });

        worker.ContextTypeName.ShouldBe("Contoso.Data.OtherContext");
        worker.SourceText.ShouldContain("typeof(Contoso.Data.OtherContext)");
        worker.SourceText.ShouldContain("(Contoso.Data.OtherContext)__efspRawContext");
    }

    [Fact]
    public void A_derived_context_subclass_is_emitted_only_when_asked_for()
    {
        var plain = Generate(ServiceDocument, "_db.Products", ".ToListAsync()");
        plain.SourceText.ShouldNotContain("PreviewDerived_");

        var derived = Generate(
            ServiceDocument,
            "_db.Products",
            ".ToListAsync()",
            options => options with { UseDerivedContext = true });

        derived.SourceText.ShouldContain("public sealed class PreviewDerived_AppDbContext : AppDbContext");
        derived.SourceText.ShouldContain("base.OnConfiguring(builder);");
        derived.SourceText.ShouldContain("builder.AddInterceptors(Pending);");

        // typeof() names the subclass while the cast stays on the user's own type.
        derived.SourceText.ShouldContain("typeof(PreviewDerived_AppDbContext)");
        derived.SourceText.ShouldContain("(AppDbContext)__efspRawContext");
        derived.SourcePath.ShouldEndWith("worker-derived.cs");
    }

    [Fact]
    public void An_unresolvable_context_type_produces_a_discovery_program_instead()
    {
        const string document = """
using Microsoft.EntityFrameworkCore;

namespace Demo;

public class Svc
{
    private readonly IUnitOfWork _uow;

    public Task<List<Product>> Go() => this._uow.Context.Products.ToListAsync();
}
""";

        var worker = Generate(document, "this._uow.Context.Products", ".ToListAsync()");

        worker.IsContextDiscovery.ShouldBeTrue();
        worker.ContextTypeName.ShouldBeNull();
        worker.SourcePath.ShouldEndWith("discover.cs");
        worker.SourceText.ShouldContain(WorkerTemplate.ContextCandidatePrefix);
        AssertParsesCleanly(worker);
    }

    [Fact]
    public void Each_variant_gets_its_own_file_name_so_the_SDK_cache_stays_warm_for_all_of_them()
    {
        var names = new[]
        {
            Generate(ServiceDocument, "_db.Products", ".ToListAsync()").SourcePath,
            Generate(ServiceDocument, "_db.Products", ".ToListAsync()", o => o with { Provider = EfProvider.Sqlite, IsDialectOverride = true }).SourcePath,
            Generate(ServiceDocument, "_db.Products", ".ToListAsync()", o => o with { UseDerivedContext = true }).SourcePath,
        };

        names.Distinct(StringComparer.OrdinalIgnoreCase).Count().ShouldBe(3);
        names.ShouldAllBe(p => p.StartsWith(ScratchDirectory, StringComparison.Ordinal));
    }

    [Fact]
    public void The_program_is_written_to_the_scratch_directory()
    {
        var fileSystem = new InMemoryFileSystem();
        var worker = Generate(ServiceDocument, "_db.Products", ".ToListAsync()", fileSystem: fileSystem);

        fileSystem.Files.Keys.ShouldContain(worker.SourcePath);
        fileSystem.Files[worker.SourcePath].ShouldBe(worker.SourceText);
    }

    [Fact]
    public void Regenerating_identical_text_does_not_rewrite_the_file()
    {
        var fileSystem = new InMemoryFileSystem();

        Generate(ServiceDocument, "_db.Products", ".ToListAsync()", fileSystem: fileSystem);
        fileSystem.EffectiveWrites.ShouldBe(1);

        // A rewrite would change the timestamp and cost the SDK its warm artifact cache.
        Generate(ServiceDocument, "_db.Products", ".ToListAsync()", fileSystem: fileSystem);
        fileSystem.EffectiveWrites.ShouldBe(1);
    }

    [Fact]
    public void The_generated_text_uses_only_line_feed_endings()
    {
        var worker = Generate(ServiceDocument, "_db.Products", ".ToListAsync()");

        worker.SourceText.ShouldNotContain("\r");
    }

    [Fact]
    public void GetScratchDirectory_is_stable_for_the_same_document_and_selection()
    {
        var generator = new WorkerCodeGenerator(new InMemoryFileSystem());

        var first = generator.GetScratchDirectory(@"C:\repo\App\Queries.cs", 100, 40);
        var second = generator.GetScratchDirectory(@"C:\repo\App\Queries.cs", 100, 40);

        first.ShouldBe(second);
        first.ShouldStartWith(@"C:\fake\LocalAppData\EFCoreSqlPreview\scratch\");
        System.IO.Path.GetFileName(first).ShouldStartWith("Queries-");
    }

    [Fact]
    public void GetScratchDirectory_ignores_path_casing_but_not_the_selection()
    {
        var generator = new WorkerCodeGenerator(new InMemoryFileSystem());

        // The hash ignores casing so a differently cased path reuses the same warm build artifacts. Only the
        // human-readable prefix follows the input, and Windows directory names are case-insensitive anyway.
        generator.GetScratchDirectory(@"C:\repo\App\Queries.cs", 100, 40)
            .ShouldBe(generator.GetScratchDirectory(@"c:\REPO\app\queries.cs", 100, 40), StringCompareShould.IgnoreCase);

        generator.GetScratchDirectory(@"C:\repo\App\Queries.cs", 100, 40)
            .ShouldNotBe(generator.GetScratchDirectory(@"C:\repo\App\Queries.cs", 101, 40));

        generator.GetScratchDirectory(@"C:\repo\App\Queries.cs", 100, 40)
            .ShouldNotBe(generator.GetScratchDirectory(@"C:\repo\App\Queries.cs", 100, 41));
    }

    [Fact]
    public void GetScratchDirectory_sanitizes_a_document_name_that_is_not_path_safe()
    {
        var generator = new WorkerCodeGenerator(new InMemoryFileSystem());

        var directory = generator.GetScratchDirectory(@"C:\repo\App\My Queries (v2).cs", 0, 10);

        System.IO.Path.GetFileName(directory).ShouldStartWith("My_Queries__v2_-");
    }

    [Fact]
    public void The_line_map_points_generated_lines_back_at_the_users_document()
    {
        var (documentText, selection) = SpanOf(ServiceDocument, "_db.Products", ".ToListAsync()");
        var analysis = LinqSelectionAnalyzer.Analyze(documentText, selection);
        var worker = new WorkerCodeGenerator(new InMemoryFileSystem()).Generate(analysis, Options());

        worker.LineMap.ShouldNotBeEmpty();

        var lines = worker.SourceText.Split('\n');
        var minPriceLine = Array.FindIndex(lines, l => l.Contains("decimal minPrice = 100m;", StringComparison.Ordinal)) + 1;
        var expected = analysis.FreeVariables.Single(v => v.Name == "minPrice").DeclarationSpan.Start;

        worker.LineMap.ContainsKey(minPriceLine).ShouldBeTrue();
        worker.LineMap[minPriceLine].ShouldBe(expected);

        // The analyzer's DeclarationSpan is the declarator, not the whole statement, so the offset lands on the
        // identifier rather than on `decimal`. Either is a fine place for the editor to put the caret.
        documentText.Substring(worker.LineMap[minPriceLine]).ShouldStartWith("minPrice = 100m;");

        var queryLine = Array.FindIndex(lines, l => l.Contains("ObserveAsync", StringComparison.Ordinal)) + 1;
        worker.LineMap.ContainsKey(queryLine).ShouldBeTrue();
        worker.LineMap[queryLine].ShouldBe(analysis.QuerySpan.Start);
    }

    [Fact]
    public void Generate_rejects_null_arguments()
    {
        var generator = new WorkerCodeGenerator(new InMemoryFileSystem());
        var (documentText, selection) = SpanOf(ServiceDocument, "_db.Products", ".ToListAsync()");
        var analysis = LinqSelectionAnalyzer.Analyze(documentText, selection);

        Should.Throw<ArgumentNullException>(() => generator.Generate(null!, Options()));
        Should.Throw<ArgumentNullException>(() => generator.Generate(analysis, null!));
        Should.Throw<ArgumentNullException>(() => new WorkerCodeGenerator(null!));
    }

    /// <summary>Every generation variant the parse and structure theories run over.</summary>
    /// <returns>One variant name per row.</returns>
    public static TheoryData<string> AllWorkerVariants() => new(
        "service",
        "builder",
        "sqlite",
        "postgres",
        "mysql",
        "oracle",
        "derived",
        "discovery",
        "deferred",
        "sync",
        "querysyntax",
        "override");

    private static GeneratedWorker GenerateVariant(string variant) => variant switch
    {
        "service" => Generate(ServiceDocument, "_db.Products", ".ToListAsync()"),
        "builder" => Generate(BuilderDocument, "var query =", "ToListAsync()"),
        "sqlite" => Generate(ServiceDocument, "_db.Products", ".ToListAsync()", o => o with { Provider = EfProvider.Sqlite, IsDialectOverride = true }),
        "postgres" => Generate(ServiceDocument, "_db.Products", ".ToListAsync()", o => o with { Provider = EfProvider.PostgreSql, IsDialectOverride = true }),
        "mysql" => Generate(ServiceDocument, "_db.Products", ".ToListAsync()", o => o with { Provider = EfProvider.MySql, IsDialectOverride = true }),
        "oracle" => Generate(ServiceDocument, "_db.Products", ".ToListAsync()", o => o with { Provider = EfProvider.Oracle, IsDialectOverride = true }),
        "derived" => Generate(ServiceDocument, "_db.Products", ".ToListAsync()", o => o with { UseDerivedContext = true }),
        "discovery" => Generate(UnresolvableContextDocument, "this._uow.Context.Products", ".ToListAsync()"),
        "deferred" => Generate(DeferredDocument, "this.db.Products", ".Where(p => p.IsActive)"),
        "sync" => Generate(SyncDocument, "this.db.Products", ".ToList()"),
        "querysyntax" => Generate(QuerySyntaxDocument, "from p in", "p.Name };"),
        "override" => Generate(
            ServiceDocument,
            "_db.Products",
            ".ToListAsync()",
            o => o with { FreeVariableOverrides = new Dictionary<string, string>(StringComparer.Ordinal) { ["term"] = "\"widget\"", ["minPrice"] = "42.5m" } }),
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unknown variant."),
    };

    private const string UnresolvableContextDocument = """
using Microsoft.EntityFrameworkCore;

namespace Demo;

public class Svc
{
    private readonly IUnitOfWork _uow;

    public Task<List<Product>> Go() => this._uow.Context.Products.ToListAsync();
}
""";

    private const string DeferredDocument = """
using Microsoft.EntityFrameworkCore;

namespace Demo;

public class Svc
{
    private readonly AppDbContext db;

    public IQueryable<Product> Go() => this.db.Products.Where(p => p.IsActive);
}
""";

    private const string SyncDocument = """
using Microsoft.EntityFrameworkCore;

namespace Demo;

public class Svc
{
    private readonly AppDbContext db;

    public List<Product> Go() => this.db.Products.Where(p => p.IsActive).ToList();
}
""";

    private const string QuerySyntaxDocument = """
using Microsoft.EntityFrameworkCore;

namespace Demo;

public class Svc
{
    private readonly AppDbContext db;

    public IQueryable<object> Go()
        => from p in this.db.Products
           where p.IsActive
           select new { p.Id, p.Name };
}
""";

    private static GeneratedWorker Generate(
        string document,
        string selectionStartsAt,
        string selectionEndsAfter,
        Func<WorkerGenerationOptions, WorkerGenerationOptions>? configure = null,
        ProjectContext? project = null,
        InMemoryFileSystem? fileSystem = null)
    {
        var (documentText, selection) = SpanOf(document, selectionStartsAt, selectionEndsAfter);
        var analysis = LinqSelectionAnalyzer.Analyze(documentText, selection);
        analysis.CanRun.ShouldBeTrue($"the fixture must analyse; status was {analysis.Status}");

        var options = Options(project);
        if (configure is not null)
        {
            options = configure(options);
        }

        return new WorkerCodeGenerator(fileSystem ?? new InMemoryFileSystem()).Generate(analysis, options);
    }

    private static (string Text, TextSpan Span) SpanOf(string document, string startsAt, string endsAfter)
    {
        var start = document.IndexOf(startsAt, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"the fixture must contain '{startsAt}'");

        var endIndex = document.IndexOf(endsAfter, start, StringComparison.Ordinal);
        endIndex.ShouldBeGreaterThanOrEqualTo(0, $"the fixture must contain '{endsAfter}'");

        return (document, TextSpan.FromBounds(start, endIndex + endsAfter.Length));
    }

    private static WorkerGenerationOptions Options(ProjectContext? project = null)
        => new(project ?? Project(), (project ?? Project()).Provider, DbContextTypeName: null, ScratchDirectory);

    private static ProjectContext Project()
        => new(
            ProjectPath: @"C:\repo\App\App.csproj",
            ProjectName: "App",
            Provider: EfProvider.SqlServer,
            ProviderPackageVersion: "10.0.10",
            EfCoreVersion: "10.0.10",
            TargetFramework: "net10.0",
            AdditionalProjectPaths: new[] { @"C:\repo\Data\Data.csproj" });

    private static IReadOnlyList<string> DirectiveLines(GeneratedWorker worker, string prefix)
        => worker.SourceText.Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => l.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();

    private static void AssertParsesCleanly(GeneratedWorker worker)
    {
        var root = ParseWorker(worker);
        root.ShouldNotBeNull();
    }

    /// <summary>
    /// Parses the generated program with Roslyn and asserts it has no syntax errors.
    /// </summary>
    /// <param name="worker">The generated worker.</param>
    /// <returns>The parsed compilation unit.</returns>
    /// <remarks>
    /// The <c>#:</c> directive lines are blanked first: they are an SDK feature the C# parser knows nothing
    /// about, and it would report them as malformed preprocessor directives. Blanking rather than deleting
    /// keeps the line numbers aligned with the real file so a reported error is easy to locate.
    /// </remarks>
    private static CompilationUnitSyntax ParseWorker(GeneratedWorker worker)
    {
        var withoutDirectives = string.Join(
            "\n",
            worker.SourceText.Split('\n').Select(l => l.StartsWith("#:", StringComparison.Ordinal) ? string.Empty : l));

        var tree = CSharpSyntaxTree.ParseText(withoutDirectives, LinqSelectionAnalyzer.ParseOptions);
        var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

        errors.ShouldBeEmpty(
            "the generated program must parse:\n"
            + string.Join("\n", errors.Select(e => e.ToString()))
            + "\n\n"
            + Numbered(withoutDirectives, errors));

        return (CompilationUnitSyntax)tree.GetRoot();
    }

    private static string Numbered(string text, IReadOnlyList<Diagnostic> errors)
    {
        if (errors.Count == 0)
        {
            return string.Empty;
        }

        var lines = text.Split('\n');
        var target = errors[0].Location.GetLineSpan().StartLinePosition.Line;
        var from = Math.Max(0, target - 6);
        var to = Math.Min(lines.Length - 1, target + 6);

        return string.Join("\n", Enumerable.Range(from, to - from + 1).Select(i => $"{i + 1,5}: {lines[i]}"));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
