using EFCoreSqlPreview.Core.Presentation;

namespace EFCoreSqlPreview.Core.Tests.Presentation;

/// <summary>
/// Tests for <see cref="SqlTokenizer"/>. The one that matters most is
/// <see cref="Tokenizing_never_changes_the_text"/>: colouring is cosmetic, but silently dropping or
/// duplicating a character while colouring would make the tool lie about the SQL it is showing.
/// </summary>
public class SqlTokenizerTests
{
    [Theory]
    [InlineData("SELECT")]
    [InlineData("select")]
    [InlineData("Where")]
    [InlineData("INNER")]
    [InlineData("JOIN")]
    [InlineData("GROUP")]
    [InlineData("HAVING")]
    [InlineData("CASE")]
    [InlineData("ESCAPE")]
    public void Reserved_words_are_keywords_regardless_of_case(string word)
        => Single(word).Kind.ShouldBe(SqlTokenKind.Keyword);

    [Theory]
    [InlineData("COUNT")]
    [InlineData("COALESCE")]
    [InlineData("SUM")]
    [InlineData("string_agg")]
    public void Built_in_functions_get_their_own_kind(string word)
        => Single(word).Kind.ShouldBe(SqlTokenKind.Function);

    [Theory]
    [InlineData("[Products]")]
    [InlineData("\"Products\"")]
    [InlineData("`Products`")]
    public void Quoted_identifiers_are_identifiers_in_every_dialects_quoting(string text)
    {
        var token = Single(text);

        token.Kind.ShouldBe(SqlTokenKind.Identifier);
        token.Text.ShouldBe(text);
    }

    [Theory]
    [InlineData("@minPrice")]
    [InlineData("@__search_0")]
    [InlineData("@@IDENTITY")]
    [InlineData(":name")]
    [InlineData("$1")]
    public void Parameters_are_recognised_in_every_dialects_spelling(string text)
    {
        var token = Single(text);

        token.Kind.ShouldBe(SqlTokenKind.Parameter);
        token.Text.ShouldBe(text);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("3.14")]
    [InlineData("0.0E0")]
    [InlineData("1e-3")]
    public void Numeric_literals_including_exponents_are_numbers(string text)
    {
        var token = Single(text);

        token.Kind.ShouldBe(SqlTokenKind.Number);
        token.Text.ShouldBe(text);
    }

    [Fact]
    public void A_string_literal_keeps_its_quotes_and_survives_a_doubled_quote()
    {
        var token = Single("'it''s'");

        token.Kind.ShouldBe(SqlTokenKind.String);
        token.Text.ShouldBe("'it''s'");
    }

    [Fact]
    public void A_keyword_inside_a_string_literal_is_not_coloured_as_a_keyword()
    {
        var kinds = Flatten("'SELECT'").Select(t => t.Kind).ToList();

        kinds.ShouldNotContain(SqlTokenKind.Keyword);
        kinds.ShouldContain(SqlTokenKind.String);
    }

    [Fact]
    public void A_line_comment_swallows_the_rest_of_the_line()
    {
        var tokens = Flatten("SELECT 1 -- WHERE never happens");

        tokens.Last().Kind.ShouldBe(SqlTokenKind.Comment);
        tokens.Last().Text.ShouldBe("-- WHERE never happens");
        tokens.Count(t => t.Kind == SqlTokenKind.Keyword).ShouldBe(1);
    }

    [Fact]
    public void A_block_comment_on_one_line_is_a_comment()
        => Flatten("SELECT /* note */ 1")
            .ShouldContain(t => t.Kind == SqlTokenKind.Comment && t.Text == "/* note */");

    [Fact]
    public void An_unterminated_quote_runs_to_the_end_of_the_line_instead_of_throwing()
    {
        var token = Single("'unterminated");

        token.Kind.ShouldBe(SqlTokenKind.String);
        token.Text.ShouldBe("'unterminated");
    }

    [Fact]
    public void Operators_are_grouped_into_one_run()
        => Flatten("a >= b").ShouldContain(t => t.Kind == SqlTokenKind.Operator && t.Text == ">=");

    [Fact]
    public void Line_structure_is_preserved_including_blank_lines()
    {
        var lines = SqlTokenizer.Tokenize("SELECT 1\n\nFROM [T]");

        lines.Count.ShouldBe(3);
        lines[1].ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Empty_input_yields_a_single_empty_line(string? sql)
    {
        var lines = SqlTokenizer.Tokenize(sql);

        lines.ShouldHaveSingleItem().ShouldBeEmpty();
    }

    [Fact]
    public void Leading_indentation_is_kept_so_the_rendered_SQL_stays_aligned()
    {
        var lines = SqlTokenizer.Tokenize("SELECT 1\n    FROM [T]");

        lines[1][0].Text.ShouldStartWith("    ");
    }

    /// <summary>
    /// Concatenating every token of every line, rejoined with newlines, must reproduce the input exactly.
    /// </summary>
    /// <param name="sql">The SQL to round-trip.</param>
    [Theory]
    [InlineData("SELECT [p].[Id], [p].[Name] FROM [Products] AS [p] WHERE [p].[Price] > @p")]
    [InlineData("SELECT COUNT(*)\nFROM [Products] AS [p]\nWHERE [p].[Name] LIKE @s ESCAPE N'\\'")]
    [InlineData("-- leading comment\nSELECT 1")]
    [InlineData("SELECT CASE WHEN [w].[Id] IS NULL THEN 0.0E0 ELSE -[w].[Qty] END")]
    [InlineData("  spaced   out  \n\n   ")]
    [InlineData("SELECT 'a''b', \"Quoted\", `Back`, /* c */ 1")]
    [InlineData("")]
    public void Tokenizing_never_changes_the_text(string sql)
    {
        var rebuilt = string.Join(
            "\n",
            SqlTokenizer.Tokenize(sql).Select(line => string.Concat(line.Select(t => t.Text))));

        rebuilt.ShouldBe(sql.Replace("\r\n", "\n"));
    }

    [Fact]
    public void A_realistic_EF_command_colours_the_parts_that_matter()
    {
        const string sql = """
SELECT [p].[Id], [c].[Name] AS [CategoryName]
FROM [Products] AS [p]
INNER JOIN [Categories] AS [c] ON [p].[CategoryId] = [c].[Id]
WHERE [p].[Price] > @minPrice
""";

        var tokens = Flatten(sql);

        tokens.ShouldContain(t => t.Kind == SqlTokenKind.Keyword && t.Text == "SELECT");
        tokens.ShouldContain(t => t.Kind == SqlTokenKind.Keyword && t.Text == "INNER");
        tokens.ShouldContain(t => t.Kind == SqlTokenKind.Keyword && t.Text == "JOIN");
        tokens.ShouldContain(t => t.Kind == SqlTokenKind.Identifier && t.Text == "[Products]");
        tokens.ShouldContain(t => t.Kind == SqlTokenKind.Parameter && t.Text == "@minPrice");
        tokens.ShouldContain(t => t.Kind == SqlTokenKind.Operator && t.Text == ">");
    }

    private static SqlToken Single(string sql) => Flatten(sql).ShouldHaveSingleItem();

    private static List<SqlToken> Flatten(string sql)
        => SqlTokenizer.Tokenize(sql).SelectMany(line => line).ToList();
}
