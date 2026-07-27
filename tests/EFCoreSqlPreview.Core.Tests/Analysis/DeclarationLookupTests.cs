using EFCoreSqlPreview.Core.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFCoreSqlPreview.Core.Tests.Analysis;

/// <summary>
/// Covers the syntax-only declaration search: locals, parameters, fields, properties and primary constructors.
/// </summary>
public class DeclarationLookupTests
{
    [Fact]
    public void Find_LocalDeclaredBeforeTheUse_IsFound()
    {
        var found = Find("class C { void M() { decimal min = 100m; Use(min); } }", "min");

        found.ShouldNotBeNull();
        found!.Kind.ShouldBe(FreeVariableKind.ReproducibleLocal);
        found.TypeText.ShouldBe("decimal");
        found.Initializer!.ToString().ShouldBe("100m");
        found.IsConst.ShouldBeFalse();
    }

    [Fact]
    public void Find_LocalDeclaredAfterTheUse_IsNotFound()
        => Find("class C { void M() { Use(min); decimal min = 100m; } }", "min").ShouldBeNull();

    [Fact]
    public void Find_ConstLocal_IsMarkedConstant()
    {
        var found = Find("class C { void M() { const int n = 5; Use(n); } }", "n");

        found!.Kind.ShouldBe(FreeVariableKind.Constant);
        found.IsConst.ShouldBeTrue();
    }

    [Fact]
    public void Find_LocalWithAnOpaqueInitializer_IsOpaque()
    {
        var found = Find("class C { void M() { var svc = GetService(); Use(svc); } }", "svc");

        found!.Kind.ShouldBe(FreeVariableKind.OpaqueLocal);
        found.TypeText.ShouldBe("var");
    }

    [Fact]
    public void Find_MethodParameter_IsFound()
    {
        var found = Find("class C { void M(int[] ids) { Use(ids); } }", "ids");

        found!.Kind.ShouldBe(FreeVariableKind.Parameter);
        found.TypeText.ShouldBe("int[]");
    }

    [Fact]
    public void Find_LocalFunctionParameter_IsFound()
    {
        var found = Find("class C { void M() { void Inner(string term) { Use(term); } } }", "term");

        found!.Kind.ShouldBe(FreeVariableKind.Parameter);
        found.TypeText.ShouldBe("string");
    }

    [Fact]
    public void Find_ConstructorParameter_IsFound()
    {
        var found = Find("class C { C(AppDbContext db) { Use(db); } }", "db");

        found!.Kind.ShouldBe(FreeVariableKind.Parameter);
        found.TypeText.ShouldBe("AppDbContext");
    }

    [Fact]
    public void Find_PrimaryConstructorParameter_IsFound()
    {
        var found = Find("class C(AppDbContext db) { void M() { Use(db); } }", "db");

        found!.Kind.ShouldBe(FreeVariableKind.PrimaryConstructorParameter);
        found.TypeText.ShouldBe("AppDbContext");
    }

    [Fact]
    public void Find_Field_IsFound()
    {
        var found = Find("class C { private readonly int _n = 3; void M() { Use(_n); } }", "_n");

        found!.Kind.ShouldBe(FreeVariableKind.Field);
        found.TypeText.ShouldBe("int");
        found.Initializer!.ToString().ShouldBe("3");
    }

    [Fact]
    public void Find_ConstField_IsMarkedConstant()
    {
        var found = Find("class C { private const int Limit = 10; void M() { Use(Limit); } }", "Limit");

        found!.Kind.ShouldBe(FreeVariableKind.Constant);
        found.IsConst.ShouldBeTrue();
    }

    [Fact]
    public void Find_Property_IsFound()
    {
        var found = Find("class C { private int PageSize { get; } = 25; void M() { Use(PageSize); } }", "PageSize");

        found!.Kind.ShouldBe(FreeVariableKind.Property);
        found.TypeText.ShouldBe("int");
        found.Initializer!.ToString().ShouldBe("25");
    }

    [Fact]
    public void Find_ForEachVariable_IsFound()
    {
        var found = Find("class C { void M() { foreach (Product p in xs) { Use(p); } } }", "p");

        found!.Kind.ShouldBe(FreeVariableKind.OpaqueLocal);
        found.TypeText.ShouldBe("Product");
    }

    [Fact]
    public void Find_PatternDesignation_IsFound()
    {
        var found = Find("class C { void M() { if (o is AppDbContext ctx) { Use(ctx); } } }", "ctx");

        found!.Kind.ShouldBe(FreeVariableKind.OpaqueLocal);
        found.TypeText.ShouldBe("AppDbContext");
    }

    [Fact]
    public void Find_LambdaParameter_ShadowsAnythingOutside()
    {
        var found = Find("class C { int p; void M() { xs.Select(p => Use(p)); } }", "p");

        found!.Kind.ShouldBe(FreeVariableKind.Parameter);
    }

    [Fact]
    public void Find_LocalDeclaredInAnEnclosingBlock_IsFound()
    {
        var found = Find("class C { void M() { int n = 1; if (true) { Use(n); } } }", "n");

        found!.TypeText.ShouldBe("int");
    }

    [Fact]
    public void Find_TopLevelStatementLocal_IsFound()
    {
        var found = Find("int n = 1;\r\nUse(n);\r\n", "n");

        found!.TypeText.ShouldBe("int");
    }

    [Fact]
    public void Find_UndeclaredName_IsNull()
        => Find("class C { void M() { Use(nope); } }", "nope").ShouldBeNull();

    [Fact]
    public void Find_NullOrEmptyInput_IsNull()
    {
        DeclarationLookup.Find(null!, "x").ShouldBeNull();
        DeclarationLookup.Find(Fixture.ParseRoot("class C { }"), string.Empty).ShouldBeNull();
    }

    private static DeclarationLookupResult? Find(string source, string name)
    {
        var root = Fixture.ParseRoot(source);
        var usage = root.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Last(i => i.Identifier.ValueText == name);

        return DeclarationLookup.Find(usage, name);
    }
}
