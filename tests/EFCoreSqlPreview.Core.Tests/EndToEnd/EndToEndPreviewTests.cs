using System.Text;
using EFCoreSqlPreview.Core.Execution;
using EFCoreSqlPreview.Core.Projects;
using Microsoft.CodeAnalysis.Text;
using Xunit.Abstractions;

namespace EFCoreSqlPreview.Core.Tests.EndToEnd;

/// <summary>
/// Runs the whole pipeline for real against <c>samples/SampleShop</c>: analyse, locate the project, detect the
/// provider, generate the worker, <c>dotnet run --file</c> it, and parse the payload.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is mocked and no database is contacted; the worker's interceptor suppresses the connection and
/// feeds EF Core a synthetic reader. These are the tests that would catch a regression the unit tests cannot
/// see, such as the SDK changing how <c>#:project</c> is parsed.
/// </para>
/// <para>
/// Each one costs a few seconds, so they carry the <c>EndToEnd</c> trait and can be excluded with
/// <c>dotnet test --filter "Category!=EndToEnd"</c>. Parallelism is disabled: concurrent <c>dotnet run</c>
/// invocations fight over the same build locks.
/// </para>
/// </remarks>
[Trait("Category", "EndToEnd")]
[Collection(EndToEndCollection.Name)]
public class EndToEndPreviewTests
{
    private readonly ITestOutputHelper output;

    /// <summary>Creates the fixture.</summary>
    /// <param name="output">Receives the observed SQL, so a failure shows what actually came back.</param>
    public EndToEndPreviewTests(ITestOutputHelper output) => this.output = output;

    [EndToEndFact]
    public async Task A_DTO_projection_with_a_captured_variable_produces_a_join_and_a_parameter()
    {
        var result = await RunAsync("dto-projection", "this.db.Products", ".ToListAsync()");

        var command = SingleCommand(result);
        command.Sql.ShouldContain("SELECT");
        command.Sql.ShouldContain("FROM [Products] AS [p]");
        command.Sql.ShouldContain("[Categories]");
        command.Sql.ShouldContain("[p].[Price] >");

        var parameter = command.Parameters.ShouldHaveSingleItem();
        parameter.Name.ShouldContain("minPrice");
        parameter.Value.ShouldBe("100");
        parameter.DbType.ShouldBe("Decimal");
        parameter.ClrType.ShouldBe("decimal");
        parameter.IsNull.ShouldBeFalse();

        var shape = result.Response.Result.ShouldNotBeNull();
        shape.IsAsync.ShouldBeTrue();
        shape.Shape.ShouldBe("List");
        shape.ElementType.ShouldBe("ProductDto");
        shape.ElementKind.ShouldBe("Dto");
    }

    [EndToEndFact]
    public async Task FirstOrDefaultAsync_with_a_predicate_produces_a_single_row_query()
    {
        var result = await RunAsync("first-or-default", "this.db.Products", "p.Id == 42)");

        var command = SingleCommand(result);
        command.Sql.ShouldContain("TOP(1)");
        command.Sql.ShouldContain("[p].[Id] = 42");

        var shape = result.Response.Result.ShouldNotBeNull();
        shape.IsAsync.ShouldBeTrue();
        shape.ElementType.ShouldBe("Product");
        shape.ElementKind.ShouldBe("Entity");

        // The worker derives the shape from the returned value's type, which cannot tell First from Single: a
        // Product is a Product either way. The distinction survives on the analyzer's side of the result, and
        // that is the one the tool window should show.
        shape.Shape.ShouldBe("SingleElement");
        result.Analysis.TerminalOperator.Name.ShouldBe("FirstOrDefaultAsync");
        result.Analysis.TerminalOperator.Shape.ShouldBe(Core.Analysis.ResultShape.FirstElement);
    }

    [EndToEndFact]
    public async Task CountAsync_produces_an_aggregate_and_does_not_throw_on_the_synthetic_reader()
    {
        var result = await RunAsync("count", "this.db.Products", ".CountAsync()");

        var command = SingleCommand(result);
        command.Sql.ShouldContain("COUNT(*)");
        command.Sql.ShouldContain("[p].[IsActive]");

        // An aggregate terminal materializes eagerly. With a zero-row reader it would throw
        // "Sequence contains no elements"; the synthetic single-row reader is what keeps this green.
        result.Response.Error.ShouldBeNull();
        result.Response.CaptureMode.ShouldBe("SyntheticRow");

        var shape = result.Response.Result.ShouldNotBeNull();
        shape.Shape.ShouldBe("Scalar");
        shape.ElementType.ShouldBe("int");
        shape.ElementKind.ShouldBe("Scalar");
    }

