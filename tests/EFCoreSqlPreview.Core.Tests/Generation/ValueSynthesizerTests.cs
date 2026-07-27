using EFCoreSqlPreview.Core.Analysis;
using EFCoreSqlPreview.Core.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace EFCoreSqlPreview.Core.Tests.Generation;

/// <summary>
/// Tests for <see cref="ValueSynthesizer"/>, which renders one analyzed free variable as the declaration the
/// generated worker emits for it.
/// </summary>
public class ValueSynthesizerTests
{
    [Fact]
    public void A_declared_type_and_a_reproducible_initializer_pass_straight_through()
    {
        var declaration = ValueSynthesizer.Synthesize(Variable("minPrice", "decimal", "100m", ValueSource.LiteralInitializer));

        declaration.Name.ShouldBe("minPrice");
        declaration.Declaration.ShouldBe("decimal minPrice = 100m;");
        declaration.Warning.ShouldBeNull();
    }

    [Fact]
    public void A_constructible_initializer_passes_through_verbatim()
    {
        var declaration = ValueSynthesizer.Synthesize(
            Variable("ids", "int[]", "new[] { 1, 2, 3 }", ValueSource.ConstructibleInitializer));

        declaration.Declaration.ShouldBe("int[] ids = new[] { 1, 2, 3 };");
        declaration.Warning.ShouldBeNull();
    }

    [Theory]
    [InlineData("var")]
    [InlineData("dynamic")]
    [InlineData("")]
    [InlineData(null)]
    public void An_uninformative_declared_type_becomes_var_when_the_value_types_itself(string? declaredType)
    {
        var declaration = ValueSynthesizer.Synthesize(Variable("term", declaredType, "\"widget\"", ValueSource.LiteralInitializer));

        declaration.Declaration.ShouldBe("var term = \"widget\";");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("default")]
    [InlineData("default!")]
    public void A_non_inferrable_value_with_no_declared_type_falls_back_to_object_with_a_warning(string value)
    {
        var declaration = ValueSynthesizer.Synthesize(Variable("thing", null, value, ValueSource.SynthesizedDefault));

        declaration.Declaration.ShouldBe($"object thing = {value};");
        declaration.Warning.ShouldNotBeNull();
        declaration.Warning!.ShouldContain("could not be determined");
        declaration.Warning.ShouldContain("thing");
    }

    [Fact]
    public void A_declared_type_keeps_default_bang_typed_rather_than_object()
    {
        var declaration = ValueSynthesizer.Synthesize(
            Variable("status", "OrderStatus", "default!", ValueSource.SynthesizedDefault, requiresUserValue: true));

        declaration.Declaration.ShouldBe("OrderStatus status = default!;");
        declaration.Warning.ShouldNotBeNull();
        declaration.Warning!.ShouldContain("no reproducible value");
    }

    [Fact]
    public void A_guessed_value_warns_that_the_SQL_can_depend_on_it()
    {
        var declaration = ValueSynthesizer.Synthesize(
            Variable("term", "string", "\"\"", ValueSource.SynthesizedDefault, requiresUserValue: true));

        declaration.Declaration.ShouldBe("string term = \"\";");
        declaration.Warning.ShouldNotBeNull();
        declaration.Warning!.ShouldContain("free-variables panel");
    }

    [Fact]
    public void A_user_override_wins_and_silences_the_guessed_value_warning()
    {
        var declaration = ValueSynthesizer.Synthesize(
            Variable("term", "string", "\"\"", ValueSource.SynthesizedDefault, requiresUserValue: true),
            overrideValue: "\"widget\"");

        declaration.Declaration.ShouldBe("string term = \"widget\";");
        declaration.Warning.ShouldBeNull();
    }

