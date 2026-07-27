using EFCoreSqlPreview.Core.Analysis;

namespace EFCoreSqlPreview.Core.Tests.Analysis;

/// <summary>
/// Covers which identifiers count as free variables and what value each gets.
/// </summary>
public class FreeVariableTests
{
    [Fact]
    public void Collect_LiteralLocal_IsReproducedFromItsInitializer()
    {
        var result = Fixture.Analyze(
            "        decimal min = 100m;\r\n" +
            "        var x = [|_db.Products.Where(p => p.Price > min).ToList()|];");

        var min = result.Variable("min");
        min.Kind.ShouldBe(FreeVariableKind.ReproducibleLocal);
        min.DeclaredTypeName.ShouldBe("decimal");
        min.ValueSource.ShouldBe(ValueSource.LiteralInitializer);
        min.SuggestedValueExpression.ShouldBe("100m");
        min.SuggestedDeclaration.ShouldBe("decimal min = 100m;");
        min.RequiresUserValue.ShouldBeFalse();
        min.IsReproducible.ShouldBeTrue();
    }

    [Fact]
    public void Collect_ConstLocal_IsReproducedFromItsInitializer()
    {
        var result = Fixture.Analyze(
            "        const int cutoff = 5;\r\n" +
            "        var x = [|_db.Products.Where(p => p.Id > cutoff).ToList()|];");

        var cutoff = result.Variable("cutoff");
        cutoff.Kind.ShouldBe(FreeVariableKind.Constant);
        cutoff.ValueSource.ShouldBe(ValueSource.LiteralInitializer);
        cutoff.SuggestedValueExpression.ShouldBe("5");
    }

    [Fact]
    public void Collect_ListInitializerLocal_IsConstructible()
    {
        var result = Fixture.Analyze(
            "        var names = new List<string> { \"a\", \"b\" };\r\n" +
            "        var x = [|_db.Products.Where(p => names.Contains(p.Name)).ToList()|];");

        var names = result.Variable("names");
        names.ValueSource.ShouldBe(ValueSource.ConstructibleInitializer);
        names.SuggestedValueExpression.ShouldBe("new List<string> { \"a\", \"b\" }");
        names.DeclaredTypeName.ShouldBe("var");
        names.SuggestedDeclaration.ShouldBe("var names = new List<string> { \"a\", \"b\" };");
        names.RequiresUserValue.ShouldBeFalse();
    }

    [Fact]
    public void Collect_ImplicitArrayLocal_IsConstructible()
    {
        var result = Fixture.Analyze(
            "        var ids = new[] { 1, 2, 3 };\r\n" +
            "        var x = [|_db.Products.Where(p => ids.Contains(p.Id)).ToList()|];");

        result.Variable("ids").ValueSource.ShouldBe(ValueSource.ConstructibleInitializer);
        result.Variable("ids").SuggestedValueExpression.ShouldBe("new[] { 1, 2, 3 }");
    }

    [Fact]
    public void Collect_WellKnownStaticLocal_IsReproduced()
    {
        var result = Fixture.Analyze(
            "        var cutoff = DateTime.Now.AddDays(-30);\r\n" +
            "        var x = [|_db.Products.Where(p => p.Created > cutoff).ToList()|];");

        var cutoff = result.Variable("cutoff");
        cutoff.ValueSource.ShouldBe(ValueSource.WellKnownStatic);
        cutoff.SuggestedValueExpression.ShouldBe("DateTime.Now.AddDays(-30)");
        cutoff.RequiresUserValue.ShouldBeFalse();
    }

    [Fact]
    public void Collect_EnumMemberLocal_IsReproduced()
    {
        var result = Fixture.Analyze(
            "        var status = Status.Active;\r\n" +
            "        var x = [|_db.Products.Where(p => p.Status == status).ToList()|];");

        var status = result.Variable("status");
        status.ValueSource.ShouldBe(ValueSource.WellKnownStatic);
        status.SuggestedValueExpression.ShouldBe("Status.Active");
    }

    [Fact]
    public void Collect_ConstField_IsReproducedFromItsInitializer()
    {
        var result = Fixture.Analyze(
            "        var x = [|_db.Products.Where(p => p.Rank < Limit).ToList()|];",
            members: "    private const int Limit = 10;");

        var limit = result.Variable("Limit");
        limit.Kind.ShouldBe(FreeVariableKind.Constant);
        limit.SuggestedValueExpression.ShouldBe("10");
        limit.RequiresUserValue.ShouldBeFalse();
    }

