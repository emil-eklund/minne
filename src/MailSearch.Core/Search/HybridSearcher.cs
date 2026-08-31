using System.Numerics.Tensors;
using MailSearch.Config;
using MailSearch.Embeddings;
using MailSearch.Rerank;
using MailSearch.Storage;

namespace MailSearch.Search;

public enum SearchMode { Hybrid, Keyword, Vector, Rerank }

public sealed record SearchHit(
    int Rank, double Score, MessageRow Message, string Snippet, int? KeywordRank, int? VectorRank);

/// <summary>
/// Keyword (FTS5) + dense retrieval fused with RRF, with structured filters applied first.
/// <see cref="SearchMode.Rerank"/> retrieves like hybrid, then re-scores the top fused candidates
/// with a cross-encoder so the best answers surface even when both retrievers ranked them low.
/// </summary>
public sealed class HybridSearcher
{
    /// <summary>Longest passage handed to the reranker; its tokenizer truncates further.</summary>
    private const int MaxPassageChars = 2000;

    private readonly SearchStore _store;
    private readonly SearchConfig _config;
    private readonly Func<CancellationToken, Task<IEmbeddingProvider>> _embedder;
    private readonly Func<CancellationToken, Task<IReranker>>? _rerankerFactory;
    private readonly int _rerankDepth;
    private EmbeddingIndex? _index;
    private IEmbeddingProvider? _provider;
    private IReranker? _reranker;

    public HybridSearcher(
        SearchStore store, SearchConfig config,
        Func<CancellationToken, Task<IEmbeddingProvider>> embedder,
        Func<CancellationToken, Task<IReranker>>? reranker = null,
        int rerankDepth = 50)
    {
        _store = store;
        _config = config;
        _embedder = embedder;
        _rerankerFactory = reranker;
        _rerankDepth = rerankDepth;
    }

    /// <summary>
    /// Releases the in-memory vector index and the native inference sessions while idle; everything
    /// reloads lazily on the next search. The index must be dropped explicitly because the searcher
    /// itself stays reachable from the UI for as long as the window is open.
    /// </summary>
    public void Unload()
    {
        _index = null;
        (_provider as IUnloadable)?.Unload();
        (_reranker as IUnloadable)?.Unload();
    }

    public async Task<List<SearchHit>> SearchAsync(string rawQuery, SearchMode mode, int top, CancellationToken ct)
    {
        var query = QueryParser.Parse(rawQuery);
        var allowed = _store.FilterRowIds(query);
        var candidates = Math.Max(_config.CandidateCount, top);

        var keywordRanking = new List<long>();
        var keywordSnippets = new Dictionary<long, string>();
        var vectorRanking = new List<long>();
        var vectorSnippets = new Dictionary<long, string>();

        if (!query.HasText)
        {
            // Filter-only query: newest first among the allowed rows.
            var rows = _store.GetMessages(allowed ?? []).Values.OrderByDescending(m => m.Received).Take(top).ToList();
            return rows.Select((m, i) => new SearchHit(i + 1, 0, m, Truncate(m.Body, 160), null, null)).ToList();
        }

        if (mode is SearchMode.Hybrid or SearchMode.Keyword or SearchMode.Rerank)
        {
            var hits = _store.FullTextSearch(query.ToFtsQuery(), allowed, candidates);
            if (hits.Count == 0 && query.Terms.Count + query.Phrases.Count > 1)
                hits = _store.FullTextSearch(query.ToFtsQuery(anyTerm: true), allowed, candidates);
            foreach (var h in hits)
            {
                keywordRanking.Add(h.RowId);
                keywordSnippets[h.RowId] = h.Snippet;
            }
        }

        if (mode is SearchMode.Hybrid or SearchMode.Vector or SearchMode.Rerank)
        {
            var (ranking, bestChunk) = await VectorSearchAsync(query.SemanticText, allowed, candidates, ct);
            vectorRanking = ranking;
            foreach (var (rowId, chunkId) in bestChunk)
                vectorSnippets[rowId] = StripChunkHeader(_store.GetChunkText(chunkId) ?? "");
        }

        var fused = mode switch
        {
            SearchMode.Keyword => keywordRanking.Select((id, i) => (Id: id, Score: 1.0 / (i + 1))).ToList(),
            SearchMode.Vector => vectorRanking.Select((id, i) => (Id: id, Score: 1.0 / (i + 1))).ToList(),
            _ => RankFusion.ReciprocalRank([(keywordRanking, 1.0), (vectorRanking, VectorWeightFor(query))], _config.RrfK),
        };

        if (mode is SearchMode.Rerank)
            fused = await RerankAsync(query.SemanticText, fused, vectorSnippets, ct);

        var topIds = fused.Take(top).ToList();
        var messages = _store.GetMessages(topIds.Select(f => f.Id));
        var results = new List<SearchHit>();
        foreach (var (id, score) in topIds)
        {
            if (!messages.TryGetValue(id, out var m)) continue;
            var kRank = keywordRanking.IndexOf(id);
            var vRank = vectorRanking.IndexOf(id);
            var snippet = keywordSnippets.TryGetValue(id, out var ks) && ks.Length > 0 ? ks
                : vectorSnippets.TryGetValue(id, out var vs) ? Truncate(vs, 220)
                : Truncate(m.Body, 160);
            results.Add(new SearchHit(results.Count + 1, score, m, snippet.ReplaceLineEndings(" "), kRank < 0 ? null : kRank + 1, vRank < 0 ? null : vRank + 1));
        }
        return results;
    }

