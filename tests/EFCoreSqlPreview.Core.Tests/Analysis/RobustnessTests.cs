using EFCoreSqlPreview.Core.Analysis;
using Microsoft.CodeAnalysis.Text;

namespace EFCoreSqlPreview.Core.Tests.Analysis;

/// <summary>
/// The analyzer runs on whatever the user happened to select. None of these may throw.
/// </summary>
public class RobustnessTests
{
    [Fact]
    public void Analyze_EmptyDocument_ReportsNoQueryFound()
    {
        var result = LinqSelectionAnalyzer.Analyze(string.Empty, new TextSpan(0, 0));

        result.Status.ShouldBe(AnalysisStatus.NoQueryFound);
        result.FreeVariables.ShouldBeEmpty();
        result.Chain.ShouldBeEmpty();
    }

    [Fact]
    public void Analyze_NullDocument_ReportsNoQueryFound()
    {
        var result = LinqSelectionAnalyzer.Analyze(null!, new TextSpan(0, 0));

        result.Status.ShouldBe(AnalysisStatus.NoQueryFound);
    }

    [Fact]
    public void Analyze_WhitespaceOnlyDocument_ReportsNoQueryFound()
    {
        var result = LinqSelectionAnalyzer.Analyze("   \r\n   ", new TextSpan(0, 8));

        result.Status.ShouldBe(AnalysisStatus.NoQueryFound);
    }

    [Theory]
    [InlineData("")]
    [InlineData("}}}{{{")]
    [InlineData("/* unterminated")]
    [InlineData("var s = \"unterminated")]
    [InlineData("#if X\r\nvar a = 1;")]
    [InlineData("class C { void M() { _db.Products.ToList( } }")]
    [InlineData("public class")]
    public void Analyze_JunkDocument_DoesNotThrow(string document)
    {
        var span = new TextSpan(0, document.Length);

        var result = Should.NotThrow(() => LinqSelectionAnalyzer.Analyze(document, span));

        result.ShouldNotBeNull();
    }

    [Fact]
    public void Analyze_SelectionPastTheEndOfTheDocument_DoesNotThrow()
    {
        var (text, _) = TestSource.Parse(Fixture.Document("        var x = [|_db.Products.ToList()|];"));

        var result = Should.NotThrow(() => LinqSelectionAnalyzer.Analyze(text, new TextSpan(5, 100_000)));

        result.ShouldNotBeNull();
    }

    [Fact]
    public void Analyze_SelectionEntirelyBeyondTheDocument_DoesNotThrow()
    {
        var (text, _) = TestSource.Parse(Fixture.Document("        var x = [|_db.Products.ToList()|];"));

        var result = Should.NotThrow(() => LinqSelectionAnalyzer.Analyze(text, new TextSpan(text.Length + 500, 10)));

        result.ShouldNotBeNull();
    }

    [Fact]
    public void Analyze_SelectionEndingExactlyAtTheDocumentLength_DoesNotThrow()
    {
        var (text, _) = TestSource.Parse(Fixture.Document("        var x = [|_db.Products.ToList()|];"));

        var result = Should.NotThrow(() => LinqSelectionAnalyzer.Analyze(text, new TextSpan(0, text.Length)));

        result.ShouldNotBeNull();
    }

    [Fact]
    public void Analyze_SelectionInsideAStringLiteral_IsNotAQuery()
    {
        var result = Fixture.Analyze("        var s = \"[|_db.Products.ToList()|]\";");

        result.Status.ShouldBe(AnalysisStatus.NotAQuery);
        result.CanRun.ShouldBeFalse();
    }

    [Fact]
    public void Analyze_SelectionInsideALineComment_FindsNoQuery()
    {
        var result = Fixture.Analyze(
            "        var n = 1;\r\n" +
            "        // [|_db.Products.ToList()|]");

        result.Status.ShouldBeOneOf(AnalysisStatus.NoQueryFound, AnalysisStatus.NotAQuery);
        result.CanRun.ShouldBeFalse();
    }

    [Fact]
    public void Analyze_SelectionInsideABlockComment_FindsNoQuery()
    {
        var result = Fixture.Analyze(
            "        var n = 1;\r\n" +
            "        /* [|_db.Products.ToList()|] */");

        result.Status.ShouldBeOneOf(AnalysisStatus.NoQueryFound, AnalysisStatus.NotAQuery);
    }

