using EFCoreSqlPreview.Core.Analysis;

namespace EFCoreSqlPreview.Core.Tests.Analysis;

/// <summary>
/// Covers identification of the DbContext a query hangs off and of the DbSet it starts from.
/// </summary>
public class ContextRootTests
{
    [Fact]
    public void Resolve_FieldContext_ReportsTheDeclaredTypeAndDbSet()
    {
        var result = Fixture.AnalyzeExpression("_context.Products.ToList()");

        result.ContextRoot.Kind.ShouldBe(ContextRootKind.Identifier);
        result.ContextRoot.RootExpressionText.ShouldBe("_context");
        result.ContextRoot.RootIdentifier.ShouldBe("_context");
        result.ContextRoot.DbContextTypeName.ShouldBe("AppDbContext");
        result.ContextRoot.Confidence.ShouldBe(ContextTypeConfidence.Declared);
        result.ContextRoot.Resolution.ShouldBe(ContextResolution.Field);
        result.ContextRoot.SourceSetName.ShouldBe("Products");
    }

    [Fact]
    public void Resolve_ThisQualifiedField_ReportsMemberAccessOnThis()
    {
        var result = Fixture.AnalyzeExpression("await this._db.Products.CountAsync()");

        result.ContextRoot.Kind.ShouldBe(ContextRootKind.MemberAccessOnThis);
        result.ContextRoot.RootExpressionText.ShouldBe("this._db");
        result.ContextRoot.RootIdentifier.ShouldBe("_db");
        result.ContextRoot.DbContextTypeName.ShouldBe("AppDbContext");
    }

    [Fact]
    public void Resolve_ThisQualifiedField_NormalizesToTheWorkerIdentifier()
    {
        var result = Fixture.AnalyzeExpression("await this._db.Products.CountAsync()");

        result.NormalizedQueryText
            .ShouldBe("await " + LinqSelectionAnalyzer.ContextPlaceholder + ".Products.CountAsync()");
    }

    [Fact]
    public void Resolve_SetOfT_ReportsTheTypeArgumentInsteadOfADbSetName()
    {
        var result = Fixture.Analyze(
            "        var x = [|context.Set<Product>().Where(p => p.Id > 0).ToList()|];",
            signature: "public void Run(AppDbContext context)");

        result.ContextRoot.SourceSetName.ShouldBeNull();
        result.ContextRoot.SourceSetTypeArgument.ShouldBe("Product");
        result.ContextRoot.Resolution.ShouldBe(ContextResolution.Parameter);
        result.ContextRoot.DbContextTypeName.ShouldBe("AppDbContext");
    }

    [Fact]
    public void Resolve_PrimaryConstructorParameter_ResolvesTheContextType()
    {
        var document =
            "using System.Linq;\r\n" +
            "using Microsoft.EntityFrameworkCore;\r\n" +
            "\r\n" +
            "namespace Demo;\r\n" +
            "\r\n" +
            "public class Service(AppDbContext db)\r\n" +
            "{\r\n" +
            "    public void Run()\r\n" +
            "    {\r\n" +
            "        var x = [|db.Products.ToList()|];\r\n" +
            "    }\r\n" +
            "}\r\n";

        var result = Fixture.AnalyzeRaw(document);

        result.ContextRoot.DbContextTypeName.ShouldBe("AppDbContext");
        result.ContextRoot.Resolution.ShouldBe(ContextResolution.PrimaryConstructorParameter);
    }

    [Fact]
    public void Resolve_LocalFromAnotherQuery_WalksBackToTheRealContext()
    {
        var result = Fixture.Analyze(
            "        var q = _db.Products.AsQueryable();\r\n" +
            "        var x = [|q.Where(p => p.Id > 0).ToList()|];");

        result.ContextRoot.Kind.ShouldBe(ContextRootKind.LocalQuery);
        result.ContextRoot.Resolution.ShouldBe(ContextResolution.LocalQuery);
        result.ContextRoot.RootIdentifier.ShouldBe("_db");
        result.ContextRoot.DbContextTypeName.ShouldBe("AppDbContext");
        result.PrecedingStatements.Count.ShouldBe(1);
    }

