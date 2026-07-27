namespace EFCoreSqlPreview.Services
{
    /// <summary>
    /// A snapshot of what the editor looked like when the user invoked the command.
    /// </summary>
    /// <remarks>
    /// Everything here is a plain value. The <c>ITextViewSnapshot</c> it came from is RPC-backed and is
    /// disposed as soon as the command returns, so nothing derived from it may outlive that call.
    /// </remarks>
    /// <param name="DocumentText">The full buffer contents, including unsaved edits.</param>
    /// <param name="DocumentPath">Absolute path of the document.</param>
    /// <param name="SelectionStart">Zero-based character offset where the selection starts.</param>
    /// <param name="SelectionLength">Length of the selection; zero means "use the caret's enclosing statement".</param>
    /// <param name="SelectedText">The selected text, or an empty string when the selection is a bare caret.</param>
    internal sealed record CapturedSelection(
        string DocumentText,
        string DocumentPath,
        int SelectionStart,
        int SelectionLength,
        string SelectedText);
}
