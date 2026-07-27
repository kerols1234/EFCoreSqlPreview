using System.Text;

namespace EFCoreSqlPreview.Core.Analysis;

/// <summary>
/// Produces a compilable placeholder value for a free variable whose real value cannot be recovered.
/// </summary>
/// <remarks>
/// The generated SQL genuinely depends on these values: an empty <c>Contains</c> collection collapses a
/// predicate to <c>WHERE 0 = 1</c>. That is why anything synthesized here also sets
/// <see cref="FreeVariable.RequiresUserValue"/>.
/// </remarks>
public static class DefaultValueSynthesizer
{
    private const string UnknownTypeDefault = "default!";

    /// <summary>Synthesizes a default value expression for a declared type.</summary>
    /// <param name="declaredTypeText">The declared type text, or <see langword="null"/> when unknown.</param>
    /// <returns>A C# expression, e.g. <c>0m</c>, <c>Array.Empty&lt;int&gt;()</c> or <c>default!</c>.</returns>
    public static string For(string? declaredTypeText)
    {
        TryFor(declaredTypeText, out var expression);
        return expression;
    }

    /// <summary>Synthesizes a default value expression, reporting whether the type was recognised.</summary>
    /// <param name="declaredTypeText">The declared type text, or <see langword="null"/> when unknown.</param>
    /// <param name="expression">The synthesized expression; always set.</param>
    /// <returns><see langword="false"/> when the result is a <c>default</c> guess rather than a real value.</returns>
    public static bool TryFor(string? declaredTypeText, out string expression)
    {
        var type = Normalize(declaredTypeText);

        if (type.Length == 0 || type == "var" || type == "dynamic" || type == "object")
        {
            expression = UnknownTypeDefault;
            return false;
        }

        // A nullable of anything is always safely null, so this check comes before every other shape.
        if (type[type.Length - 1] == '?')
        {
            expression = "null";
            return true;
        }

        if (type.EndsWith("[]", StringComparison.Ordinal))
        {
            var elementType = type.Substring(0, type.Length - 2).Trim();
            expression = elementType.Length == 0 ? UnknownTypeDefault : "Array.Empty<" + elementType + ">()";
            return elementType.Length != 0;
        }

        var genericStart = type.IndexOf('<');
        if (genericStart < 0)
        {
            return TrySimple(type, out expression);
        }

        var outerName = StripNamespace(type.Substring(0, genericStart).Trim());
        var argumentText = ExtractGenericArguments(type, genericStart);
        var arguments = SplitTopLevel(argumentText);

        switch (outerName)
        {
            case "List":
            case "IList":
            case "ICollection":
            case "IReadOnlyList":
            case "IReadOnlyCollection":
            case "Collection":
                expression = "new List<" + argumentText + ">()";
                return true;

            case "IEnumerable":
            case "IQueryable":
            case "IOrderedQueryable":
            case "IAsyncEnumerable":
                expression = "Enumerable.Empty<" + argumentText + ">()";
                return true;

            case "HashSet":
            case "ISet":
            case "IReadOnlySet":
                expression = "new HashSet<" + argumentText + ">()";
                return true;

            case "Dictionary":
            case "IDictionary":
            case "IReadOnlyDictionary":
                expression = arguments.Count == 2
                    ? "new Dictionary<" + argumentText + ">()"
                    : UnknownTypeDefault;
                return arguments.Count == 2;

            case "Nullable":
                expression = "null";
                return true;

            default:
                expression = "default(" + type + ")!";
                return false;
        }
    }

    private static bool TrySimple(string type, out string expression)
    {
        switch (StripNamespace(type))
        {
            case "int":
            case "Int32":
                expression = "0";
                return true;
            case "long":
            case "Int64":
                expression = "0L";
                return true;
            case "uint":
            case "UInt32":
                expression = "0u";
                return true;
            case "ulong":
            case "UInt64":
                expression = "0UL";
                return true;
            case "short":
            case "Int16":
                expression = "(short)0";
                return true;
            case "ushort":
            case "UInt16":
                expression = "(ushort)0";
                return true;
            case "byte":
            case "Byte":
                expression = "(byte)0";
                return true;
            case "sbyte":
            case "SByte":
                expression = "(sbyte)0";
                return true;
            case "decimal":
            case "Decimal":
                expression = "0m";
                return true;
            case "double":
            case "Double":
                expression = "0d";
                return true;
            case "float":
            case "Single":
                expression = "0f";
                return true;
            case "bool":
            case "Boolean":
                expression = "false";
                return true;
            case "char":
            case "Char":
                expression = "'\\0'";
                return true;
            case "string":
            case "String":
                expression = "\"\"";
                return true;
            case "DateTime":
                expression = "DateTime.Now";
                return true;
            case "DateTimeOffset":
                expression = "DateTimeOffset.Now";
                return true;
            case "DateOnly":
                expression = "DateOnly.FromDateTime(DateTime.Today)";
                return true;
            case "TimeOnly":
                expression = "TimeOnly.FromDateTime(DateTime.Now)";
                return true;
            case "TimeSpan":
                expression = "TimeSpan.Zero";
                return true;
            case "Guid":
                expression = "Guid.Empty";
                return true;
            case "CancellationToken":
                expression = "CancellationToken.None";
                return true;
            default:
                // An unqualified PascalCase name with no generic arguments is most often an enum, and
                // `default(SomeEnum)` is a legal value, so `default!` is emitted with a diagnostic instead of
                // guessing a member name that may not exist.
                expression = LooksLikeEnum(type) ? UnknownTypeDefault : "default(" + type + ")!";
                return false;
        }
    }

    /// <summary>Whether a type name has the shape the synthesizer treats as a probable enum.</summary>
    /// <param name="declaredTypeText">The declared type text.</param>
    /// <returns><see langword="true"/> for an unqualified, non-generic, capitalised name.</returns>
    public static bool LooksLikeEnum(string? declaredTypeText)
    {
        var type = Normalize(declaredTypeText);
        return type.Length > 0
            && char.IsUpper(type[0])
            && type.IndexOf('<') < 0
            && type.IndexOf('.') < 0
            && type.IndexOf('[') < 0
            && type[type.Length - 1] != '?';
    }

    private static string Normalize(string? declaredTypeText)
    {
        if (declaredTypeText is null)
        {
            return string.Empty;
        }

        var type = declaredTypeText.Trim();
        if (type.StartsWith("global::", StringComparison.Ordinal))
        {
            type = type.Substring("global::".Length);
        }

        return type;
    }

    private static string StripNamespace(string type)
    {
        var lastDot = type.LastIndexOf('.');
        return lastDot < 0 ? type : type.Substring(lastDot + 1);
    }

    private static string ExtractGenericArguments(string type, int genericStart)
    {
        var lastClose = type.LastIndexOf('>');
        return lastClose > genericStart
            ? type.Substring(genericStart + 1, lastClose - genericStart - 1).Trim()
            : string.Empty;
    }

    private static IReadOnlyList<string> SplitTopLevel(string arguments)
    {
        var parts = new List<string>();
        if (arguments.Length == 0)
        {
            return parts;
        }

        var depth = 0;
        var current = new StringBuilder();
        foreach (var c in arguments)
        {
            switch (c)
            {
                case '<':
                    depth++;
                    current.Append(c);
                    break;
                case '>':
                    depth--;
                    current.Append(c);
                    break;
                case ',' when depth == 0:
                    parts.Add(current.ToString().Trim());
                    current.Length = 0;
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        parts.Add(current.ToString().Trim());
        return parts;
    }
}