    [Fact]
    public void Resolve_LocalFromAnotherQuery_LeavesTheQueryTextAlone()
    {
        var result = Fixture.Analyze(
            "        var q = _db.Products.AsQueryable();\r\n" +
            "        var x = [|q.Where(p => p.Id > 0).ToList()|];");

        result.NormalizedQueryText.ShouldBe("q.Where(p => p.Id > 0).ToList()");
    }

    [Fact]
    public void Resolve_LocalFromAFactoryCall_IsUnknownAndReportsIt()
    {
        var result = Fixture.Analyze(
            "        var ctx = GetContext();\r\n" +
            "        var x = [|ctx.Products.ToList()|];");

        result.ContextRoot.Confidence.ShouldBe(ContextTypeConfidence.Unknown);
        result.ContextRoot.DbContextTypeName.ShouldBeNull();
        result.ShouldHaveDiagnostic(AnalysisDiagnosticIds.ContextTypeNotResolvableSyntactically);
        result.Status.ShouldBe(AnalysisStatus.PartiallyResolved);
    }

    [Fact]
    public void Resolve_LocalConstructedInline_InfersTheTypeFromTheInitializer()
    {
        var result = Fixture.Analyze(
            "        var ctx = new AppDbContext(opts);\r\n" +
            "        var x = [|ctx.Products.ToList()|];");

        result.ContextRoot.Confidence.ShouldBe(ContextTypeConfidence.InferredFromInitializer);
        result.ContextRoot.DbContextTypeName.ShouldBe("AppDbContext");
    }

    [Fact]
    public void Resolve_NullConditionalContext_DropsTheConditionalAndReportsIt()
    {
        var result = Fixture.AnalyzeExpression("_db?.Products.Where(p => p.Id > 0).ToList()");

        result.ContextRoot.RootIdentifier.ShouldBe("_db");
        result.ContextRoot.SourceSetName.ShouldBe("Products");
        result.ChainNames().ShouldBe(new[] { "Where", "ToList" });
        result.ShouldHaveDiagnostic(AnalysisDiagnosticIds.ConditionalAccessOnContext);
    }

    [Fact]
    public void Resolve_HeadThatIsNotAnIdentifier_IsUnresolvedAndReportsIt()
    {
        var result = Fixture.Analyze(
            "        var x = [|(flag ? _db : _context).Products.ToList()|];",
            signature: "public void Run(bool flag)");

        result.ContextRoot.Kind.ShouldBe(ContextRootKind.Unresolved);
        result.ShouldHaveDiagnostic(AnalysisDiagnosticIds.ContextRootUnresolved);
        result.Status.ShouldBe(AnalysisStatus.PartiallyResolved);
    }

    [Fact]
    public void Resolve_ContextDeclaredInABaseClass_IsUnknownWithoutAnOverride()
    {
        var document =
            "using System.Linq;\r\n" +
            "\r\n" +
            "public class Service : BaseService\r\n" +
            "{\r\n" +
            "    public void Run()\r\n" +
            "    {\r\n" +
            "        var x = [|Db.Products.ToList()|];\r\n" +
            "    }\r\n" +
            "}\r\n";

        var result = Fixture.AnalyzeRaw(document);

        result.ContextRoot.Confidence.ShouldBe(ContextTypeConfidence.Unknown);
        result.ShouldHaveDiagnostic(AnalysisDiagnosticIds.ContextTypeNotResolvableSyntactically);
    }

    [Fact]
    public void Resolve_ContextDeclaredInABaseClass_UsesTheOverrideWhenSupplied()
    {
        var document =
            "using System.Linq;\r\n" +
            "\r\n" +
            "public class Service : BaseService\r\n" +
            "{\r\n" +
            "    public void Run()\r\n" +
            "    {\r\n" +
            "        var x = [|Db.Products.ToList()|];\r\n" +
            "    }\r\n" +
            "}\r\n";

        var result = Fixture.AnalyzeRaw(document, AnalyzerOptions.Default with { DbContextTypeOverride = "AppDbContext" });

        result.ContextRoot.DbContextTypeName.ShouldBe("AppDbContext");
        result.ContextRoot.Confidence.ShouldBe(ContextTypeConfidence.Declared);
        result.ShouldNotHaveDiagnostic(AnalysisDiagnosticIds.ContextTypeNotResolvableSyntactically);
    }

