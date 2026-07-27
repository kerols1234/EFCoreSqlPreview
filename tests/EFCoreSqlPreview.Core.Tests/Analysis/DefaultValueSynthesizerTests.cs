using EFCoreSqlPreview.Core.Analysis;

namespace EFCoreSqlPreview.Core.Tests.Analysis;

/// <summary>
/// Covers the placeholder values used when a free variable's real value cannot be recovered.
/// </summary>
public class DefaultValueSynthesizerTests
{
    [Theory]
    [InlineData("int", "0")]
    [InlineData("Int32", "0")]
    [InlineData("long", "0L")]
    [InlineData("short", "(short)0")]
    [InlineData("byte", "(byte)0")]
    [InlineData("decimal", "0m")]
    [InlineData("double", "0d")]
    [InlineData("float", "0f")]
    [InlineData("bool", "false")]
    [InlineData("char", "'\\0'")]
    [InlineData("string", "\"\"")]
    [InlineData("String", "\"\"")]
    [InlineData("DateTime", "DateTime.Now")]
    [InlineData("DateTimeOffset", "DateTimeOffset.Now")]
    [InlineData("DateOnly", "DateOnly.FromDateTime(DateTime.Today)")]
    [InlineData("TimeOnly", "TimeOnly.FromDateTime(DateTime.Now)")]
    [InlineData("TimeSpan", "TimeSpan.Zero")]
    [InlineData("Guid", "Guid.Empty")]
    [InlineData("CancellationToken", "CancellationToken.None")]
    public void For_RecognisedSimpleType_ProducesAConcreteValue(string type, string expected)
    {
        DefaultValueSynthesizer.For(type).ShouldBe(expected);
        DefaultValueSynthesizer.TryFor(type, out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("int?", "null")]
    [InlineData("string?", "null")]
    [InlineData("DateTime?", "null")]
    [InlineData("Nullable<int>", "null")]
    public void For_NullableType_ProducesNull(string type, string expected)
        => DefaultValueSynthesizer.For(type).ShouldBe(expected);

    [Theory]
    [InlineData("int[]", "Array.Empty<int>()")]
    [InlineData("string[]", "Array.Empty<string>()")]
    [InlineData("List<string>", "new List<string>()")]
    [InlineData("IList<int>", "new List<int>()")]
    [InlineData("IReadOnlyList<int>", "new List<int>()")]
    [InlineData("ICollection<int>", "new List<int>()")]
    [InlineData("IEnumerable<int>", "Enumerable.Empty<int>()")]
    [InlineData("IQueryable<Product>", "Enumerable.Empty<Product>()")]
    [InlineData("HashSet<int>", "new HashSet<int>()")]
    [InlineData("ISet<int>", "new HashSet<int>()")]
    [InlineData("Dictionary<int, string>", "new Dictionary<int, string>()")]
    [InlineData("IReadOnlyDictionary<int, string>", "new Dictionary<int, string>()")]
    public void For_CollectionType_ProducesAnEmptyCollection(string type, string expected)
        => DefaultValueSynthesizer.For(type).ShouldBe(expected);

    [Fact]
    public void For_GloballyQualifiedType_StripsTheQualifier()
        => DefaultValueSynthesizer.For("global::System.Int32").ShouldBe("0");

    [Fact]
    public void For_NamespaceQualifiedCollection_IsStillRecognised()
        => DefaultValueSynthesizer.For("System.Collections.Generic.List<int>").ShouldBe("new List<int>()");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("var")]
    [InlineData("dynamic")]
    [InlineData("object")]
    public void For_UnknownType_ProducesADefaultGuess(string? type)
    {
        DefaultValueSynthesizer.For(type).ShouldBe("default!");
        DefaultValueSynthesizer.TryFor(type, out _).ShouldBeFalse();
    }

    [Fact]
    public void For_EnumLookingType_ProducesADefaultGuess()
    {
        DefaultValueSynthesizer.TryFor("Status", out var value).ShouldBeFalse();
        value.ShouldBe("default!");
    }

    [Fact]
    public void For_UnknownGenericType_ProducesATypedDefault()
    {
        DefaultValueSynthesizer.TryFor("Box<int>", out var value).ShouldBeFalse();
        value.ShouldBe("default(Box<int>)!");
    }

    [Fact]
    public void For_QualifiedUnknownType_ProducesATypedDefault()
    {
        DefaultValueSynthesizer.TryFor("Demo.Models.Owner", out var value).ShouldBeFalse();
        value.ShouldBe("default(Demo.Models.Owner)!");
    }

    [Theory]
    [InlineData("Status", true)]
    [InlineData("Kind", true)]
    [InlineData("int", false)]
    [InlineData("List<int>", false)]
    [InlineData("Demo.Status", false)]
    [InlineData("Status?", false)]
    [InlineData("Status[]", false)]
    public void LooksLikeEnum_MatchesUnqualifiedCapitalisedNames(string type, bool expected)
        => DefaultValueSynthesizer.LooksLikeEnum(type).ShouldBe(expected);

    [Fact]
    public void For_TypeWithSurroundingWhitespace_IsTrimmed()
        => DefaultValueSynthesizer.For("  decimal  ").ShouldBe("0m");
}
