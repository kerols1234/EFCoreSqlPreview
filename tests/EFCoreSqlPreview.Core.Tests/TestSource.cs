using Microsoft.CodeAnalysis.Text;

namespace EFCoreSqlPreview.Core.Tests;

/// <summary>
/// Turns marked-up test source into the (text, span) pair the analyzer takes.
/// </summary>
/// <remarks>
/// Lives in the test project on purpose: markers are a testing convenience and must not leak into the
/// public contract of the Core library.
/// </remarks>
public static class TestSource
{
    private const string SelectionStart = "[|";
    private const string SelectionEnd = "|]";
    private const string CaretMarker = "$$";

    /// <summary>
    /// Extracts a selection delimited by <c>[|</c> and <c>|]</c>.
    /// </summary>
    /// <param name="markedUp">Source containing exactly one selection marker pair.</param>
    /// <returns>The source with markers removed, and the span they delimited.</returns>
    /// <exception cref="ArgumentException">The markers are missing or out of order.</exception>
    public static (string Text, TextSpan Span) Parse(string markedUp)
    {
        var start = markedUp.IndexOf(SelectionStart, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new ArgumentException($"Source contains no '{SelectionStart}' marker.", nameof(markedUp));
        }

        var withoutStart = markedUp.Remove(start, SelectionStart.Length);

        var end = withoutStart.IndexOf(SelectionEnd, StringComparison.Ordinal);
        if (end < start)
        {
            throw new ArgumentException($"Source contains no '{SelectionEnd}' marker after '{SelectionStart}'.", nameof(markedUp));
        }

        var text = withoutStart.Remove(end, SelectionEnd.Length);
        return (text, TextSpan.FromBounds(start, end));
    }

    /// <summary>
    /// Extracts a zero-length selection marked with <c>$$</c>.
    /// </summary>
    /// <param name="markedUp">Source containing exactly one caret marker.</param>
    /// <returns>The source with the marker removed, and an empty span at its position.</returns>
    /// <exception cref="ArgumentException">The marker is missing.</exception>
    public static (string Text, TextSpan Span) Caret(string markedUp)
    {
        var position = markedUp.IndexOf(CaretMarker, StringComparison.Ordinal);
        if (position < 0)
        {
            throw new ArgumentException($"Source contains no '{CaretMarker}' marker.", nameof(markedUp));
        }

        return (markedUp.Remove(position, CaretMarker.Length), new TextSpan(position, 0));
    }
}
