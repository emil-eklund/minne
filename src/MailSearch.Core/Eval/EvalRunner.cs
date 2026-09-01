using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using MailSearch.Search;
using MailSearch.Storage;

namespace MailSearch.Eval;

public sealed class EvalCase
{
    public required string Query { get; set; }
    /// <summary>Graph ids, Internet-Message-Ids or "rowid:N" of the messages that should be found.</summary>
    public List<string> Expected { get; set; } = [];
    public string? Note { get; set; }
}

public sealed class EvalSet
{
    public string? Description { get; set; }
    public List<EvalCase> Queries { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static EvalSet Load(string path) => JsonSerializer.Deserialize<EvalSet>(File.ReadAllText(path), Json) ?? new EvalSet();
    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
}

public sealed record QueryResult(EvalCase Case, int? Rank, double Milliseconds, bool Unresolvable);

public sealed record ModeResult(SearchMode Mode, IReadOnlyList<QueryResult> Results)
{
    public int Total => Results.Count(r => !r.Unresolvable);
    public double RecallAt(int k) => Total == 0 ? 0 : Results.Count(r => r.Rank is { } rank && rank <= k) / (double)Total;
    public double Mrr => Total == 0 ? 0 : Results.Where(r => !r.Unresolvable).Sum(r => r.Rank is { } rank ? 1.0 / rank : 0) / Total;
    public double AvgMs => Results.Count == 0 ? 0 : Results.Average(r => r.Milliseconds);
}

/// <summary>Runs an evaluation set through each retrieval mode and reports recall@k / MRR.</summary>
public sealed class EvalRunner
{
    private readonly HybridSearcher _searcher;
    private readonly SearchStore _store;

    public EvalRunner(HybridSearcher searcher, SearchStore store)
    {
        _searcher = searcher;
        _store = store;
    }

    public async Task<List<ModeResult>> RunAsync(EvalSet set, IEnumerable<SearchMode> modes, int top, CancellationToken ct,
        Action<SearchMode, int, int>? progress = null)
    {
        var results = new List<ModeResult>();
        foreach (var mode in modes)
        {
            var perQuery = new List<QueryResult>();
            foreach (var c in set.Queries)
            {
                progress?.Invoke(mode, perQuery.Count + 1, set.Queries.Count);
                var expected = c.Expected.Select(_store.FindMessageRowId).Where(r => r is not null).Select(r => r!.Value).ToHashSet();
                if (expected.Count == 0)
                {
                    perQuery.Add(new QueryResult(c, null, 0, Unresolvable: true));
                    continue;
                }
                var sw = Stopwatch.StartNew();
                var hits = await _searcher.SearchAsync(c.Query, mode, top, ct);
                sw.Stop();
                var rank = hits.FirstOrDefault(h => expected.Contains(h.Message.RowId))?.Rank;
                perQuery.Add(new QueryResult(c, rank, sw.Elapsed.TotalMilliseconds, false));
            }
            results.Add(new ModeResult(mode, perQuery));
        }
        return results;
    }
}