    [Fact]
    public void Analyze_SelectionOnPlainArithmetic_IsNotAQuery()
    {
        var result = Fixture.Analyze("        var n = [|1 + 2|];");

        result.Status.ShouldBe(AnalysisStatus.NotAQuery);
    }

    [Fact]
    public void Analyze_BrokenSourceAroundTheQuery_IsPartiallyResolvedRatherThanFatal()
    {
        var document = Fixture.Document("        var x = [|_db.Products.Where(p => p.Id > 1).ToListAsync(|] }");

        var result = Fixture.AnalyzeRaw(document);

        result.Status.ShouldNotBe(AnalysisStatus.ParseFailure);
        result.ShouldNotBeNull();
    }

    [Fact]
    public void Analyze_EveryPrefixOfARealDocument_NeverThrows()
    {
        var (text, _) = TestSource.Parse(Fixture.Document(
            "        decimal min = 100m;\r\n" +
            "        var x = [|await _db.Products.Where(p => p.Price > min).ToListAsync()|];"));

        for (var start = 0; start < text.Length; start += 7)
        {
            foreach (var length in new[] { 0, 1, 5, 40, 200 })
            {
                var span = new TextSpan(start, Math.Min(length, Math.Max(0, text.Length - start)));
                Should.NotThrow(() => LinqSelectionAnalyzer.Analyze(text, span));
            }
        }
    }

    [Fact]
    public void Analyze_NullTree_ReportsParseFailure()
    {
        var result = LinqSelectionAnalyzer.Instance.Analyze(
            (Microsoft.CodeAnalysis.SyntaxTree)null!, new TextSpan(0, 0), AnalyzerOptions.Default);

        result.Status.ShouldBe(AnalysisStatus.ParseFailure);
    }

    [Fact]
    public void Analyze_NullOptions_FallsBackToTheDefaults()
    {
        var (text, span) = TestSource.Parse(Fixture.Document("        var x = [|_db.Products.ToList()|];"));

        var result = LinqSelectionAnalyzer.Instance.Analyze(text, span, null!);

        result.Status.ShouldBe(AnalysisStatus.Success);
    }

    [Fact]
    public void ParseOptions_UsePreviewLanguageVersionAndSkipDocumentation()
    {
        LinqSelectionAnalyzer.ParseOptions.LanguageVersion
            .ShouldBe(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Preview);
        LinqSelectionAnalyzer.ParseOptions.DocumentationMode
            .ShouldBe(Microsoft.CodeAnalysis.DocumentationMode.None);
    }

    [Fact]
    public void Analyze_ModernSyntaxTheProjectMayNotYetUse_StillParses()
    {
        var document =
            "using System.Linq;\r\n" +
            "\r\n" +
            "public class Service(AppDbContext db)\r\n" +
            "{\r\n" +
            "    private readonly int[] _ids = [1, 2, 3];\r\n" +
            "\r\n" +
            "    public void Run()\r\n" +
            "    {\r\n" +
            "        var x = [|db.Products.Where(p => _ids.Contains(p.Id)).ToList()|];\r\n" +
            "    }\r\n" +
            "}\r\n";

        var result = Fixture.AnalyzeRaw(document);

        result.Status.ShouldNotBe(AnalysisStatus.ParseFailure);
        result.Variable("_ids").ValueSource.ShouldBe(ValueSource.ConstructibleInitializer);
    }

    [Fact]
    public void Analyze_LargeDocument_CompletesQuickly()
    {
        var filler = string.Concat(Enumerable.Repeat("        var filler = 1;\r\n", 4000));
        var document = Fixture.Document(
            filler + "        var x = [|await _db.Products.Where(p => p.Id > 1).ToListAsync()|];");
        var (text, span) = TestSource.Parse(document);
        text.Length.ShouldBeGreaterThan(80_000);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = LinqSelectionAnalyzer.Analyze(text, span);
        stopwatch.Stop();

        result.TerminalOperator.Name.ShouldBe("ToListAsync");
        stopwatch.ElapsedMilliseconds.ShouldBeLessThan(2000);
    }
}
