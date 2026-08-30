using System.Numerics.Tensors;
using MailSearch.Config;
using MailSearch.Embeddings;
using MailSearch.Storage;

namespace MailSearch.Search;

public enum SearchMode { Hybrid, Keyword, Vector }

public sealed record SearchHit(
    int Rank, double Score, MessageRow Message, string Snippet, int? KeywordRank, int? VectorRank);

/// <summary>Keyword (FTS5) + dense retrieval fused with RRF, with structured filters applied first.</summary>
public sealed class HybridSearcher
{
    private readonly SearchStore _store;
    private readonly SearchConfig _config;
    private readonly Func<CancellationToken, Task<IEmbeddingProvider>> _embedder;
    private EmbeddingIndex? _index;
    private IEmbeddingProvider? _provider;

    public HybridSearcher(SearchStore store, SearchConfig config, Func<CancellationToken, Task<IEmbeddingProvider>> embedder)
    {
        _store = store;
        _config = config;
        _embedder = embedder;
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

        if (mode is SearchMode.Hybrid or SearchMode.Keyword)
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

        if (mode is SearchMode.Hybrid or SearchMode.Vector)
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

    /// <summary>Identifier-like queries ("INV-20431", "SAS13524") trust exact matches more; concept queries keep the configured balance.</summary>
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
