using System.Text;
using EFCoreSqlPreview.Core.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace EFCoreSqlPreview.Core.Tests.Analysis;

/// <summary>
/// Wraps a marked-up snippet in a realistic document so the analyzer sees the field declarations,
/// usings and namespace it would see in a real project.
/// </summary>
internal static class Fixture
{
    /// <summary>The usings every generated document carries unless a test overrides them.</summary>
    public const string DefaultUsings =
        "using System;\r\n" +
        "using System.Collections.Generic;\r\n" +
        "using System.Linq;\r\n" +
        "using System.Threading.Tasks;\r\n" +
        "using Microsoft.EntityFrameworkCore;";

    /// <summary>The default enclosing member: async, so awaited terminals are legal.</summary>
    public const string AsyncSignature = "public async Task RunAsync()";

    /// <summary>A synchronous enclosing member, for the "async terminal not awaited" cases.</summary>
    public const string SyncSignature = "public void Run()";

    /// <summary>Builds a complete document around a method body.</summary>
    /// <param name="body">The method body, containing the selection markers.</param>
    /// <param name="members">Extra type members to declare before the method.</param>
    /// <param name="signature">The enclosing method signature.</param>
    /// <param name="usings">The using block to emit at the top of the file.</param>
    /// <param name="namespaceName">The file-scoped namespace, or <see langword="null"/> for none.</param>
    /// <returns>The marked-up document text.</returns>
    public static string Document(
        string body,
        string members = "",
        string signature = AsyncSignature,
        string usings = DefaultUsings,
        string? namespaceName = "Demo.Services")
    {
        var builder = new StringBuilder();
        builder.AppendLine(usings);
        builder.AppendLine();

        if (namespaceName is not null)
        {
            builder.AppendLine("namespace " + namespaceName + ";");
            builder.AppendLine();
        }

        builder.AppendLine("public class Sample");
        builder.AppendLine("{");
        builder.AppendLine("    private readonly AppDbContext _db;");
        builder.AppendLine("    private readonly AppDbContext _context;");

        if (members.Length > 0)
        {
            builder.AppendLine(members);
        }

        builder.AppendLine("    " + signature);
        builder.AppendLine("    {");
        builder.AppendLine(body);
        builder.AppendLine("    }");
        builder.AppendLine("}");

        return builder.ToString();
    }

    /// <summary>Analyses a method body containing <c>[|...|]</c> markers.</summary>
    /// <param name="body">The method body.</param>
    /// <param name="members">Extra type members.</param>
    /// <param name="signature">The enclosing method signature.</param>
    /// <param name="options">Analyzer options; defaults when omitted.</param>
    /// <param name="usings">The using block.</param>
    /// <returns>The analysis result.</returns>
    public static QueryAnalysisResult Analyze(
        string body,
        string members = "",
        string signature = AsyncSignature,
        AnalyzerOptions? options = null,
        string usings = DefaultUsings)
        => AnalyzeRaw(Document(body, members, signature, usings), options);

    /// <summary>Analyses a single expression, selecting exactly that expression.</summary>
    /// <param name="expression">The query expression, without markers.</param>
    /// <param name="members">Extra type members.</param>
    /// <param name="signature">The enclosing method signature.</param>
    /// <param name="options">Analyzer options; defaults when omitted.</param>
    /// <returns>The analysis result.</returns>
    public static QueryAnalysisResult AnalyzeExpression(
        string expression,
        string members = "",
        string signature = AsyncSignature,
        AnalyzerOptions? options = null)
        => Analyze("        var result = [|" + expression + "|];", members, signature, options);

    /// <summary>Analyses a complete marked-up document.</summary>
    /// <param name="markedUpDocument">The document, containing <c>[|...|]</c> markers.</param>
    /// <param name="options">Analyzer options; defaults when omitted.</param>
    /// <returns>The analysis result.</returns>
    public static QueryAnalysisResult AnalyzeRaw(string markedUpDocument, AnalyzerOptions? options = null)
    {
        var (text, span) = TestSource.Parse(markedUpDocument);
        return LinqSelectionAnalyzer.Instance.Analyze(text, span, options ?? AnalyzerOptions.Default);
    }

    /// <summary>Analyses a complete document marked with a zero-length <c>$$</c> caret.</summary>
    /// <param name="markedUpDocument">The document, containing one <c>$$</c> marker.</param>
    /// <returns>The analysis result.</returns>
    public static QueryAnalysisResult AnalyzeCaretRaw(string markedUpDocument)
    {
        var (text, span) = TestSource.Caret(markedUpDocument);
        return LinqSelectionAnalyzer.Instance.Analyze(text, span, AnalyzerOptions.Default);
    }

    /// <summary>Parses a document with the analyzer's own parse options.</summary>
    /// <param name="text">The document text.</param>
    /// <returns>The syntax root.</returns>
    public static SyntaxNode ParseRoot(string text)
        => CSharpSyntaxTree.ParseText(text, LinqSelectionAnalyzer.ParseOptions).GetRoot();

    /// <summary>Finds the first node of a given kind in a parsed document.</summary>
    /// <typeparam name="T">The node type to look for.</typeparam>
    /// <param name="text">The document text.</param>
    /// <returns>The first matching node.</returns>
    public static T FirstNode<T>(string text)
        where T : SyntaxNode
        => ParseRoot(text).DescendantNodes().OfType<T>().First();

    /// <summary>Produces a span covering the first occurrence of a substring.</summary>
    /// <param name="text">The document text.</param>
    /// <param name="fragment">The substring to locate.</param>
    /// <returns>The span of the substring.</returns>
    public static TextSpan SpanOf(string text, string fragment)
    {
        var index = text.IndexOf(fragment, StringComparison.Ordinal);
        index.ShouldBeGreaterThanOrEqualTo(0, "fragment '" + fragment + "' is not in the document");
        return new TextSpan(index, fragment.Length);
    }
}
