using MailSearch.Text;

namespace MailSearch.Tests;

public class TextChunkerTests
{
    [Fact]
    public void Short_text_is_single_chunk()
    {
        var chunks = TextChunker.Split("hello world", 100, 10);
        Assert.Single(chunks);
        Assert.Equal("hello world", chunks[0]);
    }

    [Fact]
    public void Long_text_is_split_with_overlap_and_covers_everything()
    {
        var sentences = Enumerable.Range(1, 60).Select(i => $"Sentence number {i} talks about topic {i % 7}.");
        var text = string.Join(" ", sentences);
        var chunks = TextChunker.Split(text, 300, 60);

        Assert.True(chunks.Count > 3);
        Assert.All(chunks, c => Assert.True(c.Length <= 300));
        // every sentence must appear in at least one chunk
        foreach (var s in sentences) Assert.Contains(chunks, c => c.Contains(s));
        // consecutive chunks share text (overlap)
        Assert.Contains(chunks[1].Split(' ')[0], chunks[0]);
    }

    [Fact]
    public void Prefers_paragraph_boundaries()
    {
        var text = new string('a', 200) + "\n\n" + new string('b', 200);
        var chunks = TextChunker.Split(text, 260, 20);
        Assert.Equal(2, chunks.Count);
        Assert.All(chunks[0], c => Assert.Equal('a', c));
    }

    [Fact]
    public void Empty_returns_no_chunks() => Assert.Empty(TextChunker.Split("  ", 100, 10));
}