    [Fact]
    public void Collect_StaticReadonlyField_IsReproducedFromItsInitializer()
    {
        var result = Fixture.Analyze(
            "        var x = [|_db.Products.Where(p => p.Rank < Limit).ToList()|];",
            members: "    private static readonly int Limit = 10;");

        var limit = result.Variable("Limit");
        limit.Kind.ShouldBe(FreeVariableKind.Field);
        limit.ValueSource.ShouldBe(ValueSource.LiteralInitializer);
        limit.SuggestedValueExpression.ShouldBe("10");
    }

    [Fact]
    public void Collect_InstanceFieldWithoutInitializer_NeedsAUserValue()
    {
        var result = Fixture.Analyze(
            "        var x = [|_db.Products.Where(p => p.Rank < _threshold).ToList()|];",
            members: "    private readonly int _threshold;");

        var threshold = result.Variable("_threshold");
        threshold.Kind.ShouldBe(FreeVariableKind.Field);
        threshold.ValueSource.ShouldBe(ValueSource.SynthesizedDefault);
        threshold.SuggestedValueExpression.ShouldBe("0");
        threshold.RequiresUserValue.ShouldBeTrue();
        result.ShouldHaveDiagnostic(AnalysisDiagnosticIds.FreeVariableNeedsValue);
    }

    [Fact]
    public void Collect_PropertyWithInitializer_IsReproduced()
    {
        var result = Fixture.Analyze(
            "        var x = [|_db.Products.Take(PageSize).ToList()|];",
            members: "    private int PageSize { get; } = 25;");

        var pageSize = result.Variable("PageSize");
        pageSize.Kind.ShouldBe(FreeVariableKind.Property);
        pageSize.SuggestedValueExpression.ShouldBe("25");
    }

    [Fact]
    public void Collect_MethodParameter_AlwaysNeedsAUserValue()
    {
        var result = Fixture.Analyze(
            "        var x = [|_db.Products.Where(p => ids.Contains(p.Id)).ToList()|];",
            signature: "public void Run(int[] ids)");

        var ids = result.Variable("ids");
        ids.Kind.ShouldBe(FreeVariableKind.Parameter);
        ids.DeclaredTypeName.ShouldBe("int[]");
        ids.ValueSource.ShouldBe(ValueSource.SynthesizedDefault);
        ids.SuggestedValueExpression.ShouldBe("Array.Empty<int>()");
        ids.RequiresUserValue.ShouldBeTrue();
        result.Status.ShouldBe(AnalysisStatus.PartiallyResolved);
    }

    [Fact]
    public void Collect_ParameterWithDefault_StillNeedsAUserValue()
    {
        var result = Fixture.Analyze(
            "        var x = [|_db.Products.Take(take).ToList()|];",
            signature: "public void Run(int take = 10)");

        result.Variable("take").ValueSource.ShouldBe(ValueSource.SynthesizedDefault);
        result.Variable("take").RequiresUserValue.ShouldBeTrue();
    }

    [Fact]
    public void Collect_LocalCapturedFromOutsideALambda_IsStillFound()
    {
        var result = Fixture.Analyze(
            "        decimal min = 100m;\r\n" +
            "        Func<Task> run = async () =>\r\n" +
            "        {\r\n" +
            "            var r = [|await _db.Products.Where(p => p.Price > min).ToListAsync()|];\r\n" +
            "        };");

        var min = result.Variable("min");
        min.ValueSource.ShouldBe(ValueSource.LiteralInitializer);
        min.SuggestedValueExpression.ShouldBe("100m");
    }

    [Fact]
    public void Collect_EnclosingLambdaParameter_IsReportedAsAParameter()
    {
        var result = Fixture.Analyze(
            "        Func<int, Task> run = async (int threshold) =>\r\n" +
            "        {\r\n" +
            "            var r = [|await _db.Products.Where(p => p.Id > threshold).ToListAsync()|];\r\n" +
            "        };");

        var threshold = result.Variable("threshold");
        threshold.Kind.ShouldBe(FreeVariableKind.Parameter);
        threshold.DeclaredTypeName.ShouldBe("int");
        threshold.RequiresUserValue.ShouldBeTrue();
    }

