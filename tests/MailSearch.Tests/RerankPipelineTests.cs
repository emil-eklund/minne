using MailSearch.Config;
using MailSearch.Embeddings;
using MailSearch.Mail;
using MailSearch.Rerank;
using MailSearch.Search;
using MailSearch.Storage;

namespace MailSearch.Tests;

/// <summary>Deterministic stand-in: scores a passage by how often the query words occur in it.</summary>
public sealed class FakeReranker : IReranker
{
    public string ModelId => "fake:overlap";

    public Task<float[]> ScoreAsync(string query, IReadOnlyList<string> passages, CancellationToken ct)
    {
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return Task.FromResult(passages.Select(p => (float)words.Sum(w => Count(p.ToLowerInvariant(), w))).ToArray());
    }

    private static int Count(string text, string word)
    {
        int count = 0, i = 0;
        while ((i = text.IndexOf(word, i, StringComparison.Ordinal)) >= 0) { count++; i += word.Length; }
        return count;
    }

    public void Dispose() { }
}

public sealed class RerankPipelineTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mailsearch-rerank-test-{Guid.NewGuid():N}.db");
    private readonly SearchStore _store;
    private readonly Indexer _indexer;
    private readonly HybridSearcher _searcher;

    public RerankPipelineTests()
    {
        _store = new SearchStore(_dbPath);
        _indexer = new Indexer(_store, new IndexingConfig { ChunkSizeChars = 200, ChunkOverlapChars = 40 });
        _searcher = new HybridSearcher(_store, new SearchConfig(),
            _ => Task.FromResult<IEmbeddingProvider>(new FakeEmbeddingProvider()),
            _ => Task.FromResult<IReranker>(new FakeReranker()));

        // keyword retrieval (subject weighted 3x) favours r1; the reranker's passage scores favour r3
        Seed("r1", "Budget", "anna@example.se", "Nothing else to see here.");
        Seed("r2", "Planning", "bob@example.se", "The budget draft and the budget summary.");
        Seed("r3", "Notes", "cid@example.se", "budget budget budget review for the quarter.");
    }

    private void Seed(string id, string subject, string from, string body) =>
        _indexer.Index(new MailMessage
        {
            Id = id, InternetMessageId = $"<{id}@test>", Folder = "inbox", Subject = subject,
            From = new MailAddress(null, from), To = [new MailAddress("Karin", "karin@example.se")],
            Received = DateTimeOffset.Parse("2025-05-01T10:00:00Z"), Body = body,
        });

    [Fact]
    public async Task Rerank_orders_candidates_by_reranker_score()
    {
        await _indexer.EmbedPendingAsync(new FakeEmbeddingProvider(), null, CancellationToken.None);
        var hits = await _searcher.SearchAsync("budget", SearchMode.Rerank, 10, CancellationToken.None);
        Assert.Equal(["r3", "r2", "r1"], hits.Select(h => h.Message.Id));
        Assert.Equal([1, 2, 3], hits.Select(h => h.Rank));
        Assert.True(hits[0].Score > hits[1].Score);
        Assert.True(hits[1].Score > hits[2].Score);
    }

    [Fact]
    public async Task Rerank_still_reports_which_retrievers_found_a_hit()
    {
        await _indexer.EmbedPendingAsync(new FakeEmbeddingProvider(), null, CancellationToken.None);
        var hits = await _searcher.SearchAsync("budget", SearchMode.Rerank, 10, CancellationToken.None);
        Assert.All(hits, h => Assert.True(h.KeywordRank is not null || h.VectorRank is not null));
    }

    [Fact]
    public async Task Rerank_without_configured_reranker_throws()
    {
        var bare = new HybridSearcher(_store, new SearchConfig(),
            _ => Task.FromResult<IEmbeddingProvider>(new FakeEmbeddingProvider()));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => bare.SearchAsync("budget", SearchMode.Rerank, 10, CancellationToken.None));
    }

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_dbPath)!, Path.GetFileName(_dbPath) + "*"))
            try { File.Delete(f); } catch (IOException) { }
    }
}