    [Fact]
    public void Resolve_InterfaceTypedContext_IsFlaggedAsMaybeAnInterface()
    {
        var document =
            "using System.Linq;\r\n" +
            "\r\n" +
            "public class Service\r\n" +
            "{\r\n" +
            "    private readonly IAppDbContext _db;\r\n" +
            "\r\n" +
            "    public void Run()\r\n" +
            "    {\r\n" +
            "        var x = [|_db.Products.ToList()|];\r\n" +
            "    }\r\n" +
            "}\r\n";

        var result = Fixture.AnalyzeRaw(document);

        result.ContextRoot.Confidence.ShouldBe(ContextTypeConfidence.DeclaredInterface);
        result.ContextRoot.DbContextTypeName.ShouldBe("IAppDbContext");
        result.ShouldHaveDiagnostic(AnalysisDiagnosticIds.ContextMayBeInterface);
    }

    [Fact]
    public void Resolve_QualifiedField_TakesTheRightmostName()
    {
        var result = Fixture.Analyze(
            "        var x = [|_uow.Context.Products.ToList()|];",
            members: "    private readonly UnitOfWork _uow;");

        result.ContextRoot.Kind.ShouldBe(ContextRootKind.QualifiedField);
        result.ContextRoot.RootExpressionText.ShouldBe("_uow.Context");
        result.ContextRoot.RootIdentifier.ShouldBe("Context");
        result.ContextRoot.SourceSetName.ShouldBe("Products");
    }

    [Fact]
    public void Resolve_QualifiedField_NormalizesTheWholeDottedPath()
    {
        var result = Fixture.Analyze(
            "        var x = [|_uow.Context.Products.ToList()|];",
            members: "    private readonly UnitOfWork _uow;");

        result.NormalizedQueryText
            .ShouldBe(LinqSelectionAnalyzer.ContextPlaceholder + ".Products.ToList()");
    }

    [Fact]
    public void Resolve_QuerySyntaxSource_ReportsTheDbSetOfTheFromClause()
    {
        var result = Fixture.AnalyzeExpression("from p in _db.Products where p.Id > 0 select p");

        result.ContextRoot.RootIdentifier.ShouldBe("_db");
        result.ContextRoot.SourceSetName.ShouldBe("Products");
    }

    [Fact]
    public void Resolve_ContextRootSpan_PointsAtTheHeadExpression()
    {
        var document = Fixture.Document("        var x = [|_db.Products.ToList()|];");
        var (text, span) = TestSource.Parse(document);

        var result = LinqSelectionAnalyzer.Analyze(text, span);

        text.Substring(result.ContextRoot.Span.Start, result.ContextRoot.Span.Length).ShouldBe("_db");
    }

    [Fact]
    public void Resolve_NormalizedQueryText_SubstitutesTheContextIdentifier()
    {
        var result = Fixture.AnalyzeExpression("_db.Products.Where(p => p.Id > 0).ToList()");

        result.NormalizedQueryText
            .ShouldBe(LinqSelectionAnalyzer.ContextPlaceholder + ".Products.Where(p => p.Id > 0).ToList()");
    }

    // A query-syntax join names the context once per source. Rewriting only the chain head left `this._db`
    // in the worker, where there is no enclosing instance, and the build failed with CS0026.
    [Fact]
    public void Resolve_QuerySyntaxJoin_SubstitutesEveryContextReference()
    {
        var result = Fixture.AnalyzeExpression(
            "from o in this._db.Orders join c in this._db.Customers on o.CustomerId equals c.Id select c.Name");

        var context = LinqSelectionAnalyzer.ContextPlaceholder;
        result.NormalizedQueryText.ShouldBe(
            $"from o in {context}.Orders join c in {context}.Customers on o.CustomerId equals c.Id select c.Name");
        result.NormalizedQueryText.ShouldNotContain("this.");
    }

    [Fact]
    public void Resolve_CorrelatedSubqueryOnTheSameContext_SubstitutesEveryContextReference()
    {
        var result = Fixture.AnalyzeExpression(
            "_db.Orders.Where(o => _db.Customers.Any(c => c.Id == o.CustomerId)).ToList()");

        var context = LinqSelectionAnalyzer.ContextPlaceholder;
        result.NormalizedQueryText.ShouldBe(
            $"{context}.Orders.Where(o => {context}.Customers.Any(c => c.Id == o.CustomerId)).ToList()");
    }
}