    [EndToEndFact]
    public async Task ToDictionaryAsync_with_an_array_variable_expands_into_an_IN_list()
    {
        var result = await RunAsync("dictionary", "this.db.Products", "p => p.Name)");

        var command = SingleCommand(result);
        command.Sql.ShouldContain("[p].[Id] IN (");
        command.Parameters.Count.ShouldBe(3);
        command.Parameters.Select(p => p.Value).ShouldBe(new[] { "1", "2", "3" });
        command.Parameters.ShouldAllBe(p => p.DbType == "Int32");

        var shape = result.Response.Result.ShouldNotBeNull();
        shape.Shape.ShouldBe("Dictionary");
        shape.KeyType.ShouldBe("int");
        shape.ElementType.ShouldBe("string");
    }

    [EndToEndFact]
    public async Task Custom_IQueryable_extension_methods_resolve_and_contribute_to_the_SQL()
    {
        var result = await RunAsync("custom-extensions", "this.db.Products", ".ToListAsync()");

        var command = SingleCommand(result);

        // ActiveOnly()
        command.Sql.ShouldContain("[p].[IsActive]");

        // Search(term)
        command.Sql.ShouldContain("LIKE");

        // PricedAbove(floor)
        command.Sql.ShouldContain("[p].[Price] >");

        // ToDto() projects across the Category navigation.
        command.Sql.ShouldContain("[Categories]");

        command.Parameters.ShouldContain(p => p.Value == "%widget%");
        command.Parameters.ShouldContain(p => p.Value == "10" && p.DbType == "Decimal");

        var shape = result.Response.Result.ShouldNotBeNull();
        shape.Shape.ShouldBe("List");
        shape.ElementType.ShouldBe("ProductDto");
        shape.ElementKind.ShouldBe("Dto");
    }

    [EndToEndFact]
    public async Task Include_and_ThenInclude_produce_the_navigation_joins()
    {
        var result = await RunAsync("include", "this.db.Orders", ".ToListAsync()");

        var command = SingleCommand(result);
        command.Sql.ShouldContain("FROM [Orders] AS [o]");
        command.Sql.ShouldContain("[OrderLines]");
        command.Sql.ShouldContain("[Products]");
        command.Sql.ShouldContain("[o].[CustomerId] =");

        command.Parameters.ShouldHaveSingleItem().Value.ShouldBe("7");

        var shape = result.Response.Result.ShouldNotBeNull();
        shape.Shape.ShouldBe("List");
        shape.ElementType.ShouldBe("Order");
        shape.ElementKind.ShouldBe("Entity");
    }

    [EndToEndFact]
    public async Task A_split_query_captures_every_command_it_issues()
    {
        var result = await RunAsync("split-query", "this.db.Orders", ".ToListAsync()");

        AssertSucceeded(result);

        // AsSplitQuery makes EF issue one command per collection navigation. Capturing only the first was the
        // spike's known failure; the wide synthetic reader is what lets the rest be issued at all.
        result.Response.Commands.Count.ShouldBeGreaterThan(1);
        result.Response.Commands[0].Sql.ShouldContain("[Orders]");
        result.Response.Commands.ShouldContain(c => c.Sql.Contains("[OrderLines]"));
    }

    [EndToEndFact]
    public async Task The_dialect_picker_re_renders_the_same_query_as_SQLite()
    {
        var result = await RunAsync(
            "dto-projection",
            "this.db.Products",
            ".ToListAsync()",
            request => request with { DialectOverride = EfProvider.Sqlite });

        var command = SingleCommand(result);
        result.Response.Provider.ShouldBe(EfProvider.Sqlite);

        // SQLite quotes identifiers rather than bracketing them.
        command.Sql.ShouldContain("\"Products\"");
        command.Sql.ShouldNotContain("[Products]");

        // The design-time factory hard-codes SQL Server, so the picker must go through the options constructor.
        result.Response.ActivationStrategy.ShouldBe("OptionsConstructor");
    }

