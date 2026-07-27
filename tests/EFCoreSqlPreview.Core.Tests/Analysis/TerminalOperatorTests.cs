using EFCoreSqlPreview.Core.Analysis;

namespace EFCoreSqlPreview.Core.Tests.Analysis;

/// <summary>
/// Covers the terminal-operator table: shape, async-ness, argument classification and synthesis.
/// </summary>
public class TerminalOperatorTests
{
    public static TheoryData<string, string, ResultShape> SynchronousTerminals => new()
    {
        { "_db.Products.ToList()", "ToList", ResultShape.List },
        { "_db.Products.ToArray()", "ToArray", ResultShape.Array },
        { "_db.Products.ToHashSet()", "ToHashSet", ResultShape.HashSet },
        { "_db.Products.ToDictionary(p => p.Id)", "ToDictionary", ResultShape.Dictionary },
        { "_db.Products.ToDictionary(p => p.Id, p => p.Name)", "ToDictionary", ResultShape.Dictionary },
        { "_db.Products.ToLookup(p => p.CategoryId)", "ToLookup", ResultShape.Lookup },
        { "_db.Products.First()", "First", ResultShape.FirstElement },
        { "_db.Products.First(p => p.Id > 1)", "First", ResultShape.FirstElement },
        { "_db.Products.FirstOrDefault()", "FirstOrDefault", ResultShape.FirstElement },
        { "_db.Products.FirstOrDefault(p => p.Id > 1)", "FirstOrDefault", ResultShape.FirstElement },
        { "_db.Products.Single()", "Single", ResultShape.SingleElement },
        { "_db.Products.Single(p => p.Id > 1)", "Single", ResultShape.SingleElement },
        { "_db.Products.SingleOrDefault()", "SingleOrDefault", ResultShape.SingleElement },
        { "_db.Products.SingleOrDefault(p => p.Id > 1)", "SingleOrDefault", ResultShape.SingleElement },
        { "_db.Products.OrderBy(p => p.Id).Last()", "Last", ResultShape.LastElement },
        { "_db.Products.OrderBy(p => p.Id).LastOrDefault(p => p.Id > 1)", "LastOrDefault", ResultShape.LastElement },
        { "_db.Products.ElementAt(3)", "ElementAt", ResultShape.SingleElement },
        { "_db.Products.ElementAtOrDefault(3)", "ElementAtOrDefault", ResultShape.SingleElement },
        { "_db.Products.Count()", "Count", ResultShape.Scalar },
        { "_db.Products.Count(p => p.Id > 1)", "Count", ResultShape.Scalar },
        { "_db.Products.LongCount()", "LongCount", ResultShape.Scalar },
        { "_db.Products.Any()", "Any", ResultShape.Boolean },
        { "_db.Products.Any(p => p.Id > 1)", "Any", ResultShape.Boolean },
        { "_db.Products.All(p => p.Id > 1)", "All", ResultShape.Boolean },
        { "_db.Products.Sum(p => p.Price)", "Sum", ResultShape.Scalar },
        { "_db.Products.Average(p => p.Price)", "Average", ResultShape.Scalar },
        { "_db.Products.Min(p => p.Price)", "Min", ResultShape.Scalar },
        { "_db.Products.Max(p => p.Price)", "Max", ResultShape.Scalar },
        { "_db.Products.Load()", "Load", ResultShape.Void },
    };

