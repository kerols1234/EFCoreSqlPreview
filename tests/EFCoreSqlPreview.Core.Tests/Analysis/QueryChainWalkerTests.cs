using EFCoreSqlPreview.Core.Analysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFCoreSqlPreview.Core.Tests.Analysis;

/// <summary>
/// Covers decomposing an expression into its head receiver and the calls applied to it.
/// </summary>
public class QueryChainWalkerTests
{
    [Theory]
    [InlineData("_db.Products.Where(p => p.Id > 1).Select(p => p.Name).ToListAsync()", "Where,Select,ToListAsync")]
    [InlineData("_db.Set<Product>().Where(p => p.Id > 1).Max(p => p.Price)", "Set,Where,Max")]
    [InlineData("_db.Products.ProjectTo<ProductDto>().ToListAsync()", "ProjectTo,ToListAsync")]
    [InlineData("_db.Products.Include(p => p.Category).AsSplitQuery().ToListAsync()", "Include,AsSplitQuery,ToListAsync")]
    [InlineData("_db.Products.GroupBy(p => p.CategoryId).Select(g => g.Key).ToListAsync()", "GroupBy,Select,ToListAsync")]
    [InlineData("_db.Products.Cast<Product>().ToListAsync()", "Cast,ToListAsync")]
    [InlineData("_db.Products.ToList()", "ToList")]
    public void Walk_MethodChain_CollectsTheCallsHeadFirst(string expression, string expectedNames)
    {
        var chain = QueryChainWalker.Walk(Parse(expression));

        chain.Calls.Select(QueryChainWalker.CallName).ShouldBe(expectedNames.Split(','));
        chain.IsQuerySyntax.ShouldBeFalse();
    }

    [Fact]
    public void Walk_AwaitedChain_UnwrapsTheAwaitAndFlagsIt()
    {
        var chain = QueryChainWalker.Walk(Parse("await _db.Products.ToListAsync()"));

        chain.IsAwaited.ShouldBeTrue();
        chain.Head.ToString().ShouldBe("_db");
    }

    [Fact]
    public void Walk_UnawaitedChain_IsNotFlaggedAsAwaited()
        => QueryChainWalker.Walk(Parse("_db.Products.ToList()")).IsAwaited.ShouldBeFalse();

    [Fact]
    public void Walk_ThisQualifiedContext_StopsAtTheMemberAccess()
    {
        var chain = QueryChainWalker.Walk(Parse("this._db.Products.ToList()"));

        chain.Head.ToString().ShouldBe("this._db");
    }

    [Fact]
    public void Walk_ConditionalAccess_CollectsTheCallsAndKeepsTheReceiverAsTheHead()
    {
        var chain = QueryChainWalker.Walk(Parse("_db?.Products.Where(p => p.Id > 0).ToList()"));

        chain.Calls.Select(QueryChainWalker.CallName).ShouldBe(new[] { "Where", "ToList" });
        chain.Head.ToString().ShouldBe("_db");
    }

    [Fact]
    public void Walk_QuerySyntax_TakesTheFromClauseAsTheHead()
    {
        var chain = QueryChainWalker.Walk(Parse("from p in _db.Products select p"));

        chain.IsQuerySyntax.ShouldBeTrue();
        chain.Head.ToString().ShouldBe("_db.Products");
        chain.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void Walk_NullSuppressedReceiver_IsPeeled()
    {
        var chain = QueryChainWalker.Walk(Parse("_db!.Products.ToList()"));

        chain.Calls.Select(QueryChainWalker.CallName).ShouldBe(new[] { "ToList" });
    }

    [Fact]
    public void Walk_Null_Throws()
        => Should.Throw<ArgumentNullException>(() => QueryChainWalker.Walk(null!));

    [Theory]
    [InlineData("_db.Set<Product>()", "Product")]
    [InlineData("_db.Products.Cast<Product>()", "Product")]
    [InlineData("_db.Products.ProjectTo<ProductDto>()", "ProductDto")]
    public void TypeArguments_GenericCall_ReportsTheTypeArgument(string expression, string expected)
    {
        var chain = QueryChainWalker.Walk(Parse(expression));

        QueryChainWalker.TypeArguments(chain.Calls[^1]).ShouldBe(new[] { expected });
    }

    [Fact]
    public void TypeArguments_NonGenericCall_IsEmpty()
    {
        var chain = QueryChainWalker.Walk(Parse("_db.Products.ToList()"));

        QueryChainWalker.TypeArguments(chain.Calls[0]).ShouldBeEmpty();
    }

    [Fact]
    public void IsQueryShaped_ChainWithAKnownOperator_IsTrue()
        => QueryChainWalker.IsQueryShaped(QueryChainWalker.Walk(Parse("anything.Where(p => p)"))).ShouldBeTrue();

    [Fact]
    public void IsQueryShaped_QuerySyntax_IsTrue()
        => QueryChainWalker.IsQueryShaped(QueryChainWalker.Walk(Parse("from p in xs select p"))).ShouldBeTrue();

    [Fact]
    public void IsQueryShaped_PlainArithmetic_IsFalse()
        => QueryChainWalker.IsQueryShaped(QueryChainWalker.Walk(Parse("1 + 2"))).ShouldBeFalse();

    [Fact]
    public void IsQueryShaped_StringLiteral_IsFalse()
        => QueryChainWalker.IsQueryShaped(QueryChainWalker.Walk(Parse("\"_db.Products.ToList()\""))).ShouldBeFalse();

    [Fact]
    public void IsQueryShaped_Null_IsFalse()
        => QueryChainWalker.IsQueryShaped(null!).ShouldBeFalse();

    [Fact]
    public void Describe_Chain_ReportsNamesArgumentCountsAndTypeArguments()
    {
        var chain = QueryChainWalker.Walk(Parse("_db.Set<Product>().Where(p => p.Id > 1).ToList()"));

        var described = QueryChainWalker.Describe(chain);

        described.Select(c => c.Name).ShouldBe(new[] { "Set", "Where", "ToList" });
        described[0].TypeArguments.ShouldBe(new[] { "Product" });
        described[1].ArgumentCount.ShouldBe(1);
        described[2].ArgumentCount.ShouldBe(0);
    }

    [Fact]
    public void Describe_Null_IsEmpty()
        => QueryChainWalker.Describe(null!).ShouldBeEmpty();

    private static ExpressionSyntax Parse(string expression)
    {
        // The method is async so that 'await' always parses as an await expression.
        var text = "class C { object _db; async void M() { var x = " + expression + "; } }";
        return Fixture.ParseRoot(text)
            .DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .First(d => d.Identifier.ValueText == "x")
            .Initializer!.Value;
    }
}
