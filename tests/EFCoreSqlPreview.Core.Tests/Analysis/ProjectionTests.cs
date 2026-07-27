using EFCoreSqlPreview.Core.Analysis;

namespace EFCoreSqlPreview.Core.Tests.Analysis;

/// <summary>
/// Covers classification of the last <c>Select</c>/<c>SelectMany</c> in a chain.
/// </summary>
public class ProjectionTests
{
    [Fact]
    public void Classify_NoSelect_ReportsWholeEntities()
    {
        var result = Fixture.AnalyzeExpression("await _db.Products.ToListAsync()");

        result.Projection.Kind.ShouldBe(ProjectionKind.None);
        result.Projection.ElementKind.ShouldBe(ElementKind.Entity);
        result.ContextRoot.SourceSetName.ShouldBe("Products");
    }

    [Fact]
    public void Classify_ObjectInitializerDto_ReportsTheNamedTypeAndItsMembers()
    {
        var result = Fixture.AnalyzeExpression(
            "await _db.Products.Select(p => new ProductDto { Id = p.Id, Name = p.Name }).ToListAsync()");

        result.Projection.Kind.ShouldBe(ProjectionKind.NamedType);
        result.Projection.ProjectedTypeName.ShouldBe("ProductDto");
        result.Projection.ElementKind.ShouldBe(ElementKind.Dto);
        result.Projection.MemberNames.ShouldBe(new[] { "Id", "Name" });
    }

    [Fact]
    public void Classify_ConstructorDto_ReportsTheNamedType()
    {
        var result = Fixture.AnalyzeExpression(
            "await _db.Products.Select(p => new ProductDto(p.Id)).FirstOrDefaultAsync()");

        result.Projection.Kind.ShouldBe(ProjectionKind.NamedType);
        result.Projection.ProjectedTypeName.ShouldBe("ProductDto");
        result.TerminalOperator.Shape.ShouldBe(ResultShape.FirstElement);
    }

    [Fact]
    public void Classify_AnonymousType_ReportsAnonymousWithNoTypeName()
    {
        var result = Fixture.AnalyzeExpression(
            "await _db.Products.Select(p => new { p.Id, p.Name }).ToListAsync()");

        result.Projection.Kind.ShouldBe(ProjectionKind.Anonymous);
        result.Projection.ElementKind.ShouldBe(ElementKind.Anonymous);
        result.Projection.ProjectedTypeName.ShouldBeNull();
        result.Projection.MemberNames.ShouldBe(new[] { "Id", "Name" });
    }

    [Fact]
    public void Classify_SingleMember_ReportsAScalarNamedAfterTheMember()
    {
        var result = Fixture.AnalyzeExpression("await _db.Products.Select(p => p.Name).ToListAsync()");

        result.Projection.Kind.ShouldBe(ProjectionKind.ScalarMember);
        result.Projection.ProjectedTypeName.ShouldBe("Name");
        result.Projection.ElementKind.ShouldBe(ElementKind.Scalar);
    }

    [Fact]
    public void Classify_CastProjection_ReportsTheCastType()
    {
        var result = Fixture.AnalyzeExpression("await _db.Products.Select(p => (long)p.Id).ToListAsync()");

        result.Projection.Kind.ShouldBe(ProjectionKind.ScalarMember);
        result.Projection.ProjectedTypeName.ShouldBe("long");
    }

    [Fact]
    public void Classify_Tuple_ReportsATupleProjection()
    {
        var result = Fixture.AnalyzeExpression("await _db.Products.Select(p => (p.Id, p.Name)).ToListAsync()");

        result.Projection.Kind.ShouldBe(ProjectionKind.Tuple);
        result.Projection.ElementKind.ShouldBe(ElementKind.Tuple);
        result.Projection.MemberNames.ShouldBe(new[] { "Id", "Name" });
    }

    [Fact]
    public void Classify_ComputedExpression_ReportsComputed()
    {
        var result = Fixture.AnalyzeExpression("await _db.Products.Select(p => p.Price * 2).ToListAsync()");

        result.Projection.Kind.ShouldBe(ProjectionKind.Computed);
        result.Projection.ElementKind.ShouldBe(ElementKind.Scalar);
        result.Projection.ProjectedTypeName.ShouldBeNull();
    }

    [Fact]
    public void Classify_LiteralProjection_ReportsTheLiteralType()
    {
        var result = Fixture.AnalyzeExpression("await _db.Products.Select(p => 1).ToListAsync()");

        result.Projection.Kind.ShouldBe(ProjectionKind.ScalarMember);
        result.Projection.ProjectedTypeName.ShouldBe("int");
    }

    [Fact]
    public void Classify_SelectBeforeOtherOperators_StillFindsTheProjection()
    {
        var result = Fixture.AnalyzeExpression(
            "await _db.Products.Select(p => new ProductDto { Id = p.Id }).Where(x => x.Id > 0).CountAsync()");

        result.Projection.Kind.ShouldBe(ProjectionKind.NamedType);
        result.Projection.ProjectedTypeName.ShouldBe("ProductDto");
        result.TerminalOperator.Shape.ShouldBe(ResultShape.Scalar);
    }

    [Fact]
    public void Classify_TwoSelects_UsesTheLastOne()
    {
        var result = Fixture.AnalyzeExpression(
            "await _db.Products.Select(p => new ProductDto { Id = p.Id }).Select(d => d.Id).ToListAsync()");

        result.Projection.Kind.ShouldBe(ProjectionKind.ScalarMember);
        result.Projection.ProjectedTypeName.ShouldBe("Id");
    }

