using EFCoreSqlPreview.Core.Analysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFCoreSqlPreview.Core.Tests.Analysis;

/// <summary>
/// Covers the whitelist that decides whether an initializer can be pasted into the worker verbatim.
/// </summary>
public class InitializerReproducibilityTests
{
    [Theory]
    [InlineData("100m")]
    [InlineData("\"widget\"")]
    [InlineData("'x'")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("-30")]
    [InlineData("(decimal)5")]
    [InlineData("1 + 2 * 3")]
    [InlineData("default(int)")]
    [InlineData("typeof(int)")]
    [InlineData("true ? 1 : 2")]
    [InlineData("$\"a{1}b\"")]
    public void Classify_LiteralShapedInitializer_IsALiteral(string expression)
        => Classify(expression).ShouldBe(ValueSource.LiteralInitializer);

    [Theory]
    [InlineData("new[] { 1, 2, 3 }")]
    [InlineData("new int[] { 1, 2 }")]
    [InlineData("new List<string> { \"a\", \"b\" }")]
    [InlineData("new HashSet<int>()")]
    [InlineData("new DateTime(2024, 1, 1)")]
    [InlineData("new Guid(\"x\")")]
    [InlineData("(1, \"a\")")]
    [InlineData("[1, 2, 3]")]
    public void Classify_ConstructibleInitializer_IsConstructible(string expression)
        => Classify(expression).ShouldBe(ValueSource.ConstructibleInitializer);

    [Theory]
    [InlineData("DateTime.Now")]
    [InlineData("DateTime.UtcNow")]
    [InlineData("DateTime.Today.AddDays(-1)")]
    [InlineData("DateTime.Now.AddDays(-30)")]
    [InlineData("Guid.Empty")]
    [InlineData("Guid.NewGuid()")]
    [InlineData("string.Empty")]
    [InlineData("TimeSpan.FromMinutes(5)")]
    [InlineData("CancellationToken.None")]
    [InlineData("Status.Active")]
    [InlineData("Status.A | Status.B")]
    public void Classify_WellKnownStatic_IsReproducible(string expression)
    {
        var classified = Classify(expression);

        classified.ShouldBeOneOf(ValueSource.WellKnownStatic, ValueSource.LiteralInitializer);
        InitializerReproducibility.IsReproducible(Parse(expression)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("GetService()")]
    [InlineData("_repo.Load()")]
    [InlineData("Task.Run(() => 1)")]
    [InlineData("items[0]")]
    [InlineData("new List<int> { GetOne() }")]
    [InlineData("new Widget(GetOne())")]
    [InlineData("$\"a{GetOne()}b\"")]
    public void Classify_NonReproducibleInitializer_FallsBackToASynthesizedDefault(string expression)
    {
        Classify(expression).ShouldBe(ValueSource.SynthesizedDefault);
        InitializerReproducibility.IsReproducible(Parse(expression)).ShouldBeFalse();
    }

    [Fact]
    public void Classify_Null_IsNotReproducible()
    {
        InitializerReproducibility.Classify(null).ShouldBe(ValueSource.SynthesizedDefault);
        InitializerReproducibility.IsReproducible(null).ShouldBeFalse();
    }

    [Fact]
    public void WellKnownStatics_IncludeTheDocumentedMembers()
    {
        InitializerReproducibility.WellKnownStatics.ShouldContain("DateTime.UtcNow");
        InitializerReproducibility.WellKnownStatics.ShouldContain("Guid.Empty");
        InitializerReproducibility.WellKnownStatics.ShouldContain("CancellationToken.None");
    }

    [Fact]
    public void WellKnownContinuations_IncludeTheDateArithmetic()
    {
        InitializerReproducibility.WellKnownContinuations.ShouldContain("AddDays");
        InitializerReproducibility.WellKnownContinuations.ShouldContain("Subtract");
    }

    [Fact]
    public void Classify_WithZeroTransitiveDepth_StopsFollowingLocals()
    {
        var text = "class C { void M() { var a = 1; var b = a + 1; } }";
        var root = Fixture.ParseRoot(text);
        var initializer = root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .First(d => d.Identifier.ValueText == "b")
            .Initializer!.Value;

        InitializerReproducibility.Classify(initializer, 0).ShouldBe(ValueSource.SynthesizedDefault);
        InitializerReproducibility.Classify(initializer, 3).ShouldBe(ValueSource.TransitiveLocal);
    }

    [Fact]
    public void Classify_CyclicLocalReferences_TerminateWithoutStackOverflow()
    {
        var text = "class C { void M() { var a = b; var b = a; } }";
        var root = Fixture.ParseRoot(text);
        var initializer = root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .First(d => d.Identifier.ValueText == "a")
            .Initializer!.Value;

        Should.NotThrow(() => InitializerReproducibility.Classify(initializer, 3))
            .ShouldBe(ValueSource.SynthesizedDefault);
    }

    private static ValueSource Classify(string expression)
        => InitializerReproducibility.Classify(Parse(expression));

    private static ExpressionSyntax Parse(string expression)
    {
        var text = "class C { void M() { var x = " + expression + "; } }";
        return Fixture.ParseRoot(text)
            .DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .First(d => d.Identifier.ValueText == "x")
            .Initializer!.Value;
    }
}
