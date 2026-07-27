using System;
using System.Collections.Generic;

namespace EFCoreSqlPreview.Core.Presentation
{
    /// <summary>
    /// What a run of SQL text is, for the purpose of colouring it.
    /// </summary>
    public enum SqlTokenKind
    {
        /// <summary>Anything with no colour of its own, including whitespace and punctuation.</summary>
        Plain,

        /// <summary>A reserved word such as <c>SELECT</c> or <c>LEFT JOIN</c>.</summary>
        Keyword,

        /// <summary>A built-in function name such as <c>COUNT</c> or <c>COALESCE</c>.</summary>
        Function,

        /// <summary>A quoted identifier: <c>[Products]</c>, <c>"Products"</c> or <c>`Products`</c>.</summary>
        Identifier,

        /// <summary>A string literal.</summary>
        String,

        /// <summary>A numeric literal.</summary>
        Number,

        /// <summary>A query parameter such as <c>@minPrice</c>, <c>$1</c> or <c>:name</c>.</summary>
        Parameter,

        /// <summary>A line or block comment.</summary>
        Comment,

        /// <summary>An operator such as <c>=</c>, <c>&gt;</c> or <c>*</c>.</summary>
        Operator,
    }

    /// <summary>One coloured run of SQL.</summary>
    public sealed class SqlToken
    {
        /// <summary>Initializes a new instance of the <see cref="SqlToken"/> class.</summary>
        /// <param name="kind">What the run is.</param>
        /// <param name="text">The text, verbatim.</param>
        public SqlToken(SqlTokenKind kind, string text)
        {
            this.Kind = kind;
            this.Text = text;
        }

        /// <summary>Gets what the run is.</summary>
        public SqlTokenKind Kind { get; }

        /// <summary>Gets the text, verbatim.</summary>
        public string Text { get; }
    }

    /// <summary>
    /// Splits SQL into coloured runs, one list per line.
    /// </summary>
    /// <remarks>
    /// This is deliberately a lexer and not a parser. It has to cope with whatever dialect the provider
    /// emitted - bracket quoting from SQL Server, double quotes from PostgreSQL, backticks from MySQL - and a
    /// wrong guess must never do worse than leave a run uncoloured. Nothing here changes the text: joining
    /// every token of every line with newlines reproduces the input exactly, which is what the round-trip test
    /// asserts.
    /// </remarks>
    public static class SqlTokenizer
    {
        /// <summary>Reserved words. Multi-word forms such as LEFT JOIN colour as two adjacent keywords.</summary>
        private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ADD", "ALL", "ALTER", "AND", "ANY", "AS", "ASC", "BETWEEN", "BY", "CASE", "CAST", "COLLATE",
            "CONVERT", "CREATE", "CROSS", "DECLARE", "DEFAULT", "DELETE", "DESC", "DISTINCT", "DROP", "ELSE",
            "END", "ESCAPE", "EXCEPT", "EXISTS", "FALSE", "FETCH", "FIRST", "FOR", "FROM", "FULL", "GROUP",
            "HAVING", "IN", "INNER", "INSERT", "INTERSECT", "INTO", "IS", "JOIN", "LATERAL", "LEFT", "LIKE",
            "LIMIT", "NEXT", "NOT", "NULL", "OFFSET", "ON", "ONLY", "OR", "ORDER", "OUTER", "OVER", "PARTITION",
            "RETURNING", "RIGHT", "ROWS", "SELECT", "SET", "SOME", "THEN", "TOP", "TRUE", "UNION", "UPDATE",
            "USING", "VALUES", "WHEN", "WHERE", "WITH",
        };

