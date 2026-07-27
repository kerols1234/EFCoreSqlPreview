using EFCoreSqlPreview.Core.Analysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFCoreSqlPreview.Core.Tests.Analysis;

/// <summary>
/// Covers the backward-liveness sweep that decides which preceding statements must be replayed.
/// </summary>
public class PrologueSlicerTests
{
    [Fact]
    public void Slice_KeepsOnlyTheStatementsThatFeedTheSeed()
    {
        var block = BlockOf(
            "var unrelated = 1;",
            "var q = _db.Products.AsQueryable();",
            "q = q.Where(p => p.Id > 1);",
            "var other = 2;",
            "var r = q.ToList();");

        var kept = PrologueSlicer.Slice(block, block.Statements[4], Seed("q"));

        kept.Select(s => s.ToString()).ShouldBe(new[]
        {
            "var q = _db.Products.AsQueryable();",
            "q = q.Where(p => p.Id > 1);",
        });
    }

    [Fact]
    public void Slice_KeepsAConditionalBuilderStatementVerbatim()
    {
        var block = BlockOf(
            "var q = _db.Products.AsQueryable();",
            "if (flag) { q = q.Where(p => p.Name == term); }",
            "var r = q.ToList();");

        var kept = PrologueSlicer.Slice(block, block.Statements[2], Seed("q"));

        kept.Count.ShouldBe(2);
        kept[1].ToString().ShouldContain("if (flag)");
    }

    [Fact]
    public void Slice_FollowsTransitivelyLiveNames()
    {
        var block = BlockOf(
            "var min = 5;",
            "var q = _db.Products.Where(p => p.Id > min);",
            "var r = q.ToList();");

        var kept = PrologueSlicer.Slice(block, block.Statements[2], Seed("q"));

        kept.Count.ShouldBe(2);
        kept[0].ToString().ShouldBe("var min = 5;");
    }

    [Fact]
    public void Slice_EmptySeed_KeepsNothing()
    {
        var block = BlockOf("var q = _db.Products.AsQueryable();", "var r = q.ToList();");

        PrologueSlicer.Slice(block, block.Statements[1], Seed()).ShouldBeEmpty();
    }

    [Fact]
    public void Slice_NullArguments_KeepNothing()
    {
        var block = BlockOf("var r = 1;");

        PrologueSlicer.Slice(null!, block.Statements[0], Seed("r")).ShouldBeEmpty();
        PrologueSlicer.Slice(block, null!, Seed("r")).ShouldBeEmpty();
        PrologueSlicer.Slice(block, block.Statements[0], null!).ShouldBeEmpty();
    }

    [Fact]
    public void Writes_ReportsDeclaratorsAssignmentsAndLoopVariables()
    {
        var block = BlockOf("var a = 1;", "b = 2;", "foreach (var c in xs) { }", "if (o is int d) { }");

        PrologueSlicer.Writes(block.Statements[0]).ShouldBe(Seed("a"), ignoreOrder: true);
        PrologueSlicer.Writes(block.Statements[1]).ShouldBe(Seed("b"), ignoreOrder: true);
        PrologueSlicer.Writes(block.Statements[2]).ShouldBe(Seed("c"), ignoreOrder: true);
        PrologueSlicer.Writes(block.Statements[3]).ShouldBe(Seed("d"), ignoreOrder: true);
    }

    [Fact]
    public void Writes_Null_IsEmpty()
        => PrologueSlicer.Writes(null!).ShouldBeEmpty();

    [Fact]
    public void Reads_SkipsMemberNamesInvocationTargetsLambdaParametersAndVar()
    {
        var block = BlockOf("var q = source.Where(p => p.Id > min).Select(Project);");

        var reads = PrologueSlicer.Reads(block.Statements[0]);

        reads.ShouldContain("source");
        reads.ShouldContain("min");
        reads.ShouldNotContain("p");
        reads.ShouldNotContain("Id");
        reads.ShouldNotContain("Where");
        reads.ShouldNotContain("var");
    }

    [Fact]
    public void Reads_Null_IsEmpty()
        => PrologueSlicer.Reads(null!).ShouldBeEmpty();

    private static HashSet<string> Seed(params string[] names)
        => new(names, StringComparer.Ordinal);

    private static BlockSyntax BlockOf(params string[] statements)
    {
        var text = "class C { void M() {\r\n" + string.Join("\r\n", statements) + "\r\n} }";
        return Fixture.ParseRoot(text).DescendantNodes().OfType<BlockSyntax>().First();
    }
}
