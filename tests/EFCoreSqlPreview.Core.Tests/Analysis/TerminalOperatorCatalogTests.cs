using EFCoreSqlPreview.Core.Analysis;

namespace EFCoreSqlPreview.Core.Tests.Analysis;

/// <summary>
/// Covers the static facts the catalog asserts about each terminal operator name.
/// </summary>
public class TerminalOperatorCatalogTests
{
    [Theory]
    [InlineData("ToList", false, ResultShape.List)]
    [InlineData("ToListAsync", true, ResultShape.List)]
    [InlineData("ToArray", false, ResultShape.Array)]
    [InlineData("ToArrayAsync", true, ResultShape.Array)]
    [InlineData("ToDictionary", false, ResultShape.Dictionary)]
    [InlineData("ToDictionaryAsync", true, ResultShape.Dictionary)]
    [InlineData("ToHashSet", false, ResultShape.HashSet)]
    [InlineData("ToHashSetAsync", true, ResultShape.HashSet)]
    [InlineData("ToLookup", false, ResultShape.Lookup)]
    [InlineData("AsEnumerable", false, ResultShape.DeferredEnumerable)]
    [InlineData("AsAsyncEnumerable", true, ResultShape.AsyncEnumerable)]
    [InlineData("First", false, ResultShape.FirstElement)]
    [InlineData("FirstAsync", true, ResultShape.FirstElement)]
    [InlineData("FirstOrDefault", false, ResultShape.FirstElement)]
    [InlineData("FirstOrDefaultAsync", true, ResultShape.FirstElement)]
    [InlineData("Single", false, ResultShape.SingleElement)]
    [InlineData("SingleAsync", true, ResultShape.SingleElement)]
    [InlineData("SingleOrDefault", false, ResultShape.SingleElement)]
    [InlineData("SingleOrDefaultAsync", true, ResultShape.SingleElement)]
    [InlineData("Last", false, ResultShape.LastElement)]
    [InlineData("LastAsync", true, ResultShape.LastElement)]
    [InlineData("LastOrDefault", false, ResultShape.LastElement)]
    [InlineData("LastOrDefaultAsync", true, ResultShape.LastElement)]
    [InlineData("ElementAt", false, ResultShape.SingleElement)]
    [InlineData("ElementAtAsync", true, ResultShape.SingleElement)]
    [InlineData("ElementAtOrDefault", false, ResultShape.SingleElement)]
    [InlineData("ElementAtOrDefaultAsync", true, ResultShape.SingleElement)]
    [InlineData("Count", false, ResultShape.Scalar)]
    [InlineData("CountAsync", true, ResultShape.Scalar)]
    [InlineData("LongCount", false, ResultShape.Scalar)]
    [InlineData("LongCountAsync", true, ResultShape.Scalar)]
    [InlineData("Any", false, ResultShape.Boolean)]
    [InlineData("AnyAsync", true, ResultShape.Boolean)]
    [InlineData("All", false, ResultShape.Boolean)]
    [InlineData("AllAsync", true, ResultShape.Boolean)]
    [InlineData("Sum", false, ResultShape.Scalar)]
    [InlineData("SumAsync", true, ResultShape.Scalar)]
    [InlineData("Average", false, ResultShape.Scalar)]
    [InlineData("AverageAsync", true, ResultShape.Scalar)]
    [InlineData("Min", false, ResultShape.Scalar)]
    [InlineData("MinAsync", true, ResultShape.Scalar)]
    [InlineData("Max", false, ResultShape.Scalar)]
    [InlineData("MaxAsync", true, ResultShape.Scalar)]
    [InlineData("Contains", false, ResultShape.Boolean)]
    [InlineData("ContainsAsync", true, ResultShape.Boolean)]
    [InlineData("ForEachAsync", true, ResultShape.Void)]
    [InlineData("Load", false, ResultShape.Void)]
    [InlineData("LoadAsync", true, ResultShape.Void)]
    public void Lookup_CataloguedTerminal_ReportsAsyncnessAndShape(string name, bool isAsync, ResultShape shape)
    {
        var descriptor = TerminalOperatorCatalog.Lookup(name);

        descriptor.ShouldNotBeNull();
        descriptor!.IsAsync.ShouldBe(isAsync);
        descriptor.Shape.ShouldBe(shape);
        descriptor.Name.ShouldBe(name);
    }