    [EndToEndFact]
    public async Task A_free_variable_override_changes_the_captured_parameter_value()
    {
        var result = await RunAsync(
            "dto-projection",
            "this.db.Products",
            ".ToListAsync()",
            request => request with
            {
                FreeVariableOverrides = new Dictionary<string, string>(StringComparer.Ordinal) { ["minPrice"] = "250.75m" },
            });

        SingleCommand(result).Parameters.ShouldHaveSingleItem().Value.ShouldBe("250.75");
    }

    private async Task<PreviewResult> RunAsync(
        string scenario,
        string startsAt,
        string endsAfter,
        Func<PreviewRequest, PreviewRequest>? configure = null)
    {
        var request = new PreviewRequest(
            SampleShopQueries.Document,
            EndToEndEnvironment.SampleDocumentPath!,
            SampleShopQueries.Selection(scenario, startsAt, endsAfter))
        {
            // A cold NuGet restore on a machine that has never built EF Core can take minutes.
            Timeout = TimeSpan.FromMinutes(5),
        };

        if (configure is not null)
        {
            request = configure(request);
        }

        var result = await new PreviewRunner().RunAsync(request, CancellationToken.None);

        this.output.WriteLine(Describe(scenario, request.Selection, result));
        AssertSucceeded(result);
        return result;
    }

    private static CapturedCommand SingleCommand(PreviewResult result)
    {
        AssertSucceeded(result);
        result.Response.Commands.ShouldNotBeEmpty();
        return result.Response.Commands[0];
    }

    private static void AssertSucceeded(PreviewResult result)
    {
        if (result.Response.Success && result.Response.Commands.Count > 0)
        {
            return;
        }

        throw new Xunit.Sdk.XunitException(
            "The preview did not produce SQL.\n"
            + $"errorKind={result.Response.ErrorKind}\n"
            + $"error={result.Response.Error}\n"
            + $"exitCode={result.ExitCode}\n"
            + $"worker={result.Worker?.SourcePath}\n"
            + "stderr:\n" + Truncate(result.RawStandardError, 3000) + "\n"
            + "stdout:\n" + Truncate(result.RawStandardOutput, 6000));
    }

    private static string Describe(string scenario, TextSpan selection, PreviewResult result)
    {
        var text = new StringBuilder();
        text.AppendLine($"=== {scenario} ===");
        text.AppendLine($"selection: {SampleShopQueries.Document.Substring(selection.Start, selection.Length)}");
        text.AppendLine($"provider={result.Response.Provider} ef={result.Response.EfCoreVersion} context={result.Response.ContextType}");
        text.AppendLine($"activation={result.Response.ActivationStrategy} capture={result.Response.CaptureMode} elapsed={result.Elapsed.TotalSeconds:F1}s");

        for (var i = 0; i < result.Response.Commands.Count; i++)
        {
            var command = result.Response.Commands[i];
            text.AppendLine($"--- command {i + 1} of {result.Response.Commands.Count} ---");
            text.AppendLine(command.Sql);
            foreach (var parameter in command.Parameters)
            {
                text.AppendLine($"    param {parameter.Name} dbType={parameter.DbType} clr={parameter.ClrType} value={parameter.Value} isNull={parameter.IsNull}");
            }
        }

        if (result.Response.Result is { } shape)
        {
            text.AppendLine($"result: isAsync={shape.IsAsync} shape={shape.Shape} elementType={shape.ElementType} elementKind={shape.ElementKind} keyType={shape.KeyType}");
            text.AppendLine($"declared: {shape.DeclaredResultType}");
        }

        foreach (var warning in result.Response.Warnings)
        {
            text.AppendLine("warning: " + warning);
        }

        if (result.Response.Error is not null)
        {
            text.AppendLine("error: " + result.Response.Error);
        }

        return text.ToString();
    }

    private static string Truncate(string text, int max)
        => string.IsNullOrEmpty(text) ? "(empty)" : text.Length <= max ? text : text.Substring(0, max) + "\n... truncated ...";
}

/// <summary>
/// Serializes the end-to-end tests. Two concurrent <c>dotnet run</c> builds contend for the same MSBuild node
/// and artifact locks, which turns a fast warm run into an intermittent failure.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class EndToEndCollection
{
    /// <summary>The collection name.</summary>
    public const string Name = "EFCoreSqlPreview end-to-end";
}
