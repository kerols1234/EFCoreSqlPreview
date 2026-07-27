using EFCoreSqlPreview.Core.Analysis;
using Microsoft.CodeAnalysis.Text;

namespace EFCoreSqlPreview.Core.Tests.Analysis;

/// <summary>
/// Covers turning a raw editor selection into the query expression it refers to.
/// </summary>
public class SelectionResolutionTests
{
    [Fact]
    public void Analyze_ExactExpressionSelected_ResolvesThatExpression()
    {
        var result = Fixture.Analyze("        var x = [|_db.Products.Where(p => p.Id > 1).ToListAsync()|];");

        result.QueryExpression.ShouldBe("_db.Products.Where(p => p.Id > 1).ToListAsync()");
        result.TerminalOperator.Name.ShouldBe("ToListAsync");
        result.TerminalOperator.IsAwaited.ShouldBeFalse();
    }

    [Fact]
    public void Analyze_PartialMidChainSelection_ExpandsToTheWholeChain()
    {
        var result = Fixture.Analyze(
            "        var x = await _db.Products[|.Where(p => p.Id > 1)|].Select(p => p.Name).ToListAsync();");

        result.ChainNames().ShouldBe(new[] { "Where", "Select", "ToListAsync" });
        result.TerminalOperator.IsAwaited.ShouldBeTrue();
        result.QueryExpression.ShouldStartWith("await ");
    }

    [Fact]
    public void Analyze_SelectionIncludesAwait_ReportsAwaited()
    {
        var result = Fixture.Analyze("        var x = [|await _db.Products.ToListAsync()|];");

        result.TerminalOperator.IsAwaited.ShouldBeTrue();
        result.TerminalOperator.IsAsync.ShouldBeTrue();
        result.QueryExpression.ShouldBe("await _db.Products.ToListAsync()");
    }

    [Fact]
    public void Analyze_WholeLocalDeclarationStatement_ResolvesToTheInitializerOnly()
    {
        var result = Fixture.Analyze("        [|var items = await _db.Products.ToListAsync();|]");

        result.QueryExpression.ShouldBe("await _db.Products.ToListAsync()");
        result.Status.ShouldBe(AnalysisStatus.Success);
    }

    [Fact]
    public void Analyze_LocalDeclarationWithoutSemicolon_ResolvesThroughVariableDeclaration()
    {
        var result = Fixture.Analyze("        [|var items = await _db.Products.ToListAsync()|];");

        result.QueryExpression.ShouldBe("await _db.Products.ToListAsync()");
        result.Status.ShouldBe(AnalysisStatus.Success);
    }

    [Fact]
    public void Analyze_SelectionWithTrailingNewlineAndBlankLine_DoesNotPromoteToTheEnclosingBlock()
    {
        // Regression: an untrimmed trailing newline makes FindNode return the whole Block, which turns a
        // single-statement selection into a multi-statement one and replays the neighbouring declaration
        // instead of reporting it as a free variable.
        var result = Fixture.Analyze(
            "        decimal min = 100m;\r\n" +
            "        [|var items = await _db.Products.Where(p => p.Price > min).ToListAsync();\r\n\r\n|]" +
            "        var z = 5;");

        result.QueryExpression.ShouldBe("await _db.Products.Where(p => p.Price > min).ToListAsync()");
        result.PrecedingStatements.ShouldBeEmpty();
        result.Variable("min").SuggestedValueExpression.ShouldBe("100m");
    }

    [Fact]
    public void Analyze_SelectionWithLeadingWhitespace_TrimsBeforeResolving()
    {
        var result = Fixture.Analyze("        [|   await _db.Products.ToListAsync()|];");

        result.QueryExpression.ShouldBe("await _db.Products.ToListAsync()");
    }

    [Fact]
    public void Analyze_CaretInsideAToken_ResolvesTheWholeQuery()
    {
        var result = Fixture.AnalyzeCaretRaw(
            Fixture.Document("        var x = await _db.Products.To$$ListAsync();"));

        result.TerminalOperator.Name.ShouldBe("ToListAsync");
        result.QueryExpression.ShouldBe("await _db.Products.ToListAsync()");
    }

    [Fact]
    public void Analyze_CaretInsideAChainOperator_ResolvesTheWholeQuery()
    {
        var result = Fixture.AnalyzeCaretRaw(
            Fixture.Document("        var x = await _db.Products.Wh$$ere(p => p.Id > 1).ToListAsync();"));

        result.ChainNames().ShouldBe(new[] { "Where", "ToListAsync" });
    }

    [Fact]
    public void Analyze_SelectionInsideLambdaBody_AscendsToTheWholeQuery()
    {
        var result = Fixture.Analyze(
            "        decimal min = 100m;\r\n" +
            "        var x = await _db.Products.Where(p => [|p.Price > min|]).ToListAsync();");

        result.QueryExpression.ShouldBe("await _db.Products.Where(p => p.Price > min).ToListAsync()");
        result.VariableNames().ShouldContain("min");
    }

