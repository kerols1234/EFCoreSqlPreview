using EFCoreSqlPreview.Core.Analysis;
using EFCoreSqlPreview.Core.Projects;
using Microsoft.CodeAnalysis.CSharp;

namespace EFCoreSqlPreview.Core.Tests;

/// <summary>
/// Guards the parts of the contract that are already real: the terminal catalog, the provider catalog and the
/// analyzer's parse options. Behavioural tests arrive with the implementations.
/// </summary>
public class ContractSmokeTests
{
    [Fact]
    public void ParseOptions_never_reject_newer_csharp()
    {
        LinqSelectionAnalyzer.ParseOptions.LanguageVersion.ShouldBe(LanguageVersion.Preview);
        LinqSelectionAnalyzer.ParseOptions.DocumentationMode.ShouldBe(Microsoft.CodeAnalysis.DocumentationMode.None);
    }

    [Fact]
    public void Terminal_catalog_covers_the_common_operators()
    {
        TerminalOperatorCatalog.Lookup("ToListAsync")!.Shape.ShouldBe(ResultShape.List);
        TerminalOperatorCatalog.Lookup("ToListAsync")!.IsAsync.ShouldBeTrue();
        TerminalOperatorCatalog.Lookup("ToList")!.IsAsync.ShouldBeFalse();
        TerminalOperatorCatalog.Lookup("FirstOrDefaultAsync")!.Shape.ShouldBe(ResultShape.FirstElement);
        TerminalOperatorCatalog.Lookup("ToDictionaryAsync")!.TakesKeySelectors.ShouldBeTrue();
        TerminalOperatorCatalog.Lookup("Contains")!.TakesValueArgument.ShouldBeTrue();
        TerminalOperatorCatalog.Lookup("Contains")!.TakesPredicate.ShouldBeFalse();
        TerminalOperatorCatalog.Lookup("AllAsync")!.TakesRequiredLambda.ShouldBeTrue();
        TerminalOperatorCatalog.Lookup("Where").ShouldBeNull();
        TerminalOperatorCatalog.IsDeferredOperator("Where").ShouldBeTrue();
    }

    [Fact]
    public void Aggregate_terminals_are_flagged_as_throwing_on_an_empty_reader()
    {
        foreach (var name in new[] { "CountAsync", "AnyAsync", "AllAsync", "MaxAsync", "MinAsync", "SumAsync", "AverageAsync", "LongCountAsync" })
        {
            TerminalOperatorCatalog.Lookup(name)!.ThrowsOnEmptyReader.ShouldBeTrue($"{name} materializes eagerly");
        }
    }

    [Fact]
    public void Provider_catalog_round_trips_package_ids()
    {
        EfProviders.All.Count.ShouldBe(5);
        EfProviders.FromPackageId("Microsoft.EntityFrameworkCore.SqlServer").ShouldBe(EfProvider.SqlServer);
        EfProviders.FromPackageId("npgsql.entityframeworkcore.postgresql").ShouldBe(EfProvider.PostgreSql);
        EfProviders.FromPackageId("Some.Other.Package").ShouldBe(EfProvider.Unknown);
        EfProviders.Describe(EfProvider.Sqlite)!.ConfigureCall.ShouldBe("UseSqlite({0})");
    }

    [Fact]
    public void TestSource_extracts_the_marked_selection()
    {
        var (text, span) = TestSource.Parse("var x = [|db.Products.ToList()|];");

        text.ShouldBe("var x = db.Products.ToList();");
        text.Substring(span.Start, span.Length).ShouldBe("db.Products.ToList()");
    }

    [Fact]
    public void TestSource_extracts_a_caret_position()
    {
        var (text, span) = TestSource.Caret("var x = db.Products.To$$List();");

        text.ShouldBe("var x = db.Products.ToList();");
        span.IsEmpty.ShouldBeTrue();
        span.Start.ShouldBe(22);
    }
}