    [Fact]
    public void Classify_NestedSelectInsideAProjection_UsesTheOuterProjection()
    {
        var result = Fixture.AnalyzeExpression(
            "await _db.Products.Select(p => new ProductDto { Tags = p.Tags.Select(t => t.Name).ToList() }).ToListAsync()");

        result.Projection.Kind.ShouldBe(ProjectionKind.NamedType);
        result.Projection.ProjectedTypeName.ShouldBe("ProductDto");
        result.ChainNames().ShouldBe(new[] { "Select", "ToListAsync" });
    }

    [Fact]
    public void Classify_SelectMany_IsTreatedAsAProjection()
    {
        var result = Fixture.AnalyzeExpression(
            "await _db.Orders.SelectMany(o => o.Lines).Select(l => new LineDto { Id = l.Id }).ToListAsync()");

        result.Projection.Kind.ShouldBe(ProjectionKind.NamedType);
        result.Projection.ProjectedTypeName.ShouldBe("LineDto");
    }

    [Fact]
    public void Classify_ProjectTo_IsUnresolvableAndReportsTheMapperRequirement()
    {
        var result = Fixture.AnalyzeExpression("await _db.Products.ProjectTo<ProductDto>().ToListAsync()");

        result.Projection.Kind.ShouldBe(ProjectionKind.Unresolvable);
        result.Projection.ProjectedTypeName.ShouldBe("ProductDto");
        result.ShouldHaveDiagnostic(AnalysisDiagnosticIds.ProjectToRequiresMapper);
        result.HasErrors.ShouldBeTrue();
    }

    [Fact]
    public void Classify_GroupByThenSelect_MarksTheProjectionAsGrouped()
    {
        var result = Fixture.AnalyzeExpression(
            "await _db.Products.GroupBy(p => p.CategoryId).Select(g => new { g.Key, N = g.Count() }).ToListAsync()");

        result.Projection.Kind.ShouldBe(ProjectionKind.Anonymous);
        result.Projection.IsGroupedProjection.ShouldBeTrue();
    }

    [Fact]
    public void Classify_GroupByWithoutSelect_ReportsAGrouping()
    {
        var result = Fixture.AnalyzeExpression("await _db.Products.GroupBy(p => p.CategoryId).ToListAsync()");

        result.Projection.Kind.ShouldBe(ProjectionKind.Grouping);
        result.Projection.ElementKind.ShouldBe(ElementKind.Grouping);
    }

    [Fact]
    public void Classify_CastWithoutSelect_NamesTheCastElementType()
    {
        var result = Fixture.AnalyzeExpression("await _db.Products.Cast<Product>().ToListAsync()");

        result.Projection.Kind.ShouldBe(ProjectionKind.None);
        result.Projection.ProjectedTypeName.ShouldBe("Product");
    }

    [Fact]
    public void Classify_OfTypeAfterSelect_OverridesTheProjectedType()
    {
        var result = Fixture.AnalyzeExpression(
            "await _db.Products.Select(p => p.Category).OfType<SpecialCategory>().ToListAsync()");

        result.Projection.ProjectedTypeName.ShouldBe("SpecialCategory");
        result.Projection.ElementKind.ShouldBe(ElementKind.Entity);
    }

    [Fact]
    public void Classify_TargetTypedNew_ReportsNamedTypeWithNoNameAndWarns()
    {
        var result = Fixture.AnalyzeExpression("await _db.Products.Select(p => new(p.Id)).ToListAsync()");

        result.Projection.Kind.ShouldBe(ProjectionKind.NamedType);
        result.Projection.ProjectedTypeName.ShouldBeNull();
        result.ShouldHaveDiagnostic(AnalysisDiagnosticIds.TargetTypedNewNotResolvable);
    }

    [Fact]
    public void Classify_StatementLambdaProjection_IsUnresolvableAndWarns()
    {
        var result = Fixture.AnalyzeExpression(
            "await _db.Products.Select(p => { return p.Name; }).ToListAsync()");

        result.Projection.Kind.ShouldBe(ProjectionKind.Unresolvable);
        result.Projection.ElementKind.ShouldBe(ElementKind.Unknown);
        result.ShouldHaveDiagnostic(AnalysisDiagnosticIds.StatementLambdaProjection);
    }

    [Fact]
    public void Classify_QuerySyntaxSelectClause_ClassifiesLikeALambdaBody()
    {
        var result = Fixture.AnalyzeExpression(
            "from p in _db.Products select new ProductDto { Id = p.Id }");

        result.Projection.Kind.ShouldBe(ProjectionKind.NamedType);
        result.Projection.ProjectedTypeName.ShouldBe("ProductDto");
    }

    [Fact]
    public void Classify_QuerySyntaxGroupClause_ReportsAGrouping()
    {
        var result = Fixture.AnalyzeExpression("from p in _db.Products group p by p.CategoryId");

        result.Projection.Kind.ShouldBe(ProjectionKind.Grouping);
        result.Projection.IsGroupedProjection.ShouldBeTrue();
    }

    [Fact]
    public void Classify_ProjectionLambdaText_IsTheVerbatimSource()
    {
        var result = Fixture.AnalyzeExpression("await _db.Products.Select(p => p.Name).ToListAsync()");

        result.Projection.ProjectionLambdaText.ShouldBe("p => p.Name");
    }
}