    [Fact]
    public void Analyze_SelectionInsideObjectInitializer_AscendsToTheWholeQuery()
    {
        var result = Fixture.Analyze(
            "        var x = await _db.Products.Select(p => new Dto { [|Id = p.Id|] }).ToListAsync();");

        result.Projection.ProjectedTypeName.ShouldBe("Dto");
        result.QueryExpression.ShouldStartWith("await _db.Products.Select");
    }

    [Fact]
    public void Analyze_ParenthesizedAwait_PeelsTheParentheses()
    {
        var result = Fixture.Analyze("        var x = [|(await _db.Products.ToListAsync())|];");

        result.QueryExpression.ShouldBe("await _db.Products.ToListAsync()");
        result.TerminalOperator.IsAwaited.ShouldBeTrue();
    }

    [Fact]
    public void Analyze_AssignmentStatement_PeelsToTheRightHandSide()
    {
        var result = Fixture.Analyze(
            "        var q = _db.Products.AsQueryable();\r\n" +
            "        [|q = q.Where(p => p.Id > 1);|]");

        result.QueryExpression.ShouldBe("q.Where(p => p.Id > 1)");
    }

    [Fact]
    public void Analyze_ReturnStatement_DescendsToTheReturnedExpression()
    {
        var result = Fixture.Analyze(
            "        [|return await _db.Products.ToListAsync();|]",
            signature: "public async Task<List<Product>> RunAsync()");

        result.QueryExpression.ShouldBe("await _db.Products.ToListAsync()");
    }

    [Fact]
    public void Analyze_ForEachStatement_DescendsToTheSourceExpression()
    {
        var result = Fixture.Analyze("        [|foreach (var x in _db.Products.Where(p => p.Id > 1)) { }|]");

        result.QueryExpression.ShouldBe("_db.Products.Where(p => p.Id > 1)");
    }

    [Fact]
    public void Analyze_MultiStatementSelection_AnchorsOnTheLastQueryAndSlicesThePrologue()
    {
        var result = Fixture.Analyze(
            "        [|var q = _db.Products.AsQueryable();\r\n" +
            "        q = q.Where(p => p.Id > 1);\r\n" +
            "        var r = await q.ToListAsync();|]");

        result.QueryExpression.ShouldBe("await q.ToListAsync()");
        result.PrecedingStatements.Count.ShouldBe(2);
        result.ContextRoot.RootIdentifier.ShouldBe("_db");
        result.ContextRoot.Kind.ShouldBe(ContextRootKind.LocalQuery);
    }

    [Fact]
    public void Analyze_QueryBuiltThroughLocal_KeepsOnlyTheStatementsThatFeedIt()
    {
        var result = Fixture.Analyze(
            "        var unrelated = 1;\r\n" +
            "        var q = _db.Products.AsQueryable();\r\n" +
            "        q = q.Where(p => p.Id > 1);\r\n" +
            "        if (flag) { q = q.Where(p => p.Name == term); }\r\n" +
            "        var other = 2;\r\n" +
            "        var r = [|await q.OrderBy(p => p.Name).ToListAsync()|];",
            signature: "public async Task RunAsync(bool flag, string term)");

        result.PrecedingStatements.Count.ShouldBe(3);
        result.PrecedingStatements[0].ShouldContain("AsQueryable");
        result.PrecedingStatements[2].ShouldContain("if (flag)");
        result.PrecedingStatements.ShouldAllBe(s => !s.Contains("unrelated") && !s.Contains("other"));
        result.VariableNames().ShouldBe(new[] { "flag", "term" });
    }

    [Fact]
    public void Analyze_PrologueStatements_RewriteTheContextRootToTheWorkerIdentifier()
    {
        var result = Fixture.Analyze(
            "        var q = this._db.Products.AsQueryable();\r\n" +
            "        var r = [|await q.ToListAsync()|];");

        result.PrecedingStatements.Count.ShouldBe(1);
        result.PrecedingStatements[0].ShouldBe("var q = " + LinqSelectionAnalyzer.ContextPlaceholder + ".Products.AsQueryable();");
    }

    [Fact]
    public void Analyze_SingleStatementSelection_DoesNotReplayNeighbouringDeclarations()
    {
        var result = Fixture.Analyze(
            "        decimal min = 100m;\r\n" +
            "        var x = [|await _db.Products.Where(p => p.Price > min).ToListAsync()|];");

        result.PrecedingStatements.ShouldBeEmpty();
        result.Variable("min").ValueSource.ShouldBe(ValueSource.LiteralInitializer);
    }

