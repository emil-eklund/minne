using System.Diagnostics;
using System.Text;
using MailSearch.Config;
using MailSearch.Embeddings;
using MailSearch.Mail;
using MailSearch.Storage;
using MailSearch.Text;

namespace MailSearch;

public sealed record SyncProgress(string Folder, int Upserted, int Removed);

/// <summary>Coordinates fetching mail, cleaning and chunking bodies, and computing embeddings.</summary>
public sealed class Indexer
{
    private readonly SearchStore _store;
    private readonly IndexingConfig _config;

    public Indexer(SearchStore store, IndexingConfig config)
    {
        _store = store;
        _config = config;
    }

    public async Task<SyncProgress> SyncFolderAsync(IMailSource source, string folder, bool full, Action<SyncProgress>? progress, CancellationToken ct)
    {
        var state = full ? null : _store.GetSyncState(folder);
        int upserted = 0, removed = 0;
        var sw = Stopwatch.StartNew();

        await foreach (var change in source.GetChangesAsync(folder, state, s => _store.SetSyncState(folder, s), ct))
        {
            if (change.IsRemoved)
            {
                if (_store.DeleteMessage(change.Id)) removed++;
                continue;
            }
            Index(change.Message!);
            upserted++;
            if (upserted % 50 == 0 && sw.Elapsed > TimeSpan.FromSeconds(2))
            {
                progress?.Invoke(new SyncProgress(folder, upserted, removed));
                sw.Restart();
            }
        }
        var result = new SyncProgress(folder, upserted, removed);
        progress?.Invoke(result);
        return result;
    }

    /// <summary>Clean, chunk and store a message (used by sync and by tests).</summary>
    public void Index(MailMessage message)
    {
        var (body, chunks) = Prepare(message);
        _store.UpsertMessage(message, body, chunks);
    }

    /// <summary>
    /// Re-run cleaning and chunking for every stored message from its raw body (after changing cleaning rules or
    /// chunk settings). All chunks lose their embeddings and must be re-embedded afterwards.
    /// </summary>
    public int ReindexAll(Action<int>? progress = null)
    {
        var count = 0;
        foreach (var (rowId, message) in _store.EnumerateRaw().ToList())
        {
            var (body, chunks) = Prepare(message);
            _store.ReplaceContent(rowId, body, chunks);
            if (++count % 100 == 0) progress?.Invoke(count);
        }
        progress?.Invoke(count);
        return count;
    }

    private (string Body, List<string> Chunks) Prepare(MailMessage message)
    {
        var body = _config.CleanBodies ? BodyCleaner.Clean(message.Body) : message.Body.Trim();
        var header = BuildHeader(message);
        var pieces = TextChunker.Split(body, _config.ChunkSizeChars, _config.ChunkOverlapChars);
        var chunks = pieces.Count == 0
            ? [header.TrimEnd()]
            : pieces.Select(p => _config.IncludeHeaderInChunk ? header + p : p).ToList();
        return (body, chunks);
    }

    private static string BuildHeader(MailMessage m)
    {
        var sb = new StringBuilder();
        sb.Append("Subject: ").Append(m.Subject).Append('\n');
        if (m.From is not null) sb.Append("From: ").Append(m.From).Append('\n');
        sb.Append("Date: ").Append(m.Received.ToString("yyyy-MM-dd")).Append("\n\n");
        return sb.ToString();
    }

    /// <summary>Embed every chunk that does not have a vector yet.</summary>
    public async Task<int> EmbedPendingAsync(IEmbeddingProvider provider, Action<int, int>? progress, CancellationToken ct)
    {
        var storedModel = _store.GetMeta("embedding_model");
        if (storedModel is not null && storedModel != provider.ModelId)
            throw new InvalidOperationException(
                $"Index already contains embeddings from '{storedModel}'; configured model is '{provider.ModelId}'. Run 'embed --reset' to re-embed everything.");
        _store.SetMeta("embedding_model", provider.ModelId);
        _store.SetMeta("embedding_dims", provider.Dimensions.ToString());

        var total = _store.GetStats() is var s ? (int)(s.Chunks - s.EmbeddedChunks) : 0;
        var done = 0;
        var batchSize = Math.Max(provider.BatchSize * 8, 64);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var pending = _store.GetChunksWithoutEmbedding(batchSize);
            if (pending.Count == 0) break;
            var vectors = await provider.EmbedDocumentsAsync(pending.Select(p => p.Text).ToList(), ct);
            _store.SetEmbeddings(pending.Select((p, i) => (p.ChunkId, vectors[i])).ToList());
            done += pending.Count;
            progress?.Invoke(done, total);
        }
        return done;
    }
}