        /// <summary>Built-in functions, coloured apart from keywords so aggregates stand out in a projection.</summary>
        private static readonly HashSet<string> Functions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ABS", "AVG", "CEILING", "CHARINDEX", "COALESCE", "CONCAT", "COUNT", "COUNT_BIG", "CURRENT_TIMESTAMP",
            "DATEADD", "DATEDIFF", "DATEPART", "DAY", "FLOOR", "GETDATE", "GETUTCDATE", "IIF", "ISNULL", "LEN",
            "LOWER", "LTRIM", "MAX", "MIN", "MONTH", "NEWID", "NULLIF", "POWER", "RAND", "REPLACE", "ROUND",
            "ROW_NUMBER", "RTRIM", "SIGN", "SQRT", "STRING_AGG", "SUBSTRING", "SUM", "TRIM", "UPPER", "YEAR",
        };

        private const string OperatorCharacters = "=<>!+-*/%|&^~";

        /// <summary>
        /// Splits SQL into one list of tokens per line.
        /// </summary>
        /// <param name="sql">The command text. <see langword="null"/> and empty are both accepted.</param>
        /// <returns>
        /// One entry per line of <paramref name="sql"/>, each holding that line's runs in order. A blank line
        /// yields an empty list rather than being dropped, so line numbers survive.
        /// </returns>
        public static IReadOnlyList<IReadOnlyList<SqlToken>> Tokenize(string? sql)
        {
            var lines = new List<IReadOnlyList<SqlToken>>();
            if (string.IsNullOrEmpty(sql))
            {
                lines.Add(new List<SqlToken>());
                return lines;
            }

            foreach (var line in SplitLines(sql!))
            {
                lines.Add(TokenizeLine(line));
            }

            return lines;
        }

        /// <summary>
        /// Splits on newlines while tolerating either line ending, without discarding trailing blank lines.
        /// </summary>
        /// <param name="text">The text to split.</param>
        /// <returns>The lines, with their terminators removed.</returns>
        private static IEnumerable<string> SplitLines(string text)
        {
            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n')
                {
                    continue;
                }

                var end = i > start && text[i - 1] == '\r' ? i - 1 : i;
                yield return text.Substring(start, end - start);
                start = i + 1;
            }

            yield return text.Substring(start);
        }

        private static IReadOnlyList<SqlToken> TokenizeLine(string line)
        {
            var tokens = new List<SqlToken>();
            var plainStart = -1;
            var i = 0;

            void FlushPlain(int end)
            {
                if (plainStart >= 0)
                {
                    tokens.Add(new SqlToken(SqlTokenKind.Plain, line.Substring(plainStart, end - plainStart)));
                    plainStart = -1;
                }
            }

            void Add(SqlTokenKind kind, int start, int end)
            {
                FlushPlain(start);
                tokens.Add(new SqlToken(kind, line.Substring(start, end - start)));
            }

            while (i < line.Length)
            {
                var c = line[i];

                // Line comment: everything after it belongs to the comment, so this always ends the line.
                if (c == '-' && i + 1 < line.Length && line[i + 1] == '-')
                {
                    Add(SqlTokenKind.Comment, i, line.Length);
                    i = line.Length;
                    continue;
                }

                // Block comments are not tracked across lines; a single-line one is by far the common case.
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
                {
                    var close = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    var end = close < 0 ? line.Length : close + 2;
                    Add(SqlTokenKind.Comment, i, end);
                    i = end;
                    continue;
                }

                if (c == '\'')
                {
                    var end = ScanQuoted(line, i, '\'', doubledEscapes: true);
                    Add(SqlTokenKind.String, i, end);
                    i = end;
                    continue;
                }

                if (c == '[')
                {
                    var close = line.IndexOf(']', i + 1);
                    var end = close < 0 ? line.Length : close + 1;
                    Add(SqlTokenKind.Identifier, i, end);
                    i = end;
                    continue;
                }

                if (c == '"' || c == '`')
                {
                    var end = ScanQuoted(line, i, c, doubledEscapes: c == '"');
                    Add(SqlTokenKind.Identifier, i, end);
                    i = end;
                    continue;
                }

                // Parameters: @name and @@name (SQL Server), :name (Oracle), $1 (PostgreSQL positional).
                if ((c == '@' || c == ':' || c == '$') && i + 1 < line.Length && IsParameterStart(line[i + 1]))
                {
                    var end = i + 1;
                    while (end < line.Length && (IsWordCharacter(line[end]) || line[end] == '@'))
                    {
                        end++;
                    }

                    Add(SqlTokenKind.Parameter, i, end);
                    i = end;
                    continue;
                }

                if (char.IsDigit(c))
                {
                    var end = i;
                    while (end < line.Length && (char.IsDigit(line[end]) || line[end] == '.'))
                    {
                        end++;
                    }

                    // Exponent forms such as 0.0E0 arrive from EF's decimal handling.
                    if (end < line.Length && (line[end] == 'E' || line[end] == 'e'))
                    {
                        var exponent = end + 1;
                        if (exponent < line.Length && (line[exponent] == '+' || line[exponent] == '-'))
                        {
                            exponent++;
                        }

                        if (exponent < line.Length && char.IsDigit(line[exponent]))
                        {
                            end = exponent;
                            while (end < line.Length && char.IsDigit(line[end]))
                            {
                                end++;
                            }
                        }
                    }

                    Add(SqlTokenKind.Number, i, end);
                    i = end;
                    continue;
                }

                if (IsWordStart(c))
                {
                    var end = i;
                    while (end < line.Length && IsWordCharacter(line[end]))
                    {
                        end++;
                    }

                    var word = line.Substring(i, end - i);
                    if (Keywords.Contains(word))
                    {
                        Add(SqlTokenKind.Keyword, i, end);
                    }
                    else if (Functions.Contains(word))
                    {
                        Add(SqlTokenKind.Function, i, end);
                    }
                    else if (plainStart < 0)
                    {
                        plainStart = i;
                    }

                    i = end;
                    continue;
                }

                if (OperatorCharacters.IndexOf(c) >= 0)
                {
                    var end = i;
                    while (end < line.Length && OperatorCharacters.IndexOf(line[end]) >= 0)
                    {
                        end++;
                    }

                    Add(SqlTokenKind.Operator, i, end);
                    i = end;
                    continue;
                }

                if (plainStart < 0)
                {
                    plainStart = i;
                }

                i++;
            }

            FlushPlain(line.Length);
            return tokens;
        }

        /// <summary>
        /// Finds the end of a quoted run.
        /// </summary>
        /// <param name="line">The line being scanned.</param>
        /// <param name="start">Index of the opening quote.</param>
        /// <param name="quote">The quote character.</param>
        /// <param name="doubledEscapes">Whether a doubled quote escapes itself rather than closing the run.</param>
        /// <returns>The index just past the closing quote, or the end of the line when it is unterminated.</returns>
        private static int ScanQuoted(string line, int start, char quote, bool doubledEscapes)
        {
            var i = start + 1;
            while (i < line.Length)
            {
                if (line[i] != quote)
                {
                    i++;
                    continue;
                }

                if (doubledEscapes && i + 1 < line.Length && line[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            return line.Length;
        }

        private static bool IsParameterStart(char c) => IsWordStart(c) || char.IsDigit(c) || c == '@';

        private static bool IsWordStart(char c) => char.IsLetter(c) || c == '_' || c == '#';

        private static bool IsWordCharacter(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '#' || c == '$';
    }
}
