using Microsoft.CodeAnalysis.Text;

namespace EFCoreSqlPreview.Core.Analysis;

/// <summary>
/// Makes a raw editor selection safe to hand to Roslyn's <c>FindNode</c>.
/// </summary>
/// <remarks>
/// Three failure modes make this mandatory rather than cosmetic: a reversed span throws from
/// <see cref="TextSpan.FromBounds"/>; a span past the end of the document throws from <c>FindNode</c>; and a
/// trailing newline promotes the resolved node all the way up to the enclosing block, which silently loses
/// the query. Dragging to select a line is the most common gesture, so the third case is the common case.
/// </remarks>
public static class SelectionNormalizer
{
    /// <summary>
    /// Swaps a reversed span, clamps it to the document, trims whitespace from both ends, and snaps an empty
    /// selection onto an adjacent non-whitespace character.
    /// </summary>
    /// <param name="documentText">The full document text.</param>
    /// <param name="selection">The raw selection from the editor.</param>
    /// <returns>A span that is always safe to pass to <c>FindNode</c>.</returns>
    public static TextSpan Normalize(string documentText, TextSpan selection)
    {
        if (documentText is null || documentText.Length == 0)
        {
            return new TextSpan(0, 0);
        }

        var length = documentText.Length;
        var start = selection.Start;
        var end = selection.End;

        if (end < start)
        {
            var swap = start;
            start = end;
            end = swap;
        }

        start = Clamp(start, 0, length);
        end = Clamp(end, 0, length);

        while (start < end && char.IsWhiteSpace(documentText[start]))
        {
            start++;
        }

        while (end > start && char.IsWhiteSpace(documentText[end - 1]))
        {
            end--;
        }

        if (start == end)
        {
            // A caret (or an all-whitespace drag). Snap onto an adjacent non-whitespace character so that
            // FindNode lands on a token instead of on the enclosing block.
            var touchesTokenOnTheLeft = start > 0 && !char.IsWhiteSpace(documentText[start - 1]);
            if (!touchesTokenOnTheLeft)
            {
                while (start < length && char.IsWhiteSpace(documentText[start]))
                {
                    start++;
                }
            }

            if (start >= length)
            {
                start = length - 1;
            }

            end = start;
        }

        return TextSpan.FromBounds(start, end);
    }

    private static int Clamp(int value, int min, int max)
        => value < min ? min : value > max ? max : value;
}
