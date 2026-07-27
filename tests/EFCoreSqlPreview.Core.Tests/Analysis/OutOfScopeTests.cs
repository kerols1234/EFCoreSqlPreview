using EFCoreSqlPreview.Core.Analysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFCoreSqlPreview.Core.Tests.Analysis;

/// <summary>
/// Covers refusal of the write-side and raw-SQL constructs: EF Core SQL Preview is SELECT only.
/// </summary>
public class OutOfScopeTests
{
    [Theory]
    [InlineData("await _db.Products.Where(p => p.Id > 0).ExecuteDeleteAsync()", "ExecuteDeleteAsync", OutOfScopeReason.ExecuteDelete)]
    [InlineData("_db.Products.Where(p => p.Id > 0).ExecuteDelete()", "ExecuteDelete", OutOfScopeReason.ExecuteDelete)]
    [InlineData("await _db.Products.ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, \"x\"))", "ExecuteUpdateAsync", OutOfScopeReason.ExecuteUpdate)]
    [InlineData("_db.Products.ExecuteUpdate(s => s.SetProperty(p => p.Name, \"x\"))", "ExecuteUpdate", OutOfScopeReason.ExecuteUpdate)]
    [InlineData("await _db.SaveChangesAsync()", "SaveChangesAsync", OutOfScopeReason.SaveChanges)]
    [InlineData("_db.SaveChanges()", "SaveChanges", OutOfScopeReason.SaveChanges)]
    [InlineData("_db.Database.ExecuteSqlRaw(\"DELETE FROM P\")", "ExecuteSqlRaw", OutOfScopeReason.RawSql)]
    [InlineData("await _db.Database.ExecuteSqlRawAsync(\"DELETE FROM P\")", "ExecuteSqlRawAsync", OutOfScopeReason.RawSql)]
    [InlineData("_db.Database.ExecuteSqlInterpolated($\"DELETE FROM P\")", "ExecuteSqlInterpolated", OutOfScopeReason.RawSql)]
    public void Detect_WriteSideConstruct_BlocksTheRun(string expression, string method, OutOfScopeReason reason)
    {
        var result = Fixture.AnalyzeExpression(expression);

        result.Status.ShouldBe(AnalysisStatus.OutOfScope, result.Describe());
        result.OutOfScopeReason.ShouldBe(reason);
        result.OutOfScope.OffendingMethodName.ShouldBe(method);
        result.OutOfScope.IsHardBlock.ShouldBeTrue();
        result.CanRun.ShouldBeFalse();
        result.ShouldHaveDiagnostic(AnalysisDiagnosticIds.OutOfScopeConstruct);
    }

    [Fact]
    public void Detect_ExecuteDelete_ExplainsThatOnlySelectsArePreviewed()
    {
        var result = Fixture.AnalyzeExpression("await _db.Products.ExecuteDeleteAsync()");

        result.OutOfScope.Message.ShouldContain("SELECT queries only");
    }

    [Fact]
    public void Detect_ExecuteDelete_PointsAtTheOffendingMethodName()
    {
        var document = Fixture.Document(
            "        var x = [|await _db.Products.Where(p => p.Id > 0).ExecuteDeleteAsync()|];");
        var (text, span) = TestSource.Parse(document);

        var result = LinqSelectionAnalyzer.Analyze(text, span);

        text.Substring(result.OutOfScope.Span.Start, result.OutOfScope.Span.Length).ShouldBe("ExecuteDeleteAsync");
    }

    [Fact]
    public void Detect_FromSqlRawComposedIntoALongerChain_IsOnlyAWarning()
    {
        var result = Fixture.AnalyzeExpression(
            "_db.Products.FromSqlRaw(\"SELECT * FROM P\").Where(p => p.Id > 0).ToList()");

        result.OutOfScopeReason.ShouldBe(OutOfScopeReason.RawSql);
        result.OutOfScope.IsHardBlock.ShouldBeFalse();
        result.Status.ShouldBe(AnalysisStatus.PartiallyResolved);
        result.CanRun.ShouldBeTrue();
    }

    [Fact]
    public void Detect_FromSqlInterpolatedComposedIntoALongerChain_IsOnlyAWarning()
    {
        var result = Fixture.AnalyzeExpression(
            "await _db.Products.FromSqlInterpolated($\"SELECT * FROM P\").Where(p => p.Id > 0).ToListAsync()");

        result.OutOfScopeReason.ShouldBe(OutOfScopeReason.RawSql);
        result.OutOfScope.IsHardBlock.ShouldBeFalse();
    }

    [Fact]
    public void Detect_FromSqlRawAsTheWholeQuery_BlocksTheRun()
    {
        var result = Fixture.AnalyzeExpression("_db.Products.FromSqlRaw(\"SELECT * FROM P\")");

        result.OutOfScopeReason.ShouldBe(OutOfScopeReason.RawSql);
        result.OutOfScope.IsHardBlock.ShouldBeTrue();
        result.Status.ShouldBe(AnalysisStatus.OutOfScope);
    }

