using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace EFCoreSqlPreview.Core.Analysis;

/// <summary>
/// Turns a text selection in a C# document into a fully specified query-preview request.
/// </summary>
/// <remarks>
/// Implementations are pure: no file I/O, no <c>Workspace</c>, no <c>Compilation</c>, no <c>SemanticModel</c>.
/// The real C# compiler does the semantic work later, when the generated worker is built against the user's project.
/// </remarks>
public interface ILinqSelectionAnalyzer
{
    /// <summary>Analyses a selection in a document.</summary>
    /// <param name="documentText">The full document text, including unsaved edits.</param>
    /// <param name="selection">The selection span; may be empty (a caret), reversed, or out of range.</param>
    /// <param name="options">Analysis options.</param>
    /// <returns>The analysis result; never <see langword="null"/>, never throws for malformed input.</returns>
    QueryAnalysisResult Analyze(string documentText, TextSpan selection, AnalyzerOptions options);

    /// <summary>Analyses a selection using an already-parsed syntax tree.</summary>
    /// <param name="tree">A tree parsed with <see cref="LinqSelectionAnalyzer.ParseOptions"/>.</param>
    /// <param name="selection">The selection span; may be empty, reversed, or out of range.</param>
    /// <param name="options">Analysis options.</param>
    /// <returns>The analysis result; never <see langword="null"/>, never throws for malformed input.</returns>
    QueryAnalysisResult Analyze(SyntaxTree tree, TextSpan selection, AnalyzerOptions options);
}