    [Fact]
    public void Analyze_TwoDeclaratorsInOneStatement_PicksTheIntersectingOneAndWarns()
    {
        var result = Fixture.Analyze(
            "        [|var a = _db.Products.ToList(), b = _db.Categories.ToList();|]",
            signature: "public void Run()");

        result.QueryExpression.ShouldBe("_db.Categories.ToList()");
        result.ShouldHaveDiagnostic(AnalysisDiagnosticIds.MultipleDeclaratorsInSelection);
    }

    [Fact]
    public void Analyze_SelectionCrossingALineComment_StillResolvesTheWholeChain()
    {
        var result = Fixture.Analyze(
            "        var x = [|await _db.Products // only the cheap ones\r\n" +
            "            .Where(p => p.Id > 1)\r\n" +
            "            .ToListAsync()|];");

        result.ChainNames().ShouldBe(new[] { "Where", "ToListAsync" });
    }

    [Fact]
    public void Analyze_QuerySyntax_IsRecognisedAsAQueryExpression()
    {
        var result = Fixture.Analyze(
            "        var x = [|from p in _db.Products where p.Id > 0 select new { p.Id, p.Name }|];");

        result.Projection.Kind.ShouldBe(ProjectionKind.Anonymous);
        result.TerminalOperator.Shape.ShouldBe(ResultShape.DeferredQueryable);
        result.TerminalOperator.Source.ShouldBe(TerminalSource.Synthesized);
        result.ContextRoot.RootIdentifier.ShouldBe("_db");
        result.VariableNames().ShouldNotContain("p");
    }

    [Fact]
    public void Analyze_CaretInsideAQueryClause_ResolvesTheWholeQueryExpression()
    {
        var result = Fixture.AnalyzeCaretRaw(Fixture.Document(
            "        var x = from p in _db.Products wh$$ere p.Id > 0 select p;"));

        result.QueryExpression.ShouldStartWith("from p in _db.Products");
    }

    [Fact]
    public void Analyze_TopLevelStatements_ResolvesWithoutANamespaceOrClass()
    {
        var document =
            "using System.Linq;\r\n" +
            "using Microsoft.EntityFrameworkCore;\r\n" +
            "\r\n" +
            "var db = new AppDbContext();\r\n" +
            "var items = [|db.Products.ToList()|];\r\n";

        var result = Fixture.AnalyzeRaw(document);

        result.Namespace.ShouldBeNull();
        result.QueryExpression.ShouldBe("db.Products.ToList()");
        result.ContextRoot.DbContextTypeName.ShouldBe("AppDbContext");
    }

    [Fact]
    public void Analyze_SetOfBrokenSource_StillExtractsTheRecoverableChain()
    {
        var document = Fixture.Document("        var x = [|_db.Products.Where(p => p.Id > 1).ToListAsync(  |]}");

        var result = Fixture.AnalyzeRaw(document);

        result.Status.ShouldNotBe(AnalysisStatus.ParseFailure);
        result.ChainNames().ShouldContain("Where");
    }

    [Fact]
    public void Analyze_SameSelectionTwice_ProducesEqualResults()
    {
        var body = "        var x = [|await _db.Products.Where(p => p.Id > 1).ToListAsync()|];";

        var first = Fixture.Analyze(body);
        var second = Fixture.Analyze(body);

        first.QueryExpression.ShouldBe(second.QueryExpression);
        first.Status.ShouldBe(second.Status);
        first.ChainNames().ShouldBe(second.ChainNames());
    }

    [Fact]
    public void Analyze_WithParsedTree_MatchesTheStringOverload()
    {
        var (text, span) = TestSource.Parse(
            Fixture.Document("        var x = [|await _db.Products.ToListAsync()|];"));
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(text, LinqSelectionAnalyzer.ParseOptions);

        var fromTree = LinqSelectionAnalyzer.Instance.Analyze(tree, span, AnalyzerOptions.Default);
        var fromText = LinqSelectionAnalyzer.Analyze(text, span);

        fromTree.QueryExpression.ShouldBe(fromText.QueryExpression);
        fromTree.Status.ShouldBe(fromText.Status);
    }

    [Fact]
    public void SelectionSpan_IsTheNormalizedSpan_NotTheRawOne()
    {
        var document = Fixture.Document("        [|var items = await _db.Products.ToListAsync();   |]");
        var (text, raw) = TestSource.Parse(document);

        var result = LinqSelectionAnalyzer.Analyze(text, raw);

        result.SelectionSpan.End.ShouldBeLessThan(raw.End);
        result.SelectionSpan.ShouldBe(SelectionNormalizer.Normalize(text, raw));
    }

    [Fact]
    public void QuerySpan_PointsAtTheResolvedExpressionInTheDocument()
    {
        var document = Fixture.Document("        var x = [|await _db.Products.ToListAsync()|];");
        var (text, span) = TestSource.Parse(document);

        var result = LinqSelectionAnalyzer.Analyze(text, span);

        text.Substring(result.QuerySpan.Start, result.QuerySpan.Length)
            .ShouldBe("await _db.Products.ToListAsync()");
    }
}