    public static TheoryData<string, string, ResultShape> AsynchronousTerminals => new()
    {
        { "await _db.Products.ToListAsync()", "ToListAsync", ResultShape.List },
        { "await _db.Products.ToArrayAsync()", "ToArrayAsync", ResultShape.Array },
        { "await _db.Products.ToHashSetAsync()", "ToHashSetAsync", ResultShape.HashSet },
        { "await _db.Products.ToDictionaryAsync(p => p.Id, p => p.Name)", "ToDictionaryAsync", ResultShape.Dictionary },
        { "await _db.Products.FirstAsync()", "FirstAsync", ResultShape.FirstElement },
        { "await _db.Products.FirstOrDefaultAsync(p => p.Id == 42)", "FirstOrDefaultAsync", ResultShape.FirstElement },
        { "await _db.Products.SingleAsync()", "SingleAsync", ResultShape.SingleElement },
        { "await _db.Products.SingleOrDefaultAsync(p => p.Id == 42)", "SingleOrDefaultAsync", ResultShape.SingleElement },
        { "await _db.Products.OrderBy(p => p.Id).LastAsync()", "LastAsync", ResultShape.LastElement },
        { "await _db.Products.OrderBy(p => p.Id).LastOrDefaultAsync()", "LastOrDefaultAsync", ResultShape.LastElement },
        { "await _db.Products.ElementAtAsync(3)", "ElementAtAsync", ResultShape.SingleElement },
        { "await _db.Products.ElementAtOrDefaultAsync(3)", "ElementAtOrDefaultAsync", ResultShape.SingleElement },
        { "await _db.Products.CountAsync()", "CountAsync", ResultShape.Scalar },
        { "await _db.Products.LongCountAsync()", "LongCountAsync", ResultShape.Scalar },
        { "await _db.Products.AnyAsync(p => p.Id > 0)", "AnyAsync", ResultShape.Boolean },
        { "await _db.Products.AllAsync(p => p.Id > 0)", "AllAsync", ResultShape.Boolean },
        { "await _db.Products.SumAsync(p => p.Price)", "SumAsync", ResultShape.Scalar },
        { "await _db.Products.AverageAsync(p => p.Price)", "AverageAsync", ResultShape.Scalar },
        { "await _db.Products.MinAsync(p => p.Price)", "MinAsync", ResultShape.Scalar },
        { "await _db.Products.MaxAsync(p => p.Price)", "MaxAsync", ResultShape.Scalar },
        { "await _db.Products.ForEachAsync(p => Consume(p))", "ForEachAsync", ResultShape.Void },
        { "await _db.Products.LoadAsync()", "LoadAsync", ResultShape.Void },
    };

    [Theory]
    [MemberData(nameof(SynchronousTerminals))]
    public void Classify_SynchronousTerminal_ReportsNameAndShape(string expression, string name, ResultShape shape)
    {
        var result = Fixture.AnalyzeExpression(expression);

        result.TerminalOperator.Name.ShouldBe(name, result.Describe());
        result.TerminalOperator.Shape.ShouldBe(shape, result.Describe());
        result.TerminalOperator.IsAsync.ShouldBeFalse(result.Describe());
        result.TerminalOperator.Source.ShouldBe(TerminalSource.Catalog);
    }

    [Theory]
    [MemberData(nameof(AsynchronousTerminals))]
    public void Classify_AsynchronousTerminal_ReportsNameShapeAndAwaited(string expression, string name, ResultShape shape)
    {
        var result = Fixture.AnalyzeExpression(expression);

        result.TerminalOperator.Name.ShouldBe(name, result.Describe());
        result.TerminalOperator.Shape.ShouldBe(shape, result.Describe());
        result.TerminalOperator.IsAsync.ShouldBeTrue(result.Describe());
        result.TerminalOperator.IsAwaited.ShouldBeTrue(result.Describe());
        result.ShouldNotHaveDiagnostic(AnalysisDiagnosticIds.AsyncTerminalNotAwaited);
    }

    [Fact]
    public void Classify_ToDictionaryAsync_ExposesTheKeyAndElementSelectors()
    {
        var result = Fixture.AnalyzeExpression("await _db.Products.ToDictionaryAsync(p => p.Id, p => p.Name)");

        result.TerminalOperator.ArgumentCount.ShouldBe(2);
        result.TerminalOperator.DictionaryKeySelectorText.ShouldBe("p => p.Id");
        result.TerminalOperator.DictionaryValueSelectorText.ShouldBe("p => p.Name");
        result.TerminalOperator.Descriptor!.TakesKeySelectors.ShouldBeTrue();
    }

    [Fact]
    public void Classify_CountAsync_FlagsTheEmptyReaderThrow()
    {
        var result = Fixture.AnalyzeExpression("await _db.Products.CountAsync()");

        result.TerminalOperator.ThrowsOnEmptyReader.ShouldBeTrue();
    }

    [Fact]
    public void Classify_Count_DoesNotFlagTheEmptyReaderThrow()
    {
        var result = Fixture.AnalyzeExpression("_db.Products.Count()");

        result.TerminalOperator.ThrowsOnEmptyReader.ShouldBeFalse();
    }