    [Theory]
    [InlineData("100m;", "decimal minPrice = 100m;")]
    [InlineData("  100m  ", "decimal minPrice = 100m;")]
    [InlineData("100m;;", "decimal minPrice = 100m;")]
    public void Trailing_semicolons_and_whitespace_are_trimmed_from_the_value(string value, string expected)
        => ValueSynthesizer.Synthesize(Variable("minPrice", "decimal", value, ValueSource.LiteralInitializer))
            .Declaration.ShouldBe(expected);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(";")]
    public void A_blank_override_falls_back_to_the_analyzer_suggestion(string overrideValue)
        => ValueSynthesizer.Synthesize(Variable("minPrice", "decimal", "100m", ValueSource.LiteralInitializer), overrideValue)
            .Declaration.ShouldBe("decimal minPrice = 100m;");

    [Fact]
    public void A_variable_with_no_value_at_all_becomes_default_bang()
        => ValueSynthesizer.Synthesize(Variable("x", "int", "", ValueSource.SynthesizedDefault))
            .Declaration.ShouldBe("int x = default!;");

    [Fact]
    public void Synthesize_rejects_a_null_variable()
        => Should.Throw<ArgumentNullException>(() => ValueSynthesizer.Synthesize(null!));

    [Fact]
    public void SynthesizeAll_keeps_source_order_and_applies_overrides_by_name()
    {
        var variables = new[]
        {
            Variable("minPrice", "decimal", "100m", ValueSource.LiteralInitializer),
            Variable("term", "string", "\"\"", ValueSource.SynthesizedDefault, requiresUserValue: true),
        };

        var overrides = new Dictionary<string, string>(StringComparer.Ordinal) { ["term"] = "\"widget\"" };

        var declarations = ValueSynthesizer.SynthesizeAll(variables, overrides, skipNames: null);

        declarations.Select(d => d.Declaration).ShouldBe(new[]
        {
            "decimal minPrice = 100m;",
            "string term = \"widget\";",
        });
    }

    [Fact]
    public void SynthesizeAll_skips_names_the_replayed_statements_already_declare()
    {
        var variables = new[]
        {
            Variable("query", "var", "null", ValueSource.SynthesizedDefault),
            Variable("term", "string", "\"x\"", ValueSource.LiteralInitializer),
        };

        var declarations = ValueSynthesizer.SynthesizeAll(
            variables,
            overrides: null,
            skipNames: new HashSet<string>(StringComparer.Ordinal) { "query" });

        declarations.ShouldHaveSingleItem().Name.ShouldBe("term");
    }

    [Fact]
    public void SynthesizeAll_emits_a_repeated_name_only_once()
    {
        var variables = new[]
        {
            Variable("term", "string", "\"first\"", ValueSource.LiteralInitializer),
            Variable("term", "string", "\"second\"", ValueSource.LiteralInitializer),
        };

        ValueSynthesizer.SynthesizeAll(variables, null, null)
            .ShouldHaveSingleItem().Declaration.ShouldBe("string term = \"first\";");
    }

    [Fact]
    public void SynthesizeAll_ignores_a_nameless_variable()
        => ValueSynthesizer.SynthesizeAll(
            new[] { Variable(string.Empty, "int", "0", ValueSource.LiteralInitializer) }, null, null).ShouldBeEmpty();

    [Fact]
    public void SynthesizeAll_rejects_a_null_sequence()
        => Should.Throw<ArgumentNullException>(() => ValueSynthesizer.SynthesizeAll(null!, null, null));

    [Theory]
    [InlineData("args")]
    [InlineData("__efspCtx")]
    [InlineData("__efspInterceptor")]
    [InlineData("__efspResult")]
    [InlineData("__efspCommands")]
    public void Identifiers_the_generated_program_owns_are_reserved(string name)
        => ValueSynthesizer.IsReservedName(name).ShouldBeTrue();

    [Theory]
    [InlineData("term")]
    [InlineData("Args")]
    [InlineData("ctx")]
    [InlineData(null)]
    public void Ordinary_identifiers_are_not_reserved(string? name)
        => ValueSynthesizer.IsReservedName(name).ShouldBeFalse();

    /// <summary>
    /// Every declared type the analyzer can synthesize a default for must produce a declaration that is a legal
    /// local variable declaration of exactly that type. This is what stops a synthesized value from turning a
    /// preview into a compiler error inside the generated worker.
    /// </summary>
    /// <param name="declaredType">The declared type text.</param>
    /// <param name="expectedValue">The value the analyzer synthesizes for it.</param>
    [Theory]
    [InlineData("int", "0")]
    [InlineData("long", "0L")]
    [InlineData("short", "(short)0")]
    [InlineData("byte", "(byte)0")]
    [InlineData("decimal", "0m")]
    [InlineData("double", "0d")]
    [InlineData("float", "0f")]
    [InlineData("bool", "false")]
    [InlineData("char", "'\\0'")]
    [InlineData("string", "\"\"")]
    [InlineData("DateTime", "DateTime.Now")]
    [InlineData("DateTimeOffset", "DateTimeOffset.Now")]
    [InlineData("DateOnly", "DateOnly.FromDateTime(DateTime.Today)")]
    [InlineData("TimeOnly", "TimeOnly.FromDateTime(DateTime.Now)")]
    [InlineData("TimeSpan", "TimeSpan.Zero")]
    [InlineData("Guid", "Guid.Empty")]
    [InlineData("int?", "null")]
    [InlineData("DateTime?", "null")]
    [InlineData("Nullable<int>", "null")]
    [InlineData("string?", "null")]
    [InlineData("int[]", "Array.Empty<int>()")]
    [InlineData("string[]", "Array.Empty<string>()")]
    [InlineData("List<string>", "new List<string>()")]
    [InlineData("IList<int>", "new List<int>()")]
    [InlineData("IEnumerable<int>", "Enumerable.Empty<int>()")]
    [InlineData("IQueryable<int>", "Enumerable.Empty<int>()")]
    [InlineData("HashSet<string>", "new HashSet<string>()")]
    [InlineData("Dictionary<int, string>", "new Dictionary<int, string>()")]
    [InlineData("OrderStatus", "default!")]
    [InlineData("Some.Custom.Type", "default(Some.Custom.Type)!")]
    public void A_synthesized_default_declares_a_local_of_the_declared_type(string declaredType, string expectedValue)
    {
        DefaultValueSynthesizer.For(declaredType).ShouldBe(expectedValue);

        var declaration = ValueSynthesizer.Synthesize(
            Variable("value", declaredType, expectedValue, ValueSource.SynthesizedDefault, requiresUserValue: true));

        declaration.Declaration.ShouldBe($"{declaredType} value = {expectedValue};");
        ParseAsLocalDeclaration(declaration.Declaration).Declaration.Type.ToString().ShouldBe(declaredType);
    }

    [Fact]
    public void An_unknown_type_yields_an_object_declaration_that_still_parses()
    {
        DefaultValueSynthesizer.For(null).ShouldBe("default!");

        var declaration = ValueSynthesizer.Synthesize(Variable("value", null, "default!", ValueSource.SynthesizedDefault));

        declaration.Declaration.ShouldBe("object value = default!;");
        ParseAsLocalDeclaration(declaration.Declaration).Declaration.Type.ToString().ShouldBe("object");
    }

    private static LocalDeclarationStatementSyntax ParseAsLocalDeclaration(string text)
    {
        var statement = SyntaxFactory.ParseStatement(text, options: LinqSelectionAnalyzer.ParseOptions);
        statement.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        return statement.ShouldBeOfType<LocalDeclarationStatementSyntax>();
    }

    private static FreeVariable Variable(
        string name,
        string? declaredType,
        string suggestedValue,
        ValueSource source,
        bool requiresUserValue = false)
        => new(
            Name: name,
            Kind: FreeVariableKind.ReproducibleLocal,
            DeclaredTypeName: declaredType,
            InitializerExpression: null,
            ValueSource: source,
            SuggestedValueExpression: suggestedValue,
            SuggestedDeclaration: null,
            RequiresUserValue: requiresUserValue,
            DeclarationSpan: new TextSpan(0, 0),
            UsageSpans: Array.Empty<TextSpan>());
}
