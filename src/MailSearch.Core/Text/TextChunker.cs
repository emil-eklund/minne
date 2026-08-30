namespace MailSearch.Text;

/// <summary>Splits text into overlapping chunks, preferring paragraph and sentence boundaries.</summary>
public static class TextChunker
{
    public static IReadOnlyList<string> Split(string text, int chunkSize, int overlap)
    {
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        if (overlap < 0 || overlap >= chunkSize) throw new ArgumentOutOfRangeException(nameof(overlap));

        text = text.Trim();
        if (text.Length == 0) return [];
        if (text.Length <= chunkSize) return [text];

        var chunks = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var end = Math.Min(start + chunkSize, text.Length);
            if (end < text.Length)
            {
                // look for a natural boundary in the last 40% of the window
                var floor = start + (int)(chunkSize * 0.6);
                var cut = LastBoundary(text, floor, end);
                if (cut > floor) end = cut;
            }

            var piece = text[start..end].Trim();
            if (piece.Length > 0) chunks.Add(piece);
            if (end >= text.Length) break;

            var next = end - overlap;
            start = next <= start ? end : next;
        }
        return chunks;
    }

    private static int LastBoundary(string text, int floor, int end)
    {
        // Paragraph break beats sentence end beats whitespace.
        var p = text.LastIndexOf("\n\n", end - 1, end - floor, StringComparison.Ordinal);
        if (p > floor) return p + 2;
        for (var i = end - 1; i > floor; i--)
        {
            var c = text[i];
            if ((c == '.' || c == '!' || c == '?' || c == '\n') && i + 1 < text.Length && char.IsWhiteSpace(text[i + 1]))
                return i + 1;
        }
        var s = text.LastIndexOf(' ', end - 1, end - floor);
        return s > floor ? s + 1 : end;
    }
}
