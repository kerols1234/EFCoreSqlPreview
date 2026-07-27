using EFCoreSqlPreview.Core.Analysis;
using Microsoft.CodeAnalysis.Text;

namespace EFCoreSqlPreview.Core.Tests.Analysis;

/// <summary>
/// Covers the guard that makes a raw editor selection safe for Roslyn's <c>FindNode</c>.
/// </summary>
public class SelectionNormalizerTests
{
    private const string Sample = "var x = _db.Products.ToList();\r\n";

    [Fact]
    public void Normalize_EmptyDocument_ReturnsAnEmptySpanAtTheStart()
        => SelectionNormalizer.Normalize(string.Empty, new TextSpan(0, 10)).ShouldBe(new TextSpan(0, 0));

    [Fact]
    public void Normalize_NullDocument_ReturnsAnEmptySpanAtTheStart()
        => SelectionNormalizer.Normalize(null!, new TextSpan(3, 10)).ShouldBe(new TextSpan(0, 0));

    [Fact]
    public void Normalize_SpanPastTheEnd_IsClampedToTheDocument()
    {
        var normalized = SelectionNormalizer.Normalize(Sample, new TextSpan(0, 10_000));

        normalized.End.ShouldBeLessThanOrEqualTo(Sample.Length);
    }

    [Fact]
    public void Normalize_SpanStartingPastTheEnd_IsClampedToTheDocument()
    {
        var normalized = SelectionNormalizer.Normalize(Sample, new TextSpan(Sample.Length + 50, 5));

        normalized.Start.ShouldBeLessThanOrEqualTo(Sample.Length);
        normalized.End.ShouldBeLessThanOrEqualTo(Sample.Length);
    }

    [Fact]
    public void Normalize_TrailingWhitespace_IsTrimmed()
    {
        var text = "var x = 1;   \r\n";
        var normalized = SelectionNormalizer.Normalize(text, new TextSpan(0, text.Length));

        text.Substring(normalized.Start, normalized.Length).ShouldBe("var x = 1;");
    }

    [Fact]
    public void Normalize_LeadingWhitespace_IsTrimmed()
    {
        var text = "    var x = 1;";
        var normalized = SelectionNormalizer.Normalize(text, new TextSpan(0, text.Length));

        text.Substring(normalized.Start, normalized.Length).ShouldBe("var x = 1;");
    }

    [Fact]
    public void Normalize_AllWhitespaceSelection_CollapsesToAZeroLengthSpan()
    {
        var text = "var x = 1;\r\n\r\n   \r\nvar y = 2;";
        var normalized = SelectionNormalizer.Normalize(text, new TextSpan(10, 8));

        normalized.Length.ShouldBe(0);
        normalized.Start.ShouldBeLessThan(text.Length);
    }

    [Fact]
    public void Normalize_CaretAfterAToken_StaysWhereItIs()
    {
        var normalized = SelectionNormalizer.Normalize(Sample, new TextSpan(3, 0));

        normalized.ShouldBe(new TextSpan(3, 0));
    }

    [Fact]
    public void Normalize_CaretInLeadingWhitespace_SnapsToTheNextToken()
    {
        var text = "    var x = 1;";
        var normalized = SelectionNormalizer.Normalize(text, new TextSpan(1, 0));

        normalized.Start.ShouldBe(4);
        normalized.Length.ShouldBe(0);
    }

    [Fact]
    public void Normalize_CaretAtTheVeryEnd_StaysInsideTheDocument()
    {
        var normalized = SelectionNormalizer.Normalize(Sample, new TextSpan(Sample.Length, 0));

        normalized.Start.ShouldBeLessThan(Sample.Length);
    }

    [Fact]
    public void Normalize_IsIdempotent()
    {
        var once = SelectionNormalizer.Normalize(Sample, new TextSpan(0, Sample.Length));
        var twice = SelectionNormalizer.Normalize(Sample, once);

        twice.ShouldBe(once);
    }
}