    [Fact]
    public void Detect_MutationOnADbSet_BlocksTheRun()
    {
        var result = Fixture.Analyze(
            "        var x = [|_db.Products.Add(product)|];",
            signature: Fixture.SyncSignature);

        result.OutOfScopeReason.ShouldBe(OutOfScopeReason.Mutation);
        result.Status.ShouldBe(AnalysisStatus.OutOfScope);
    }

    [Fact]
    public void Detect_AddOnAnUnrelatedReceiver_IsNotAMutation()
    {
        var text = "class C { void M(System.Collections.Generic.List<string> names) { names.Add(\"a\"); } }";
        var invocation = Fixture.FirstNode<InvocationExpressionSyntax>(text);
        var contextRoot = ContextRootInfo.Unresolved with { IdentifierName = "_db" };

        var found = OutOfScopeDetector.Detect(invocation, contextRoot, AnalyzerOptions.Default);

        found.IsOutOfScope.ShouldBeFalse();
    }

    [Fact]
    public void Detect_QueryUsingACollectionContains_IsInScope()
    {
        var result = Fixture.Analyze(
            "        var names = new List<string> { \"a\" };\r\n" +
            "        var x = [|_db.Products.Where(p => names.Contains(p.Name)).ToList()|];");

        result.OutOfScopeReason.ShouldBe(OutOfScopeReason.None);
        result.Status.ShouldNotBe(AnalysisStatus.OutOfScope);
    }

    [Fact]
    public void Detect_AdditionalOutOfScopeMethod_IsHonoured()
    {
        var options = AnalyzerOptions.Default with
        {
            AdditionalOutOfScopeMethods = new[] { "HouseBulkMerge" },
        };

        var result = Fixture.AnalyzeExpression("_db.Products.HouseBulkMerge()", options: options);

        result.OutOfScopeReason.ShouldBe(OutOfScopeReason.ThirdPartyBulk);
        result.Status.ShouldBe(AnalysisStatus.OutOfScope);
    }

    [Fact]
    public void Detect_BulkExtension_BlocksTheRun()
    {
        var result = Fixture.AnalyzeExpression("await _db.Products.BulkDeleteAsync()");

        result.OutOfScopeReason.ShouldBe(OutOfScopeReason.ThirdPartyBulk);
        result.OutOfScope.IsHardBlock.ShouldBeTrue();
    }

    [Fact]
    public void Detect_ExecuteDeleteNestedInsideALambda_BlocksTheRun()
    {
        var result = Fixture.AnalyzeExpression(
            "_db.Products.Where(p => p.Id > _db.Orders.ExecuteDelete()).ToList()");

        result.OutOfScopeReason.ShouldBe(OutOfScopeReason.ExecuteDelete);
        result.Status.ShouldBe(AnalysisStatus.OutOfScope);
    }

    [Fact]
    public void Detect_ExecuteDeleteInsideAReplayedPrologueStatement_BlocksTheRun()
    {
        var result = Fixture.Analyze(
            "        var q = _db.Products.AsQueryable();\r\n" +
            "        q = q.Where(p => p.Id > _db.Orders.ExecuteDelete());\r\n" +
            "        var x = [|q.ToList()|];");

        result.PrecedingStatements.Count.ShouldBe(2);
        result.OutOfScopeReason.ShouldBe(OutOfScopeReason.ExecuteDelete);
        result.Status.ShouldBe(AnalysisStatus.OutOfScope);
    }

    [Fact]
    public void Detect_PlainSelectQuery_IsInScope()
    {
        var result = Fixture.AnalyzeExpression("await _db.Products.Where(p => p.Id > 0).ToListAsync()");

        result.OutOfScopeReason.ShouldBe(OutOfScopeReason.None);
        result.OutOfScope.IsOutOfScope.ShouldBeFalse();
        result.OutOfScope.IsHardBlock.ShouldBeFalse();
        result.CanRun.ShouldBeTrue();
        result.Status.ShouldBe(AnalysisStatus.Success);
    }

    [Fact]
    public void KnownConstructs_CoverEveryDocumentedWriteSideName()
    {
        var expected = new[]
        {
            "ExecuteUpdate", "ExecuteUpdateAsync", "ExecuteDelete", "ExecuteDeleteAsync",
            "SaveChanges", "SaveChangesAsync",
            "FromSql", "FromSqlRaw", "FromSqlInterpolated",
            "ExecuteSql", "ExecuteSqlRaw", "ExecuteSqlInterpolated",
        };

        foreach (var name in expected)
        {
            OutOfScopeDetector.KnownConstructs.ContainsKey(name).ShouldBeTrue(name);
        }
    }
}