    [Fact]
    public void Collect_LambdaParameters_AreNeverFreeVariables()
    {
        var result = Fixture.AnalyzeExpression(
            "_db.Products.Where(p => p.Id > 1).Select(p => p.Name).ToList()");

        result.FreeVariables.ShouldBeEmpty(result.Describe());
    }

    [Fact]
    public void Collect_LambdaParameterShadowingALocal_IsNotReported()
    {
        var result = Fixture.Analyze(
            "        var p = 5;\r\n" +
            "        var x = [|_db.Products.Where(p => p.Id > 1).ToList()|];");

        result.VariableNames().ShouldNotContain("p");
    }

    [Fact]
    public void Collect_MemberNamesAfterADot_AreNotReported()
    {
        var result = Fixture.AnalyzeExpression("_db.Products.Where(p => p.Name == \"widget\").ToList()");

        result.VariableNames().ShouldNotContain("Name");
        result.VariableNames().ShouldNotContain("Products");
    }

    [Fact]
    public void Collect_EnumTypeNameOnTheLeftOfADot_IsSkippedAsAProbableType()
    {
        var result = Fixture.AnalyzeExpression("_db.Products.Where(p => p.Kind == (int)Kind.A).ToList()");

        result.VariableNames().ShouldNotContain("Kind");
        result.ShouldHaveDiagnostic(AnalysisDiagnosticIds.SkippedProbableTypeName);
    }

    [Fact]
    public void Collect_GenericTypeArguments_AreNotReported()
    {
        var result = Fixture.AnalyzeExpression("_db.Set<Product>().Where(p => p.Id > 0).ToList()");

        result.VariableNames().ShouldNotContain("Product");
        result.FreeVariables.ShouldBeEmpty(result.Describe());
    }

    [Fact]
    public void Collect_ObjectCreationTypeName_IsNotReported()
    {
        var result = Fixture.AnalyzeExpression(
            "_db.Products.Select(p => new ProductDto { Id = p.Id }).ToList()");

        result.VariableNames().ShouldNotContain("ProductDto");
        result.VariableNames().ShouldNotContain("Id");
    }

    [Fact]
    public void Collect_UnresolvedIdentifier_NeedsBothATypeAndAValue()
    {
        var result = Fixture.AnalyzeExpression(
            "_db.Products.Where(p => p.OwnerId == owner.Id).ToList()");

        var owner = result.Variable("owner");
        owner.Kind.ShouldBe(FreeVariableKind.Unresolved);
        owner.DeclaredTypeName.ShouldBeNull();
        owner.ValueSource.ShouldBe(ValueSource.SynthesizedDefault);
        owner.RequiresUserValue.ShouldBeTrue();
        owner.SuggestedDeclaration.ShouldBeNull();
        result.ShouldHaveDiagnostic(AnalysisDiagnosticIds.FreeVariableNeedsValue);
        result.ShouldHaveDiagnostic(AnalysisDiagnosticIds.FreeVariableTypeUnknown);
    }

    [Fact]
    public void Collect_OpaqueLocal_NeedsAUserValue()
    {
        var result = Fixture.Analyze(
            "        var svc = GetService();\r\n" +
            "        var x = [|_db.Products.Where(p => p.OwnerId == svc.Id).ToList()|];");

        var svc = result.Variable("svc");
        svc.Kind.ShouldBe(FreeVariableKind.OpaqueLocal);
        svc.ValueSource.ShouldBe(ValueSource.SynthesizedDefault);
        svc.RequiresUserValue.ShouldBeTrue();
        result.ShouldHaveDiagnostic(AnalysisDiagnosticIds.FreeVariableTypeUnknown);
    }

    [Fact]
    public void Collect_TransitiveLocal_EmitsTheDependencyFirst()
    {
        var result = Fixture.Analyze(
            "        var a = 1;\r\n" +
            "        var b = a + 1;\r\n" +
            "        var x = [|_db.Products.Where(p => p.Id == b).ToList()|];");

        result.VariableNames().ShouldBe(new[] { "a", "b" });
        result.Variable("b").ValueSource.ShouldBe(ValueSource.TransitiveLocal);
        result.Variable("b").SuggestedValueExpression.ShouldBe("a + 1");
        result.Variable("a").ValueSource.ShouldBe(ValueSource.LiteralInitializer);
    }

