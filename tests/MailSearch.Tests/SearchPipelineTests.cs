using MailSearch.Config;
using MailSearch.Eval;
using MailSearch.Mail;
using MailSearch.Search;
using MailSearch.Storage;

namespace MailSearch.Tests;

/// <summary>End-to-end: index → embed → keyword/vector/hybrid search → eval, against a temp SQLite file.</summary>
public sealed class SearchPipelineTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mailsearch-test-{Guid.NewGuid():N}.db");
    private readonly SearchStore _store;
    private readonly Indexer _indexer;
    private readonly HybridSearcher _searcher;

    public SearchPipelineTests()
    {
        _store = new SearchStore(_dbPath);
        _indexer = new Indexer(_store, new IndexingConfig { ChunkSizeChars = 200, ChunkOverlapChars = 40 });
        _searcher = new HybridSearcher(_store, new SearchConfig(), _ => Task.FromResult<Embeddings.IEmbeddingProvider>(new FakeEmbeddingProvider()));

        Seed("m1", "Kick-off agenda", "anna@example.se", "2024-06-03", "Here is the kick-off agenda for Friday. We start 09:00.", attachments: true);
        Seed("m2", "Invoice INV-20431", "billing@contoso.com", "2025-02-10", "Please find invoice INV-20431 attached. Payment due in 30 days.", attachments: true);
        Seed("m3", "Lunch?", "bob@example.se", "2025-03-01", "Want to grab lunch on Thursday?");
        Seed("m4", "Budget review", "anna@example.se", "2025-01-20", "Attaching the budget review notes from Monday. " + string.Concat(Enumerable.Repeat("More detail about budget lines and forecasts. ", 20)));
    }

    private void Seed(string id, string subject, string from, string date, string body, bool attachments = false) =>
        _indexer.Index(new MailMessage
        {
            Id = id, InternetMessageId = $"<{id}@test>", Folder = "inbox", Subject = subject,
            From = new MailAddress(null, from), To = [new MailAddress("Emil", "emil@example.se")],
            Received = DateTimeOffset.Parse(date + "T10:00:00Z"), HasAttachments = attachments, Body = body,
        });

    [Fact]
    public async Task Keyword_search_finds_exact_identifier()
    {
        var hits = await _searcher.SearchAsync("INV-20431", SearchMode.Keyword, 10, CancellationToken.None);
        Assert.Equal("m2", hits[0].Message.Id);
    }

    [Fact]
    public async Task Keyword_search_falls_back_to_any_term_when_and_matches_nothing()
    {
        var hits = await _searcher.SearchAsync("lunch agenda", SearchMode.Keyword, 10, CancellationToken.None);
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public async Task Vector_search_requires_embeddings_and_finds_similar_wording()
    {
        var done = await _indexer.EmbedPendingAsync(new FakeEmbeddingProvider(), null, CancellationToken.None);
        Assert.True(done >= 4);

        var hits = await _searcher.SearchAsync("kickoff", SearchMode.Vector, 10, CancellationToken.None);
        Assert.Equal("m1", hits[0].Message.Id);
        Assert.Null(hits[0].KeywordRank);
        Assert.Equal(1, hits[0].VectorRank);
    }

    [Fact]
    public async Task Hybrid_combines_both_and_reports_source_ranks()
    {
        await _indexer.EmbedPendingAsync(new FakeEmbeddingProvider(), null, CancellationToken.None);
        var hits = await _searcher.SearchAsync("invoice payment", SearchMode.Hybrid, 10, CancellationToken.None);
        Assert.Equal("m2", hits[0].Message.Id);
        Assert.NotNull(hits[0].KeywordRank);
        Assert.NotNull(hits[0].VectorRank);
    }

    [Fact]
    public async Task Filters_restrict_results()
    {
        await _indexer.EmbedPendingAsync(new FakeEmbeddingProvider(), null, CancellationToken.None);

        var fromAnna = await _searcher.SearchAsync("agenda budget from:anna", SearchMode.Hybrid, 10, CancellationToken.None);
        Assert.All(fromAnna, h => Assert.Equal("anna@example.se", h.Message.SenderAddress));

        var recent = await _searcher.SearchAsync("agenda after:2025-01", SearchMode.Keyword, 10, CancellationToken.None);
        Assert.DoesNotContain(recent, h => h.Message.Id == "m1");

        var filterOnly = await _searcher.SearchAsync("has:attachment", SearchMode.Hybrid, 10, CancellationToken.None);
        Assert.Equal(["m2", "m1"], filterOnly.Select(h => h.Message.Id));
    }

    [Fact]
    public async Task Reindexing_a_message_replaces_old_chunks()
    {
        Seed("m3", "Lunch?", "bob@example.se", "2025-03-01", "Lunch is cancelled, sorry.");
        var stats = _store.GetStats();
        Assert.Equal(4, stats.Messages);
        var hits = await _searcher.SearchAsync("cancelled", SearchMode.Keyword, 10, CancellationToken.None);
        Assert.Single(hits);
        Assert.Contains("cancelled", hits[0].Snippet);
    }

    [Fact]
    public async Task Reindex_rebuilds_content_from_raw_bodies_and_clears_embeddings()
    {
        await _indexer.EmbedPendingAsync(new FakeEmbeddingProvider(), null, CancellationToken.None);
        var before = _store.GetStats();
        Assert.Equal(before.Chunks, before.EmbeddedChunks);

        var count = _indexer.ReindexAll();
        var after = _store.GetStats();
        Assert.Equal(4, count);
        Assert.Equal(4, after.Messages);
        Assert.Equal(0, after.EmbeddedChunks);
        Assert.Equal(before.Chunks, after.Chunks);
        // content still searchable after reindex
        var hits = await _searcher.SearchAsync("INV-20431", SearchMode.Keyword, 10, CancellationToken.None);
        Assert.Equal("m2", hits[0].Message.Id);
    }

    [Fact]
    public void Deleting_a_message_removes_it_from_fts()
    {
        Assert.True(_store.DeleteMessage("m3"));
        Assert.False(_store.DeleteMessage("m3"));
        Assert.Empty(_store.FullTextSearch("\"lunch\"", null, 10));
    }

    [Fact]
    public async Task Model_mismatch_is_detected()
    {
        await _indexer.EmbedPendingAsync(new FakeEmbeddingProvider(), null, CancellationToken.None);
        _store.SetMeta("embedding_model", "someone-else");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _searcher.SearchAsync("kickoff", SearchMode.Vector, 10, CancellationToken.None));
    }

    [Fact]
    public async Task Eval_reports_recall_per_mode()
    {
        await _indexer.EmbedPendingAsync(new FakeEmbeddingProvider(), null, CancellationToken.None);
        var set = new EvalSet
        {
            Queries =
            [
                new EvalCase { Query = "INV-20431", Expected = ["<m2@test>"] },
                new EvalCase { Query = "kickoff", Expected = ["m1"] },
                new EvalCase { Query = "nothing", Expected = ["rowid:99999"] }, // unresolvable
            ],
        };
        var results = await new EvalRunner(_searcher, _store).RunAsync(set, [SearchMode.Keyword, SearchMode.Vector, SearchMode.Hybrid], 10, CancellationToken.None);
        var hybrid = results.Single(r => r.Mode == SearchMode.Hybrid);
        Assert.Equal(2, hybrid.Total);
        Assert.Equal(1.0, hybrid.RecallAt(10));
        Assert.Equal(1.0, hybrid.RecallAt(1));
    }

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_dbPath)!, Path.GetFileName(_dbPath) + "*"))
            try { File.Delete(f); } catch (IOException) { }
    }
}