    [Theory]
    [InlineData("CountAsync")]
    [InlineData("LongCountAsync")]
    [InlineData("AnyAsync")]
    [InlineData("AllAsync")]
    [InlineData("SumAsync")]
    [InlineData("AverageAsync")]
    [InlineData("MinAsync")]
    [InlineData("MaxAsync")]
    [InlineData("ContainsAsync")]
    public void Lookup_AggregateAsyncTerminal_ThrowsOnAnEmptyReader(string name)
        => TerminalOperatorCatalog.Lookup(name)!.ThrowsOnEmptyReader.ShouldBeTrue();

    [Theory]
    [InlineData("ToListAsync")]
    [InlineData("FirstOrDefaultAsync")]
    [InlineData("ToDictionaryAsync")]
    [InlineData("Count")]
    public void Lookup_NonAggregateTerminal_DoesNotThrowOnAnEmptyReader(string name)
        => TerminalOperatorCatalog.Lookup(name)!.ThrowsOnEmptyReader.ShouldBeFalse();

    [Theory]
    [InlineData("Where")]
    [InlineData("Select")]
    [InlineData("Include")]
    [InlineData("ThenInclude")]
    [InlineData("AsNoTracking")]
    [InlineData("AsSplitQuery")]
    [InlineData("OrderBy")]
    [InlineData("Skip")]
    [InlineData("Take")]
    [InlineData("GroupBy")]
    [InlineData("Cast")]
    [InlineData("OfType")]
    [InlineData("Set")]
    [InlineData("AsQueryable")]
    [InlineData("IgnoreQueryFilters")]
    [InlineData("TagWith")]
    public void IsDeferredOperator_KnownDeferredOperator_IsTrue(string name)
    {
        TerminalOperatorCatalog.IsDeferredOperator(name).ShouldBeTrue();
        TerminalOperatorCatalog.Lookup(name).ShouldBeNull();
    }

    [Fact]
    public void Lookup_UnknownName_IsNull()
        => TerminalOperatorCatalog.Lookup("MyCustomTerminalAsync").ShouldBeNull();

    [Fact]
    public void Lookup_Null_IsNull()
        => TerminalOperatorCatalog.Lookup(null).ShouldBeNull();

    [Fact]
    public void IsDeferredOperator_Null_IsFalse()
        => TerminalOperatorCatalog.IsDeferredOperator(null).ShouldBeFalse();

    [Fact]
    public void All_ContainsNoOverlapWithTheDeferredSet()
        => TerminalOperatorCatalog.All.Keys
            .Where(TerminalOperatorCatalog.IsDeferredOperator)
            .ShouldBeEmpty();

    [Fact]
    public void All_ExposesEveryDescriptorUnderItsOwnName()
        => TerminalOperatorCatalog.All.ShouldAllBe(pair => pair.Key == pair.Value.Name);

    [Fact]
    public void Lookup_All_TakesAKeySelector()
    {
        TerminalOperatorCatalog.Lookup("ToDictionary")!.TakesKeySelectors.ShouldBeTrue();
        TerminalOperatorCatalog.Lookup("ToLookup")!.TakesKeySelectors.ShouldBeTrue();
    }

    [Fact]
    public void Lookup_ValueArgumentTerminals_DoNotTakeAPredicate()
    {
        foreach (var name in new[] { "Contains", "ContainsAsync", "ElementAt", "ElementAtAsync" })
        {
            var descriptor = TerminalOperatorCatalog.Lookup(name)!;
            descriptor.TakesValueArgument.ShouldBeTrue(name);
            descriptor.TakesPredicate.ShouldBeFalse(name);
        }
    }

    [Fact]
    public void Lookup_RequiredLambdaTerminals_AreMarkedAsSuch()
    {
        TerminalOperatorCatalog.Lookup("All")!.TakesRequiredLambda.ShouldBeTrue();
        TerminalOperatorCatalog.Lookup("AllAsync")!.TakesRequiredLambda.ShouldBeTrue();
        TerminalOperatorCatalog.Lookup("ForEachAsync")!.TakesRequiredLambda.ShouldBeTrue();
    }
}