    [Fact]
    public void Classify_AnyAsyncWithPredicate_ReportsAPredicateArgument()
    {
        var result = Fixture.AnalyzeExpression("await _db.Products.AnyAsync(p => p.Id > 0)");

        result.TerminalOperator.ArgumentCount.ShouldBe(1);
        result.TerminalOperator.HasPredicateArgument.ShouldBeTrue();
        result.TerminalOperator.Descriptor!.TakesPredicate.ShouldBeTrue();
    }

    [Fact]
    public void Classify_AllAsync_RequiresALambda()
    {
        var result = Fixture.AnalyzeExpression("await _db.Products.AllAsync(p => p.Id > 0)");

        result.TerminalOperator.Descriptor!.TakesRequiredLambda.ShouldBeTrue();
        result.TerminalOperator.Descriptor.TakesPredicate.ShouldBeFalse();
    }

    [Fact]
    public void Classify_ContainsAsync_TakesAValueNotAPredicate()
    {
        var result = Fixture.AnalyzeExpression("await _db.Products.ContainsAsync(someProduct)");

        result.TerminalOperator.Descriptor!.TakesValueArgument.ShouldBeTrue();
        result.TerminalOperator.Descriptor.TakesPredicate.ShouldBeFalse();
        result.TerminalOperator.HasPredicateArgument.ShouldBeFalse();
        result.VariableNames().ShouldContain("someProduct");
    }

    [Fact]
    public void Classify_NoTerminalOperator_SynthesizesToListAsyncAndReportsIt()
    {
        var result = Fixture.AnalyzeExpression("_db.Products.Where(p => p.Id > 0)");

        result.TerminalOperator.Shape.ShouldBe(ResultShape.DeferredQueryable);
        result.TerminalOperator.Source.ShouldBe(TerminalSource.Synthesized);
        result.TerminalOperator.Name.ShouldBeNull();
        result.TerminalOperator.SynthesizedTerminalText.ShouldBe(".ToListAsync()");
        result.ShouldHaveDiagnostic(AnalysisDiagnosticIds.NoTerminalOperator);
    }

    [Fact]
    public void Classify_NoTerminalWithoutEntityFrameworkImport_SynthesizesTheSynchronousForm()
    {
        var result = Fixture.Analyze(
            "        var x = [|_db.Products.Where(p => p.Id > 0)|];",
            signature: Fixture.SyncSignature,
            usings: "using System;\r\nusing System.Linq;");

        result.TerminalOperator.SynthesizedTerminalText.ShouldBe(".ToList()");
    }

    [Fact]
    public void Classify_AsyncTerminalNotAwaited_Warns()
    {
        var result = Fixture.AnalyzeExpression("_db.Products.ToListAsync()", signature: Fixture.SyncSignature);

        result.TerminalOperator.IsAsync.ShouldBeTrue();
        result.TerminalOperator.IsAwaited.ShouldBeFalse();
        result.ShouldHaveDiagnostic(AnalysisDiagnosticIds.AsyncTerminalNotAwaited);
    }

    [Fact]
    public void Classify_AwaitOnASynchronousTerminal_TreatsItAsAsync()
    {
        var result = Fixture.AnalyzeExpression("await _db.Products.ToList()");

        result.TerminalOperator.IsAsync.ShouldBeTrue();
        result.TerminalOperator.Source.ShouldBe(TerminalSource.InferredFromAwait);
        result.ShouldHaveDiagnostic(AnalysisDiagnosticIds.AwaitOnSyncTerminal);
    }

    [Fact]
    public void Classify_UnknownAsyncExtension_IsReportedAsACustomTerminal()
    {
        var result = Fixture.AnalyzeExpression("await _db.Products.MyCustomTerminalAsync()");

        result.TerminalOperator.Name.ShouldBe("MyCustomTerminalAsync");
        result.TerminalOperator.Source.ShouldBe(TerminalSource.CustomAsyncExtension);
        result.TerminalOperator.IsAsync.ShouldBeTrue();
        result.TerminalOperator.Shape.ShouldBe(ResultShape.Unknown);
        result.ShouldHaveDiagnostic(AnalysisDiagnosticIds.UnknownCustomAsyncTerminal);
    }

