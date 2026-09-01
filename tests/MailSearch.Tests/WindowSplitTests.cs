using MailSearch.Embeddings;

namespace MailSearch.Tests;

public class WindowSplitTests
{
    private const int Bos = 0, Eos = 2;

    private static int[] Sequence(int contentTokens)
    {
        var ids = new int[contentTokens + 2];
        ids[0] = Bos;
        for (var i = 0; i < contentTokens; i++) ids[i + 1] = 100 + i;
        ids[^1] = Eos;
        return ids;
    }

    [Fact]
    public void Sequence_within_budget_is_returned_unchanged()
    {
        var tokens = Sequence(10);
        var windows = OnnxEmbeddingProvider.SplitIntoWindows(tokens, 128).ToList();
        Assert.Single(windows);
        Assert.Same(tokens, windows[0]);
    }

    [Theory]
    [InlineData(127, 128)] // one over the content budget of 126
    [InlineData(300, 128)]
    [InlineData(1000, 128)]
    [InlineData(50, 16)]
    public void Windows_cover_all_content_in_order_within_budget(int contentTokens, int maxTokens)
    {
        var tokens = Sequence(contentTokens);
        var windows = OnnxEmbeddingProvider.SplitIntoWindows(tokens, maxTokens).ToList();

        Assert.Equal((int)Math.Ceiling(contentTokens / (double)(maxTokens - 2)), windows.Count);
        Assert.All(windows, w =>
        {
            Assert.InRange(w.Length, 3, maxTokens);
            Assert.Equal(Bos, w[0]);
            Assert.Equal(Eos, w[^1]);
        });
        var recombined = windows.SelectMany(w => w[1..^1]).ToArray();
        Assert.Equal(tokens[1..^1], recombined);
    }

    [Fact]
    public void Content_that_exactly_fills_windows_produces_no_empty_window()
    {
        var tokens = Sequence(2 * 126); // exactly two full windows at maxTokens 128
        var windows = OnnxEmbeddingProvider.SplitIntoWindows(tokens, 128).ToList();
        Assert.Equal(2, windows.Count);
        Assert.All(windows, w => Assert.Equal(128, w.Length));
    }

    [Fact]
    public void Degenerate_budget_still_makes_progress()
    {
        var tokens = Sequence(5);
        var windows = OnnxEmbeddingProvider.SplitIntoWindows(tokens, 2).ToList();
        Assert.Equal(5, windows.Count); // one content token per window
        Assert.Equal(tokens[1..^1], windows.SelectMany(w => w[1..^1]).ToArray());
    }
}
