namespace Linkbase.Core.Extraction;

public sealed record ExtractionProgress(string Message, int Completed, int Total);

public sealed class ExtractionFailedException(string message) : Exception(message);

public static class TextChunker
{
    public const int DefaultMaxLength = 24_000;

    // Splits on paragraph breaks where it can, so a route's line stays whole. No text is dropped.
    public static List<string> Split(string text, int maxLength = DefaultMaxLength)
    {
        var chunks = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var paragraph in text.ReplaceLineEndings("\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var piece in SplitLongParagraph(paragraph, maxLength))
            {
                if (current.Length > 0 && current.Length + piece.Length + 2 > maxLength)
                {
                    chunks.Add(current.ToString());
                    current.Clear();
                }

                if (current.Length > 0)
                {
                    current.Append("\n\n");
                }

                current.Append(piece);
            }
        }

        if (current.Length > 0)
        {
            chunks.Add(current.ToString());
        }

        return chunks;
    }

    private static IEnumerable<string> SplitLongParagraph(string paragraph, int maxLength)
    {
        var remaining = paragraph;
        while (remaining.Length > maxLength)
        {
            var cut = remaining.LastIndexOfAny(['\n', ' '], maxLength - 1);
            if (cut <= 0)
            {
                cut = maxLength;
            }

            yield return remaining[..cut];
            remaining = remaining[cut..].TrimStart();
        }

        if (remaining.Length > 0)
        {
            yield return remaining;
        }
    }
}