    [Fact]
    public void Classify_CustomSynchronousExtension_LeavesTheChainDeferred()
    {
        var result = Fixture.AnalyzeExpression("_db.Products.ActiveOnly()");

        result.TerminalOperator.Source.ShouldBe(TerminalSource.Synthesized);
        result.TerminalOperator.Shape.ShouldBe(ResultShape.DeferredQueryable);
    }

    [Fact]
    public void Classify_LastWithoutOrderBy_Warns()
    {
        var result = Fixture.AnalyzeExpression("await _db.Products.LastAsync()");

        result.TerminalOperator.Shape.ShouldBe(ResultShape.LastElement);
        result.ShouldHaveDiagnostic(AnalysisDiagnosticIds.LastWithoutOrderBy);
    }

    [Fact]
    public void Classify_LastWithOrderBy_DoesNotWarn()
    {
        var result = Fixture.AnalyzeExpression("await _db.Products.OrderBy(p => p.Name).LastAsync()");

        result.ShouldNotHaveDiagnostic(AnalysisDiagnosticIds.LastWithoutOrderBy);
    }

    [Fact]
    public void Classify_AsEnumerableBeforeToList_FlagsTheClientEvaluationBoundary()
    {
        var result = Fixture.AnalyzeExpression("_db.Products.Where(p => p.Id > 0).AsEnumerable().ToList()");

        result.TerminalOperator.Name.ShouldBe("ToList");
        result.ShouldHaveDiagnostic(AnalysisDiagnosticIds.ClientEvaluationBoundary);
    }

    [Fact]
    public void Classify_ToLookup_FlagsTheClientEvaluationBoundary()
    {
        var result = Fixture.AnalyzeExpression("_db.Products.ToLookup(p => p.CategoryId)");

        result.TerminalOperator.Shape.ShouldBe(ResultShape.Lookup);
        result.TerminalOperator.SynthesizedTerminalText.ShouldBe(".ToList()");
        result.ShouldHaveDiagnostic(AnalysisDiagnosticIds.ClientEvaluationBoundary);
    }

    [Fact]
    public void Classify_AsAsyncEnumerable_IsDeferredAndForcedToEnumerate()
    {
        var result = Fixture.AnalyzeExpression("_db.Products.AsAsyncEnumerable()", signature: Fixture.SyncSignature);

        result.TerminalOperator.Shape.ShouldBe(ResultShape.AsyncEnumerable);
        result.TerminalOperator.SynthesizedTerminalText.ShouldBe(".ToListAsync()");
        result.ShouldHaveDiagnostic(AnalysisDiagnosticIds.ClientEvaluationBoundary);
    }

    [Fact]
    public void Classify_AsSplitQuery_WarnsAboutMultipleCommands()
    {
        var result = Fixture.AnalyzeExpression(
            "await _db.Products.Include(p => p.Category).AsSplitQuery().ToListAsync()");

        result.ShouldHaveDiagnostic(AnalysisDiagnosticIds.MayProduceMultipleCommands);
    }

    [Fact]
    public void Classify_PlainInclude_DoesNotWarnAboutMultipleCommands()
    {
        var result = Fixture.AnalyzeExpression("await _db.Products.Include(p => p.Category).ToListAsync()");

        result.ShouldNotHaveDiagnostic(AnalysisDiagnosticIds.MayProduceMultipleCommands);
    }

    [Fact]
    public void Classify_ExplicitTerminalOverride_UsesTheConfiguredSynthesizedTerminal()
    {
        var options = AnalyzerOptions.Default with { SynthesizedTerminal = "ToArray" };

        var result = Fixture.AnalyzeExpression("_db.Products.Where(p => p.Id > 0)", options: options);

        result.TerminalOperator.SynthesizedTerminalText.ShouldBe(".ToArray()");
    }

    [Fact]
    public void Classify_TerminalSpan_PointsAtTheTerminalInvocation()
    {
        var document = Fixture.Document("        var x = [|await _db.Products.ToListAsync()|];");
        var (text, span) = TestSource.Parse(document);

        var result = LinqSelectionAnalyzer.Analyze(text, span);

        text.Substring(result.TerminalOperator.Span.Start, result.TerminalOperator.Span.Length)
            .ShouldBe("_db.Products.ToListAsync()");
    }
}