    [Fact]
    public void Collect_InterpolatedStringOverAReproducibleLocal_StaysALiteral()
    {
        var result = Fixture.Analyze(
            "        string term = \"widget\";\r\n" +
            "        var pattern = $\"%{term}%\";\r\n" +
            "        var x = [|_db.Products.Where(p => EF.Functions.Like(p.Name, pattern)).ToList()|];");

        result.Variable("pattern").ValueSource.ShouldBe(ValueSource.LiteralInitializer);
        result.Variable("term").ValueSource.ShouldBe(ValueSource.LiteralInitializer);
    }

    [Fact]
    public void Collect_InterpolatedStringOverAnOpaqueLocal_NeedsAUserValue()
    {
        var result = Fixture.Analyze(
            "        var term = GetTerm();\r\n" +
            "        var pattern = $\"%{term}%\";\r\n" +
            "        var x = [|_db.Products.Where(p => p.Name == pattern).ToList()|];");

        var pattern = result.Variable("pattern");
        pattern.ValueSource.ShouldBe(ValueSource.SynthesizedDefault);
        pattern.RequiresUserValue.ShouldBeTrue();
    }

    [Fact]
    public void Collect_UserOverride_WinsOverTheInferredValue()
    {
        var options = AnalyzerOptions.Default with
        {
            FreeVariableOverrides = new Dictionary<string, string>(StringComparer.Ordinal) { ["min"] = "250m" },
        };

        var result = Fixture.Analyze(
            "        decimal min = 100m;\r\n" +
            "        var x = [|_db.Products.Where(p => p.Price > min).ToList()|];",
            options: options);

        var min = result.Variable("min");
        min.ValueSource.ShouldBe(ValueSource.UserSupplied);
        min.SuggestedValueExpression.ShouldBe("250m");
        min.RequiresUserValue.ShouldBeFalse();
        result.Status.ShouldBe(AnalysisStatus.Success);
    }

    [Fact]
    public void Collect_UserOverride_SatisfiesAnOtherwiseUnresolvedVariable()
    {
        var options = AnalyzerOptions.Default with
        {
            FreeVariableOverrides = new Dictionary<string, string>(StringComparer.Ordinal) { ["ids"] = "new[] { 1, 2 }" },
        };

        var result = Fixture.Analyze(
            "        var x = [|_db.Products.Where(p => ids.Contains(p.Id)).ToList()|];",
            signature: "public void Run(int[] ids)",
            options: options);

        result.Variable("ids").RequiresUserValue.ShouldBeFalse();
        result.RequiresUserInput.ShouldBeFalse();
    }

    [Fact]
    public void Collect_TheContextRoot_IsNeverAFreeVariable()
    {
        var result = Fixture.AnalyzeExpression("_db.Products.ToList()");

        result.VariableNames().ShouldNotContain("_db");
    }

    [Fact]
    public void Collect_TheContextRootUsedOnlyInThePrologue_IsStillNotAFreeVariable()
    {
        var result = Fixture.Analyze(
            "        var q = _db.Products.AsQueryable();\r\n" +
            "        var x = [|q.ToList()|];");

        result.VariableNames().ShouldNotContain("_db");
        result.VariableNames().ShouldNotContain("q");
    }

    [Fact]
    public void Collect_RepeatedUsages_AreReportedOnce_WithEveryUsageSpan()
    {
        var result = Fixture.Analyze(
            "        int min = 1;\r\n" +
            "        var x = [|_db.Products.Where(p => p.Id > min && p.Rank > min).ToList()|];");

        result.VariableNames().ShouldBe(new[] { "min" });
        result.Variable("min").UsageSpans.Count.ShouldBe(2);
    }

    [Fact]
    public void Collect_QueryRangeVariables_AreNotFreeVariables()
    {
        var result = Fixture.AnalyzeExpression(
            "from p in _db.Products let n = p.Name where n != null select n");

        result.VariableNames().ShouldNotContain("p");
        result.VariableNames().ShouldNotContain("n");
    }

    [Fact]
    public void Collect_PatternDesignationsInsideTheQuery_AreNotFreeVariables()
    {
        var result = Fixture.AnalyzeExpression(
            "_db.Products.Where(p => p.Category is Category c && c.Id > 0).ToList()");

        result.VariableNames().ShouldNotContain("c");
        result.VariableNames().ShouldNotContain("Category");
    }
}