    /// <summary>Re-scores the top fused candidates with the cross-encoder; deeper candidates keep their fusion order.</summary>
    private async Task<List<(long Id, double Score)>> RerankAsync(
        string queryText, List<(long Id, double Score)> fused, Dictionary<long, string> bestChunks, CancellationToken ct)
    {
        if (_rerankerFactory is null)
            throw new InvalidOperationException("No reranker is configured (see rerank.onnx in config.json).");
        var candidates = fused.Take(Math.Max(_rerankDepth, 1)).ToList();
        if (candidates.Count == 0) return fused;

        _reranker ??= await _rerankerFactory(ct);
        var messages = _store.GetMessages(candidates.Select(c => c.Id));
        var passages = candidates.Select(c =>
        {
            messages.TryGetValue(c.Id, out var m);
            var text = bestChunks.TryGetValue(c.Id, out var chunk) && chunk.Length > 0 ? chunk : m?.Body ?? "";
            var subject = m?.Subject ?? "";
            var passage = subject.Length > 0 ? subject + "\n" + text : text;
            return passage.Length <= MaxPassageChars ? passage : passage[..MaxPassageChars];
        }).ToList();

        var scores = await _reranker.ScoreAsync(queryText, passages, ct);
        var reranked = candidates
            .Select((c, i) => (c.Id, Score: (double)scores[i]))
            .OrderByDescending(x => x.Score)
            .ToList();
        reranked.AddRange(fused.Skip(candidates.Count));
        return reranked;
    }

    private async Task<(List<long> Ranking, Dictionary<long, long> BestChunk)> VectorSearchAsync(
        string text, HashSet<long>? allowed, int limit, CancellationToken ct)
    {
        _provider ??= await _embedder(ct);
        var storedModel = _store.GetMeta("embedding_model");
        if (storedModel is not null && storedModel != _provider.ModelId)
            throw new InvalidOperationException($"Index was embedded with '{storedModel}' but the configured model is '{_provider.ModelId}'. Run 'embed --reset'.");

        _index ??= _store.LoadEmbeddings(_provider.Dimensions);
        if (_index.Count == 0) return ([], []);

        var q = await _provider.EmbedQueryAsync(text, ct);
        var scores = new float[_index.Count];
        var dims = _index.Dimensions;
        var data = _index.Data;
        Parallel.For(0, _index.Count, i => scores[i] = TensorPrimitives.Dot(q, data.AsSpan(i * dims, dims)));

        // best chunk per message
        var best = new Dictionary<long, (float Score, long ChunkId)>();
        for (var i = 0; i < _index.Count; i++)
        {
            var msg = _index.MessageRowIds[i];
            if (allowed is not null && !allowed.Contains(msg)) continue;
            if (!best.TryGetValue(msg, out var cur) || scores[i] > cur.Score)
                best[msg] = (scores[i], _index.ChunkIds[i]);
        }

        var ranked = best.OrderByDescending(kv => kv.Value.Score).Take(limit).ToList();
        return (ranked.Select(kv => kv.Key).ToList(), ranked.ToDictionary(kv => kv.Key, kv => kv.Value.ChunkId));
    }

    /// <summary>Quoted identifiers ("INV-20431", "SAS13524") trust exact matches more; everything else keeps the configured balance.</summary>
    private double VectorWeightFor(ParsedQuery query) =>
        QueryHeuristics.ContainsIdentifier(query) ? _config.VectorWeight * _config.IdentifierVectorWeightFactor : _config.VectorWeight;

    private static string StripChunkHeader(string chunk)
    {
        if (!chunk.StartsWith("Subject:", StringComparison.Ordinal)) return chunk;
        var idx = chunk.IndexOf("\n\n", StringComparison.Ordinal);
        return idx > 0 ? chunk[(idx + 2)..] : ""; // header-only chunk = message without body text
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
